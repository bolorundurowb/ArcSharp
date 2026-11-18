# Memory model

ArcSharp has no garbage collector. Every object on the heap carries a count of
the strong references that point at it. When that count reaches zero, the
object is destroyed straight away and its memory is returned. This page
describes the compiler as it is today. The planned lifetime pass, which skips
those counts when a lifetime can be proved, is summarised under
[Performance](#performance) and specified in the [Roadmap](roadmap.md).

This page explains what that means for the code you write: when objects die,
why reference cycles leak, and how to break a cycle with `WeakReference<T>`.
For the compiler's internal rules, see [Architecture](architecture.md#4-the-arc-model-ownership-rules).

## What counts as a reference

Objects of class types, strings, arrays, and `WeakReference<T>` objects live on
the heap and are reference counted. Values of `int`, `bool`, `double`, and the
other numeric types are not; they are copied, and they never affect a count.

Structs are value types as well. A struct value lives inline in the local,
field, or parameter that holds it, and copying it copies its fields. If a
struct contains a reference field, the copy retains the target and the original
value keeps its own reference. The struct itself has no reference count; only
the objects its fields point at are counted.

A **strong reference** keeps its target alive. Every reference-typed local,
parameter, field, static field, and array element is a strong reference.

A **weak reference** points at an object without keeping it alive. The only
way to make one is `System.WeakReference<T>`.

## When an object dies

The runtime releases a strong reference in these situations:

- **A method returns.** Every reference-typed local in the method is released.
  This happens on every `return`, not at the end of an inner block. A local
  declared inside an `if` keeps its object alive until the whole method returns.
- **A variable, field, or array element is overwritten.** The object it pointed
  at is released at the moment of the assignment.
- **The owner dies.** When an object is destroyed, it releases every object its
  fields point at, and an array releases every element. Destruction therefore
  cascades through a tree of objects.
- **A struct value is overwritten or goes out of scope.** Overwriting a struct
  releases the reference fields of the old value before copying the new one. A
  struct local is dropped when the method returns, and a struct temporary at the
  end of its statement.
- **A temporary is finished with.** An object created inside an expression and
  not stored anywhere, such as an intermediate string in `"a" + b + "c"`, is
  released at the end of the statement.

Reassignment releases the old object immediately:

```csharp
using System;

class Node
{
    public int value;
}

class Watcher
{
    public WeakReference<Node> target;

    public void Report()
    {
        if (target.TryGetTarget(out Node seen))
        {
            Console.WriteLine("alive: " + seen.value);
        }
        else
        {
            Console.WriteLine("released");
        }
    }
}

class Program
{
    static void Main()
    {
        Watcher watcher = new Watcher();
        Node first = new Node();
        first.value = 1;
        watcher.target = new WeakReference<Node>(first);
        watcher.Report();      // alive: 1

        first = new Node();    // the first Node has no strong references left
        watcher.Report();      // released
    }
}
```

Built with `dotnet run`, the same program prints `alive: 1` twice, because the
first object stays in memory until the garbage collector happens to run. Under
ArcSharp the answer is the same every time.

## Finalisation

Finalisation is a deliberate difference from the CLR. ArcSharp is a separate
runtime with its own compatibility profile. It is not a replacement garbage
collector for the existing CLR, and it does not pretend that `GC` means the
same thing in both.

The contract is:

1. When an object's strong count reaches zero, its finaliser runs
   synchronously, on the thread that released the last strong reference.
2. The object is then torn down and its memory reclaimed, once any outstanding
   weak-reference bookkeeping has finished. A weak reference does not keep the
   object alive, and it does not by itself keep the allocation after that
   bookkeeping.
3. `WeakReference<T>` never contributes to the strong count.
   `TryGetTarget` is false as soon as the strong count is zero.
4. `Dispose()` is not a finaliser. `IDisposable` and `IAsyncDisposable` stay
   the programmer's responsibility. A finaliser does not call `Dispose`, and
   `using` does not suppress or replace a finaliser.
5. There is no finaliser thread, no finalisation queue, and no
   stop-the-world tracing collector.

The finaliser runs when the last strong reference is released. That is not the
same event as a variable leaving lexical scope. A local that is stored into a
field, returned, or otherwise still reachable is not finalised at the end of
the block that declared it. A local that never escapes is finalised when that
method releases its locals, which is during the method's cleanup, not on a
later collection.

```csharp
public sealed class Resource
{
    ~Resource()
    {
        Console.WriteLine("Resource finalized");
    }
}

void Execute()
{
    var resource = new Resource();
}
```

If `resource` does not escape, `Resource` is finalised as `Execute` cleans up,
because that cleanup releases the last strong reference. On the CLR the same
finaliser would wait for an unspecified future collection.

Order inside one object:

- The finaliser runs before the object's own strong references are released, so
  it can still use its children.
- A derived finaliser runs, then the base finaliser. The most derived body
  runs first.
- Children are released after that chain returns. A child whose strong count
  falls to zero is finalised at that release, so a child's finaliser runs
  after its parent's finaliser.
- Two objects that die because of unrelated releases are ordered only by those
releases. The compiler may delete a redundant retain or release. Deleting the
count traffic does not delete the finaliser. A proved lifetime still runs the
finaliser at the point where that lifetime ends, which is the point that would
have dropped the last strong reference. The compiler must not move the
finaliser past that point.

A finaliser that throws does not stop teardown and does not propagate to the
code that dropped the last reference. The runtime reports the exception and
continues with field release and reclamation. The object cannot be resurrected
by the exception. Until the language accepts `throw`, a finaliser cannot throw
because `throw` itself is rejected.

Resurrection is prohibited. A finaliser must not store `this`, or a reference
derived from it, into any location that outlives the finaliser. The compiler
rejects that store. If a finaliser returns with the strong count above zero,
the runtime aborts the process. It does not keep the object, and it does not
run the finaliser again.

`GC.SuppressFinalize` sets a flag on the object. The flag skips the finaliser
at the last strong release. It does not skip field release or reclamation, and
it does not keep the object alive. Calling it while the finaliser is already
running does not abort the chain that has started. `GC.ReRegisterForFinalize`
is rejected. `GC.Collect` and `GC.WaitForPendingFinalizers` are accepted and
do nothing, because there is nothing to collect and no queue to wait for. Each
is reported with info `ARC3200`, so the difference from the CLR is visible.

Deep graphs are released with a worklist. Tearing a child down must not recurse
on the call stack that is already running a finaliser. The runtime today still
releases fields recursively. The worklist arrives with user finalisers.

Objects in a strong cycle never reach a zero count, so their finalisers never
run. Breaking the cycle with `WeakReference<T>` is what allows finalisation.

| Feature | Contract | Compiler today |
|---|---|---|
| C# finaliser (`~ClassName()`) | Supported. Runs at the last strong release, on that thread. | Rejected (`ARC3001`). |
| Finaliser inheritance | Supported. Derived body, then base body. | Not accepted. |
| `GC.SuppressFinalize` | Supported. Skips the finaliser only. | Not implemented. |
| `GC.ReRegisterForFinalize` | Unsupported. Rejected. | Not implemented. |
| Object resurrection | Unsupported. A store of `this` out of the finaliser is rejected. A strong count above zero when the finaliser returns aborts the process. | Not applicable yet. |
| `GC.Collect` | Documented no-op, with info `ARC3200`. | Not in the supported library. |
| `GC.WaitForPendingFinalizers` | Documented no-op, with info `ARC3200`. | Not in the supported library. |
| `IDisposable` | Separate from finalisation. `Dispose()` runs only when the program calls it, including through `using`. | `using` is supported, with the limits in the [language reference](language-reference.md#using-and-idisposable). `Dispose` is structural. The compiler has no library `IDisposable` type. The reference assembly declares one so the Roslyn frontend can bind `using`. |
| `IAsyncDisposable` | The same separation. `await using` disposes at the end of the statement. It is not a finaliser. | Not supported. `async` is not implemented. |
| `WeakReference<T>` | Does not keep the target alive. The allocation outlives the object only for weak bookkeeping. | Supported. |

What runs today, for every class, is a generated teardown. It releases strong
reference fields and drops struct fields when the strong count reaches zero.
It is not a user finaliser, and there is no `~T()` body in it. The compiler
still rejects a finaliser. The contract above is the semantics that teardown
will grow into. It is scheduled in [M4](roadmap.md#m4--deterministic-finalisers) of the Roadmap.

## Why cycles leak

This is an architectural decision. A cycle that the compiler can see in the
type declarations produces warning `ARC3100`. The program is still accepted.
If you ignore the warning, or a library you use ignores it, the cycle leaks.
ArcSharp will not collect it later. `WeakReference<T>` is how the cycle is
broken. The opt-in leak report is how a leak that got through is named.

Reference counting cannot reclaim a group of objects that refer to each other.
Each one keeps the next alive, so none of their counts ever reaches zero, even
after the rest of the program has forgotten them. A finaliser on one of those
objects does not run, because the count never reaches the point that would
call it.

```csharp
class Parent
{
    public Child child;
}

class Child
{
    public Parent owner;    // strong: Parent -> Child -> Parent
}

class Program
{
    static void Link()
    {
        Parent parent = new Parent();
        Child child = new Child();
        parent.child = child;
        child.owner = parent;
    }

    static void Main()
    {
        Link();             // both objects are unreachable after this call
    }
}
```

The accounting line shows the leak. Both objects are still alive at exit:

```text
[arc] alloc=2 dead=0 freed=0 live=2
```

`samples/cycle_strong.cs` is the same program with one line of output, and the
test harness asserts that it leaks, to document the limitation.

Common shapes that form cycles:

- A child that points back at its parent.
- A doubly linked list, where each node has `next` and `previous`.
- An observer that holds the object it observes, while that object holds a
  list of its observers.
- Any object that stores a reference to itself, directly or through others.

A tree in which references only point downwards, and a singly linked list, do
not leak.

## Breaking a cycle with `WeakReference<T>`

Make one edge of the cycle weak. The usual choice is the reference that points
back up, towards the owner:

```csharp
using System;

class Parent
{
    public Child child;
}

class Child
{
    public WeakReference<Parent> owner;
}

class Program
{
    static void Link()
    {
        Parent parent = new Parent();
        Child child = new Child();
        parent.child = child;
        child.owner = new WeakReference<Parent>(parent);
    }

    static void Main()
    {
        Link();
        Console.WriteLine("linked");
    }
}
```

```text
linked
[arc] alloc=4 dead=4 freed=4 live=0
```

When `Link` returns, `parent` loses its last strong reference and is destroyed.
That releases `child`, which in turn releases its `WeakReference<Parent>`
object. Nothing leaks.

The fourth allocation is the `WeakReference<Parent>` object itself, which is a
small heap object that the `Child` owns.

### Reading through a weak reference

Call `TryGetTarget`. It returns `true` and gives you a strong reference if the
target is still alive, and returns `false` if it has been destroyed:

```csharp
class Child
{
    public WeakReference<Parent> owner;

    public void Describe()
    {
        if (owner.TryGetTarget(out Parent parent))
        {
            Console.WriteLine("owned by " + parent.name);
        }
        else
        {
            Console.WriteLine("orphan");
        }
    }
}
```

The `parent` local is a strong reference. It keeps the parent alive until
`Describe` returns, so the parent cannot be destroyed while you are using it.

`SetTarget(x)` points an existing weak reference at a different object.

### Rules for weak references

- `WeakReference<T>` must be the type of a field. A local variable of this type
  does not compile yet; see [Known issues](language-reference.md#known-issues).
- `T` must be a class.
- Under ArcSharp, `TryGetTarget` returns `false` as soon as the last strong
  reference is gone. Under .NET it usually returns `true` for a while longer.
  Code that is correct under ArcSharp is also correct under .NET, but the
  reverse is not always true.
- Older samples use a non-standard `weak` field modifier
  (`public weak Parent owner;`). It still works, but it is not valid C#, so
  Roslyn rejects it. Use `WeakReference<T>` in new code.

## Finding leaks

Every program prints its accounting line when `Main` returns:

```text
[arc] alloc=A dead=D freed=F live=L
```

`live` is the number of objects that were never destroyed. If it is not zero,
set the environment variable `ARC_LEAKS` to `1` and run the program again. The
accounting line is unchanged. After it, the runtime prints one line for each
object that is still alive:

```text
[arc] survivor addr=<address> type=<type> strong=<n> weak=<n> site=<file:line> root=static|unreachable
```

`root=static` means a static field still reaches the object, so it is retained
on purpose. `root=unreachable` means nothing the program can still name reaches
it, which is a leak. When unreachable objects form a strong cycle, a further
line names the types around that cycle. `samples/cycle_strong.cs` prints a
report of this shape (the address changes from run to run):

```text
[arc] alloc=3 dead=1 freed=1 live=2
[arc] survivor addr=000001C80194F040 type=Child strong=1 weak=1 site=cycle_strong.cs:11 root=unreachable
[arc] survivor addr=000001C80194EE00 type=Parent strong=1 weak=1 site=cycle_strong.cs:10 root=unreachable
[arc] cycle Parent -> Child -> Parent
```

`site` is the source file and line of the `new` expression, the array
creation, the string literal, or the `WeakReference<T>` constructor. Strings
built by concatenation or by printing a number are reported as `<runtime>`.
The report does not free the objects. A cycle that is still reachable from a
static field is classified as `root=static` and is not printed as an
unreachable cycle.

With that report:

1. Make an unreachable back-reference a `WeakReference<T>`.
2. Run the program again and check that `live=0`, and that `ARC_LEAKS=1`
   prints no `survivor` lines.

`ARC3100` is that warning. It looks at declared field types, not at one object
graph, so a tree or a singly linked list warns too. A cycle the warning cannot
see, or a warning that was ignored in this program or in a library, still
leaks. See [Diagnostics](diagnostics.md#arc3100).

Static fields are a special case. An object held by a static field is still
alive when `Main` returns, so it counts towards `live` and the leak report
marks it `root=static`. `samples/leaks.cs` keeps one object that way and also
builds two unreachable cycles.

## Threads

Reference counts are updated without atomic instructions, so ArcSharp is safe
only for single-threaded programs. Threads are not supported, and sharing
objects between threads would corrupt the counts. Threads are outside the
compatibility target and are listed under
[Not scheduled](roadmap.md#not-scheduled) in the Roadmap.

## Managed references

A `ref`, `out`, or `in` argument, and a struct method's `this`, is an interior
pointer that lasts only for that call. When the slot lives inside a heap object
— a class field, a field of an inline struct, or an array element — the caller
retains that object until the call returns. The callee can clear every variable
and field that named the object. The hidden retain keeps the storage alive and
is released afterwards. A local, a by-value parameter, a static field, and a
`ref` parameter borrowed from the caller need no extra retain, because that
storage already outlives the call.

A managed reference that would outlive the call is rejected. Ref locals and
ref returns are [`ARC3001`](diagnostics.md#arc3001). A weak field cannot be
passed by `ref`, `out`, or `in` ([`ARC2015`](diagnostics.md#arc2015)), because a
store through that pointer would not use the weak primitives. An `in`
parameter is read-only, including the fields of an inline struct. Assigning
that storage, passing it as `ref` or `out`, or calling an instance method on
an `in` struct is also `ARC2015`. A reference loaded from that struct is a
different object and may still be written. Heap-backed spans, and any other
escaping interior reference, stay rejected until a proof can cover a lifetime
longer than one call. Stack confinement alone does not keep heap storage alive.

## Planned safety work

The opt-in leak report above names surviving objects and unreachable cycles.
It does not prove that a program is free of leaks when it is switched off, and
a static `ARC3100` warning still cannot prove that a cycle will happen at run
time. Allocation stacks are not part of the report yet.

Future closures will preserve C# strong captures. Capturing an object alone
does not form a cycle, but a strong path back to the closure can do so. The
cycle decision is closed: `ARC3100` warns, the cycle stays legal, and an
ignored warning leaks, including inside a library. `WeakReference<T>` is how a
back-edge is broken. There is no tracing collector. Closures,
events, and async wait until the lifetime pass exists.

Atomic counts alone will not make threading safe. Loading a shared pointer
and then retaining it can race with its final release. Threading also requires
safe shared-reference acquisition, weak promotion, synchronisation, and
reclamation. The runtime remains single-threaded until that protocol is verified.

## Performance

Every read of a reference into a local or temporary increments its count, and
the matching release decrements it later. This is the compiler's behaviour
today. It is simple to prove, and it does more work than the planned model.

The [lifetime pass](roadmap.md#m3--lifetime-pass) will skip a
retain and its release when it can prove the lifetime, and it will keep using
the count everywhere else. The object header does not change. A value that
escapes into a field, an array, or a call the compiler cannot see stays
reference-counted on the same object. That pass is not in the compiler yet.
Until it is, every heap object is reference-counted, there are no collection
pauses, and memory is returned when the last strong reference goes away.
