# Deterministic finalisers

A class can declare `~T()`. The finaliser runs at the moment the last strong
reference is released, on the thread that released it. There is no finaliser
thread and no queue. The contract is in the
[memory model](../memory-model.md#finalisation). This page records how it is
built and why.

## What a program can do

```csharp
class Base
{
    ~Base() { Console.WriteLine("Base"); }
}

class Derived : Base
{
    ~Derived() { Console.WriteLine("Derived"); }
}
```

- `~T()` is accepted on classes. Dropping the last reference to a `Derived`
  prints `Derived`, then `Base`. The most derived body runs first.
- The finaliser runs before the object's own fields are released, so it can
  still read its children and call methods on them.
- `GC.SuppressFinalize(obj)` skips the finaliser for that object.
- `GC.Collect()` and `GC.WaitForPendingFinalizers()` compile and do nothing.
  Each reports info `ARC3200`, which does not fail the build, so the
  difference from the CLR is visible.
- `GC.ReRegisterForFinalize` is rejected with `ARC3001`.
- A finaliser that stores `this` somewhere that outlives it is rejected with
  `ARC2016`. If the compiler cannot see the store, the runtime stops the
  program instead.

## How it works

### The destruction path

`arc_release` calls `arc_destroy` when the strong count reaches zero.
`arc_destroy` does the following for each dying object:

1. Run the finaliser chain, unless `GC.SuppressFinalize` was called.
2. Run the generated teardown (`<Type>__deinit`), which releases the fields.
3. Count the object as dead and release its implicit weak reference. The
   memory is freed when the weak count reaches zero.

The generated teardown is the same one every class already had. It is not
the finaliser, and a class with no `~T()` runs no user code when it dies.

### The finaliser chain

Each class that declares `~T()` gets a function that runs its own body and
then calls the nearest inherited chain. The type descriptor has one new field,
`finalizer`, that points at the nearest chain in the class or its bases, or at
null when there is none. The runtime makes one indirect call, and the order
(derived, then base) is fixed at compile time. A class with no finaliser
anywhere in its hierarchy pays for one null check.

`TypeInfo` grew from ten fields to eleven. The new field is last, so the
existing positional initialisers in the runtime stayed valid.

### The worklist

Before this change, `arc_release` recursed into each field. A list of a
hundred thousand nodes overflowed the C stack. A finaliser makes that worse,
because a finaliser can run user code in the middle of a recursive walk.

Now a release that reaches zero during teardown is pushed onto a worklist
instead of being destroyed on the spot. `arc_destroy` loops: pop an object,
finalise it, tear it down, and then push the children that died. The
children are pushed in reverse so that they pop in field order. That keeps
the destruction order identical to the old recursive walk, so no existing
accounting changed.

A release made by user code is different. When a finaliser sets a field to
`null`, the object it held must be finalised at that statement, not later.
Such a release drains the worklist straight away, above the entries that were
already waiting. The `finaliser_release_point` sample checks this.

### Flags in the weak word

`GC.SuppressFinalize` needs a per-object flag, and the runtime needs to know
that a finaliser is running. Adding a header slot would move every field
offset in the emitter, the symbols and the runtime. Instead the weak word at
offset 8 is packed. Its low 32 bits are the weak count. Bit 32 means
"finalisation suppressed" and bit 33 means "finaliser running". The emitter
never reads or writes that word, so no offset changed and the header is still
three slots.

### The guard count

While the chain runs, the runtime sets the strong count to one. Without this,
a finaliser that stores `this` in a local, passes it to a method, and lets it
go would drop the count back to zero and start the destruction again, in the
middle of the finaliser. With the guard, those retains and releases balance.
When the chain returns the runtime subtracts the guard. A result above zero
means the object was stored somewhere that outlives the finaliser, which is
resurrection.

`arc_load_weak` returns null while the running flag is set, so
`WeakReference<T>.TryGetTarget` is `false` inside a finaliser. Otherwise a
weak reference could hand out a new strong reference to a dying object.

## Resurrection

Resurrection is not supported. There are two checks.

**At compile time (`ARC2016`).** The check is syntactic, in
`src/Frontend/RoslynRules.cs`. It reports an assignment inside a finaliser
when both of these hold:

- the value is `this`, a conversion of `this`, or a local variable that holds
  one of those, and
- the target is a field, a property or an array element.

The check is deliberately narrow. A wider analysis would reject correct
programs, and the compiler already has to follow the runtime check for
anything it misses.

**At run time.** A strong count above zero after the chain returns prints
`ResurrectionException: a finaliser of <Type> kept a reference to the object
it was finalising`, prints the `[arc]` line, and exits with code 70, like the
other runtime failures. The runtime does not try to keep the object.

## The GC calls

`GC` is declared in the reference assembly (`ref/ArcSharp.References`) so that
Roslyn can bind it. It has `SuppressFinalize`, `ReRegisterForFinalize`,
`Collect` and `WaitForPendingFinalizers`. It has no `KeepAlive`, which would
be a different promise, and a use of it is a normal C# error.

- `SuppressFinalize` lowers to a bound `GcCall` node and then to one runtime
  call. A value-type argument reports `ARC3001`, because boxing is not
  supported.
- `Collect` and `WaitForPendingFinalizers` lower to the same node and emit
  nothing. They report `ARC3200` as an info message. The Roslyn analyser
  reports the same code, so a user who has only the analyser still sees it.
- `ReRegisterForFinalize` reports `ARC3001`. Finalisation that can be turned
  back on would let an object die twice.

The lifetime pass does not treat the argument of a `GC` call as a read. A local
that is passed to `SuppressFinalize` is therefore never treated as confined
to its method.

## Limits

- A finaliser cannot throw, because `throw` is not accepted until
  [M5](../roadmap.md#m5--exceptions). The contract for a throwing finaliser
  is written in the memory model and arrives with M5.
- Objects held by static fields are never released, so their finalisers never
  run. `Main` returning does not finalise them. The accounting line counts
  them as `live`.
- Objects in a strong cycle never reach zero, so their finalisers never run.
  `ARC3100` still warns about a visible cycle.
- Finalisers on structs do not exist, because C# does not allow them.

## How it is verified

Every sample runs with `ARC_CHECK=1`, so a header that is wrong, a double
free or a leak fails the run. The harness covers:

| Check | Sample |
|---|---|
| Derived before base across a four-level chain, and a parent before its children | `finaliser_order` |
| A finaliser runs at the release statement and not at the end of the method | `finaliser_release_point` |
| `GC.SuppressFinalize` called from `Dispose`, a finaliser that still runs for an object that was not disposed, and the `ARC3200` messages | `finaliser_suppress` |
| Weak references read as dead inside a finaliser, a child released by a finaliser is finalised at that statement, array elements finalise in index order, and a generic class finalises for each closed type | `finaliser_graph` |
| A list of 100,000 nodes, which overflowed the stack before the worklist | `finaliser_deep` |
| A strong cycle never finalises, and still reports `ARC3100` | `finaliser_cycle` |
| `ARC2016` for each visible form of resurrection | `negative/finaliser_resurrect` |
| `ARC3001` for `GC.ReRegisterForFinalize` | `negative/gc_reregister` |
| The runtime abort, exit code 70, and no output after it | `negative/finaliser_resurrect_call` |

The analyser tests cover `ARC3200`, and the reference-assembly check covers
what Roslyn accepts and rejects about `GC` and finalisers. The
[verification page](../verification.md) has the counts from the latest run.
The [GC comparison](gc-comparison.md) has the memory and pause numbers after
this milestone.
