# Iterators

ArcSharp supports iterator methods and property getters. A member that returns
`IEnumerable<T>`, `IEnumerator<T>`, `IEnumerable` or `IEnumerator` and contains
`yield return` or `yield break` is compiled into a small class that produces
the values one at a time. The [iterators sample](../../samples/iterators.cs) and
the [regions sample](../../samples/iterators_regions.cs) check the behaviour.

## What works

- `yield return` and `yield break` in loops, `if`, `switch`, `try`/`finally`,
  `using` and `foreach`.
- Instance and static iterators, iterators in generic types, and generic
  iterator methods.
- Locals of any supported type across a `yield`, including structs, arrays,
  strings and lambdas that capture locals.
- Enumerating one `IEnumerable<T>` more than once. Each call to
  `GetEnumerator` after the first one gets a fresh copy that starts from the
  beginning with the original arguments.
- `finally` blocks and `using` cleanup run when the enumeration is disposed
  early, for example by `break` in a `foreach`, as they do in .NET.
- An exception thrown inside the iterator reaches the code that called
  `MoveNext`.

## What is not supported

- Iterators in structs, and `yield` in a local function. Both report `ARC3001`.
- `ref` and `out` parameters on an iterator. Roslyn rejects these (`CS1623`).
- Reading the untyped `IEnumerator.Current`. It throws `NotSupportedException`,
  because returning a value as `object` needs boxing, which ArcSharp does not
  support. An iterator that returns `IEnumerable` or `IEnumerator` compiles and
  moves through its values, but its elements cannot be read. Use the generic
  interfaces.

## How it works

The iterator method becomes a factory. It creates an object of a generated
class and returns it. The object derives from `IteratorBase<T>` in
`corelib/Iterators.cs`, which implements `IEnumerable<T>` and `IEnumerator<T>`
and keeps the current state number and the current value.

The body of the iterator is lowered into one `Step` method on the generated
class. `Step` is a resumable method:

1. On entry it jumps to a label that follows the `yield` where the iterator
   last stopped, using the state number.
2. A `yield return` stores the value, saves the state number and every local
   into fields of the object, and returns `true` without running any cleanup.
3. After the label, the locals are copied back from the fields.
4. `yield break` and the end of the body set the state to finished and return
   `false`.

### Why every local is saved and restored

The simplest correct design keeps the body as ordinary lowered code. Saving all
locals at each `yield` means no other part of the compiler needs to know that
locals live in an object. `using`, `foreach`, patterns and lambdas work
without change. The cost is some extra copying, and a saved reference stays
alive until the next save or until the iterator object is released. That is
safe under ARC. Saving only the locals that are live across a `yield` is a
possible optimisation, listed in the roadmap.

### `finally` blocks and `Dispose`

A suspended return skips the cleanups of the `try` and `using` regions it is
inside, because the iterator has not finished. `Dispose` sets a flag and
resumes the body once. At the resume point the body returns normally, so the
regions run their existing cleanup code and the `finally` blocks run.
A `Dispose` before the first `MoveNext`, or after the iterator has finished,
runs nothing.

An iterator object that is dropped without `Dispose` does not run its `finally`
blocks, which matches .NET. Its fields are released by the usual generated
teardown, so nothing leaks.

### Definite assignment

The `Step` body is not given to the definite-assignment analysis. The
compiler adds the dispatch jumps that skip declarations, and the analysis
would report false warnings for them. Roslyn has already checked the user's
code for definite assignment.

## Verification

Both samples run under `tools\verify.ps1` with `live=0`, and the harness checks
their output. Two programs in `samples/negative` check the rejected forms.
