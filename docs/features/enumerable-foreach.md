# Enumerable foreach

ArcSharp supports ordinary synchronous `foreach` over `IEnumerable<T>` and
the equivalent instance pattern on a reference type with a reference-type
enumerator. Roslyn selects `GetEnumerator`, `MoveNext` and `Current`.
ArcSharp evaluates the source once, holds the enumerator in a local, and
uses the normal method dispatch paths.

Each iteration releases its loop variable at the end of the body. If the
enumerator implements `IDisposable`, loop cleanup calls `Dispose` when
the loop finishes or exits through a break, return or exception. A separate
cleanup region releases the enumerator even if disposal throws. Array
`foreach` still uses indexed access.

Reference and numeric element conversions are supported where the compiler
can lower them directly. Struct enumerators, extension-based enumeration,
and conversions requiring boxing, unboxing or user-defined operators
are outside the supported subset. Iterator methods with `yield` are described in
[Iterators](iterators.md). The built-in collections also support generic [enumeration](collection-enumeration.md).

The [enumerable sample](../../samples/enumerable_foreach.cs) checks that
the factory runs once per loop and that disposal runs on completion, break
and exception. The verification harness checks its output and ARC counts.
