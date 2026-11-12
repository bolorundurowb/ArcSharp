# Memory model

ArcSharp has no garbage collector. Every object on the heap carries a count of
the strong references that point at it. When that count reaches zero, the
object is destroyed straight away and its memory is returned.

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

ArcSharp has no user-defined destructors or finalisers yet. The runtime
generates a destructor for each class that releases its reference fields and
drops its struct fields, and that is the only code that runs when an object
dies.

## Why cycles leak

Reference counting cannot reclaim a group of objects that refer to each other.
Each one keeps the next alive, so none of their counts ever reaches zero, even
after the rest of the program has forgotten them.

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

`live` is the number of objects that were never destroyed. If it is not zero:

1. Look for classes whose fields can lead back to themselves, directly or
   through other classes.
2. Make the back-reference a `WeakReference<T>`.
3. Run the program again and check that `live=0`.

The compiler warns about possible cycles as `ARC3100` when a set of classes can
reach each other through strong fields. The warning is about declared types,
not a particular object graph, so a tree or a singly linked list warns too. See
[Diagnostics](diagnostics.md#arc3100) and
[Roadmap section 7](roadmap.md#7-roslyn-analyser--warnings-inside-an-ordinary-c-project).

Static fields are a special case. An object held by a static field is still
alive when `Main` returns, so it counts towards `live`.

## Threads

Reference counts are updated without atomic instructions, so ArcSharp is safe
only for single-threaded programs. Threads are not supported, and sharing
objects between threads would corrupt the counts. Atomic counts are on the
[Roadmap](roadmap.md#2-arc-model--from-correct-to-efficient).

## Performance

Every read of a reference into a local or temporary increments its count, and
the matching release decrements it later. This is deliberately simple and easy
to prove correct, but it does more work than necessary. Removing redundant
pairs is planned as a separate compiler pass. In exchange, there are no
collection pauses, and memory is returned as soon as it is no longer used.
