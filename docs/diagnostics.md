# Diagnostics

The compiler reports Roslyn diagnostics with their original `CS` IDs and
ArcSharp diagnostics with stable `ARC` IDs.

```text
C:\src\app.cs(6,19): error CS1002: ; expected
(0,0): error ARC2050: no static 'Main()' entry point found
```

When a diagnostic has a source location, it includes the file, line and column.
Program-wide diagnostics may use `(0,0)`. Definite-assignment warnings point to
the enclosing method.

Errors fail compilation with exit code 1. Warnings fail compilation with
`-Werror`. Retired IDs stay reserved.

| Range | What it covers |
|---|---|
| `CS` | Roslyn diagnostics for C# syntax, types and binding against the ArcSharp reference assembly |
| `ARC2xxx` | ArcSharp lowering, compiler configuration, entry point and definite-assignment checks |
| `ARC3xxx` | Unsupported language constructs and ARC hazards shared with the analyser |

The native runtime reports checking-mode violations with an `[arc-check]`
message and exit code 70. See [ARC checking mode](features/arc-checking-mode.md).
An exception that no handler catches ends the program with exit code 70 too.
That is a run-time event, not a diagnostic: it prints
`Unhandled exception. Type: message` and has no code. See
[Exceptions](features/exceptions.md).

## ArcSharp compiler diagnostics

### ARC2001

**Error. ArcSharp could not bind or lower the program.** The message identifies
the failure. Examples include a missing ArcSharp reference assembly, an absent
getter during property lowering, and assignment to a reference-typed parameter.
For the parameter case, copy it into a local and assign the local.

### ARC2002

**Error. Unresolved member or parameter during lowering.** ArcSharp could not
find the lowered symbol for a parameter, field, method or property accessor.
Roslyn reports ordinary unresolved C# names with `CS` diagnostics.

### ARC2011

**Error. Invalid `using` resource.** The resource of a `using` statement does
not meet the cleanup contract: the compiler needs a parameterless instance
`void Dispose()` method to call at scope exit. The messages are:

| Message | Cause |
|---|---|
| `type 'T' used in 'using' has no parameterless 'void Dispose()' method` | The resource type has no instance `Dispose()` method, or its `Dispose` is `static`. `IDisposable` is structural: implementing the interface is not required, but the method must exist. |
| `interface 'I' has no parameterless 'Dispose' method` | The interface-typed resource does not declare `Dispose`. |
| `type 'T' used in 'using' must be a reference type` | A struct cannot be a `using` resource yet. |

### ARC2012

**Error. Abstract class instantiated.** `new Shape()` where `Shape` is
`abstract`. Create an instance of a concrete derived class instead.

### ARC2013

**Error. Generic specialisation failed.** A type argument could not be
materialised, or specialisation exceeds 64 nested or 1024 total instantiations.
Check the type arguments and avoid recursively expanding closed types. Roslyn
reports generic arity and constraint errors with `CS` diagnostics.

### ARC2015

**Error. Managed reference is not safe for this call.** The compiler cannot
prove that the storage addressed by `ref`, `out`, or `in` stays valid, or the
use would break the rules for that slot.

| Message | What to do |
|---|---|
| `'in' parameter 'p' is read-only` | Do not assign `p`, pass it as `ref` or `out`, or call an instance method on it when `p` is an `in` struct. Copy it into a local first. A reference loaded from the struct can still be written. |

### ARC2016

**Error. A finaliser stores `this`.** The message is
`a finaliser must not store 'this' in a field, property, or array element: an
object cannot be resurrected`. Inside `~T()`, an assignment to a field, a
property or an array element is rejected when the value is `this`, a
conversion of `this`, or a local variable that holds one of those.

```csharp
class Registry
{
    public static Item last;
}

class Item
{
    ~Item()
    {
        Registry.last = this;   // ARC2016
    }
}
```

