# M2 verification baseline

The Windows harness checks the exact `alloc`, `dead`, `freed`, and `live`
counts for each sample under `ARC_CHECK=1`. The destruction-point sample
checks a weak reference before either strong owner is cleared, after the first
is cleared, and immediately after the last is cleared. Each observation uses
a helper so the strong reference returned by `TryGetTarget` is released
before the next statement in the caller. The recorded accounting and
retain/release counts are in [Verification](../verification.md).

Instance field initialisers run in declaration order in the constructor that
calls the base constructor. A delegating `this(...)` constructor leaves
initialisation to the constructor it calls. Static field initialisers run in
declaration order on first access to a static field of their declaring type.
The guard is set before any initialiser runs so re-entrant reads see the
fields initialised so far. Get-only auto-properties use their backing fields
for constructor assignments. Inherited accessors are verified through a
derived-typed variable.

Abstract methods occupy virtual slots, which a concrete override fills.
String operators compare the stored byte sequences and distinguish null
from the empty string. Object-typed equality keeps reference identity.
The existing integer conversion and array-slot instructions handle narrow
integers.

Short-circuit branches merge owned temporaries with null values on skipped
paths. This gives the statement cleanup valid LLVM operands and keeps each
executed temporary alive through the full statement. Regression programs
cover both the executed and skipped operands.
