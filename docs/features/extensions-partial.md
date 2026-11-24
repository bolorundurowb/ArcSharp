# Extension methods and partial types

## Extension methods

A static class can declare extension methods with `this` on the first
parameter, and calls such as `crate.Total()` or `6.Times(7)` bind to them.
Roslyn already resolves the call to a static method, so the lowering needs no
new code. The extension methods work on classes, structs, strings and
numeric types, and they can be chained.

The static class must be in scope. A call whose class is in another namespace
without a `using` fails with Roslyn's `CS1061`.

The reference assembly declares `System.Runtime.CompilerServices.ExtensionAttribute`.
Roslyn needs it to accept the `this` modifier. Without it, every extension
declaration is rejected with `CS1110`.

## Partial types

The parts of a `partial class` are merged by Roslyn before lowering. The
parts can be in one file or in several files given to the compiler together.
The analyser no longer reports `partial` as unsupported. Only classes are
covered by a sample, and partial methods are not part of this step.

## Verification

The sample `extensions` is built from two files. The first holds `Main`. The
second holds the two halves of a partial class, a struct, and the extension
class. It checks calls on a class, on `int`, on `string` and on a struct,
and chained calls. The accounting ends with `live=0`. The must-fail sample
`extension_scope` checks the namespace rule. The surface checker confirms
that Roslyn accepts an extension method against the reference assembly.