Remove the store. The object is being destroyed and cannot be kept. The check
only sees stores that are written in the finaliser itself. If the store is
hidden inside another method, the program compiles and the runtime aborts it
when the finaliser returns: the output is
`ResurrectionException: a finaliser of <Type> kept a reference to the object it
was finalising`, followed by the `[arc]` line, and the exit code is 70. See
[Deterministic finalisers](features/finalisers.md#resurrection).

### ARC2050

**Error. No entry point.** No class declares `static void Main()` or
`static int Main()`. `Main` must have no parameters; `Main(string[] args)` is
not recognised.

### ARC2100

**Warning. Local may be used before assignment.** The message is
`local 'x' may be used before assignment`. The analysis is conservative: it
warns whenever it cannot prove that every path assigns the local before
reading it. Give the local an initial value where you declare it.

The line number is the line of the method that contains the read.

## Shared ARC3xxx catalogue

These codes are shared by the compiler and the Roslyn analyser in
`analyzers/ArcSharp.Analyzers`. The ids, titles, and cycle messages live in
`analyzers/ArcSharp.Diagnostics`, which both reference, so the two tools cannot
drift apart. The analyser is optional: the compiler reports every rule itself.

### ARC3001

**Error. Construct is not supported by ArcSharp.** ArcSharp recognised a
C# construct it cannot lower. The message names the construct.

The rule covers virtual/interface generic methods, exception filters
(`catch ... when`), throw expressions, `lock`, `foreach`, `switch`, `break`, `continue`, `do`/`while`, `goto`,
lambdas, local functions, `async`/`await`, `yield`, LINQ, pattern matching,
interpolated strings, `default`, top-level statements,
attributes, expression-bodied members, `sealed`/`partial`/`readonly`/`const`,
`base.` access and the struct
restrictions listed in the [Language reference](language-reference.md#structs).
Unsupported constructs also include `GC.ReRegisterForFinalize`
and a value-type argument to `GC.SuppressFinalize`, which would need boxing. A bad Unicode escape is Roslyn's
`CS1009`. A missing type such as `List<T>`, a wrong generic arity, and a
failed constraint are Roslyn `CS` codes.

The negative samples under `samples/negative/` exercise this rule.
`tools/verify.ps1` checks that each one fails with the expected code.

### ARC3100

**Warning. Possible strong reference cycle.** A set of classes can reach each
other through strong fields, or one class has a strong field of its own type.
The warning is the architectural response to cycles: the program stays legal.
ARC cannot reclaim such a cycle, so the instances leak if the developer, or a
library they compile, leaves the back-edge in place. Change it to
`WeakReference<T>` when the cycle is real.

The analysis looks at declared field types, not at an object graph. It includes
arrays of a class, static fields, and class references reached through a struct
field. It also warns on a binary tree or a singly linked list, which does not
leak unless an instance is later wired into a loop: that is the documented
false positive. Suppress it with `#pragma warning disable ARC3100` or
`[SuppressMessage]` when the cycle is intentional or impossible.

`cycle_strong` and `cycle_struct` warn; `cycle_weak` and `cycle_weakref` do not.

### ARC3200

**Info. This call behaves differently from the .NET garbage collector.**
`GC.Collect()` and `GC.WaitForPendingFinalizers()` compile and do nothing,
because an object is finalised and destroyed when its last strong reference is
released, so there is never anything to collect or wait for. The compiler and
the analyser both report it. An info message does not fail the build, not even
with `-Werror`.

`GC.KeepAlive` is not in the supported library, so it is an ordinary Roslyn
error. `GC.ReRegisterForFinalize` and resurrection are rejected rather than
reported here: the first is `ARC3001`, and the second is `ARC2016`. The
finalisation contract is in the [memory model](memory-model.md#finalisation).

### ARC3300 (reserved)

**Warning, planned.** Threads and other cross-thread sharing. Reference counts
are non-atomic, so such code is unsound rather than merely unimplemented. The
id is reserved in the shared catalogue but is not reported yet.

## Errors from clang

A few problems pass the compiler's checks and fail later, when `clang` builds
the executable. They appear as `clang` errors followed by
`error: clang build failed`:

| `clang` message | Cause |
|---|---|
| `use of undefined value` | Calling an `abstract` method |

This is a known defect. See [Known issues](language-reference.md#known-issues).
