# Exceptions

ArcSharp accepts `try`, `catch`, `finally`, and `throw`. Whatever way control
leaves a piece of code, whether it returns normally or an exception unwinds
through it, every reference that code owned is released. A release that reaches
zero on that path runs the object's finaliser there, as it does everywhere
else. This page records how that is built and why. The language rules are in
the [language reference](../language-reference.md#exceptions).

## What a program can do

- Throw with `throw expr;` and rethrow with `throw;` inside a `catch` block.
- Catch by type, with or without a variable, or catch everything with `catch`
  and no type.
- Run a `finally` block on every path out of the `try`.
- Rely on `using` to call `Dispose()` while an exception unwinds.
- Derive new exception classes from the small library in the `System`
  namespace.
- Catch the failures the runtime itself detects: a null reference, an index out
  of range, a failed class cast, division by zero, the minimum integer divided
  by -1, and a covariant array store of the wrong type.
- Let an exception go unhandled. The program prints
  `Unhandled exception. Type: message`, then the `[arc]` line, and exits with
  code 70. The accounting line still shows `live=0`.

Exception filters (`catch ... when`) and throw expressions are rejected with
`ARC3001`.

## How it works

### A pending exception flag, not structured exception handling

The roadmap first proposed Windows structured exception handling through LLVM.
The implementation uses a different mechanism: one global pointer, `arc_exception`,
that holds the exception in flight. `throw` stores the exception there and
branches to the nearest handler. After every call, and after every runtime check
that can fail, the compiled code loads `arc_exception`. When it is not null,
the code releases what the statement still owns and branches to the nearest
handler. A function that has no handler branches to its own exit, which returns
to the caller, and the caller checks in the same way.

The reasons for this choice:

- **It reuses the cleanup that already exists.** Every `return` already stores
  its value and branches to `func.exit`, where locals are released. An
  exception takes the same road through a new `func.unwind` block, so the
  ownership rules do not have a second set of cleanup code to keep in step.
- **It is deterministic and easy to read.** The unwind path is ordinary
  branches in the `.ll` file. There are no personality routines, no landing
  pads, and no dependency on the C++ or Windows unwinder, which the runtime
  does not link.
- **It does not depend on Windows.** Structured exception handling is specific
  to Windows. The flag is plain C and plain LLVM, so the Linux path needs
  nothing extra. That path is not part of the current verification.

The cost is a load and a compare after each call that can fail. The roadmap
records the optimisation of skipping that check after a call that cannot throw
as a later step. It is not done, because proving that needs a pass over the
call graph, and the project prefers a simple and correct model.

### Who owns what while unwinding

The rule from the ARC model is that every owned value is released exactly once.
An exception adds a new exit from the middle of a statement, so each value that
is owned at that point needs an owner.

| Value in flight | Owner when an exception leaves |
|---|---|
| A temporary in the statement | The statement's temporary list. The unwind path releases that list, in reverse order, before it branches on |
| A pinned owner behind an interior reference | The pin list of the call. The unwind path releases the pins too |
| A new object while its constructor runs | The temporary list. The emitter adds the object before the constructor call and removes it afterwards |
| The value of `target = value` while the target is evaluated | The temporary list. The emitter adds the value before it evaluates a field or element target, and removes it just before the store that takes it over |
| The left operand of a string concatenation | The temporary list, from the moment it exists |
| The operand of a class cast | The cast's own unwind path, because the cast failing has no statement to belong to yet |
| A local or parameter | `func.exit`, as always. The unwind block branches there |
| A catch variable, and the exception a `catch` block is handling | Hidden slots that are released at `func.exit` |
| A return value that has to wait for a `finally` block | A hidden slot, so a `finally` block that throws does not lose or leak it |

The hidden slots are ordinary stack slots, created in the entry block and
initialised to null. Releasing a null is allowed, so the same cleanup is right
whether or not the slot was ever filled.

### `finally` and `using`

A `finally` block, and the `Dispose()` call of a `using` statement, are both
emitted by one routine. It emits the cleanup code once for each way out of the
protected code:

1. Falling off the end of the block.
2. A `return`. The return value waits in a hidden slot while the cleanups run,
   innermost first.
3. An exception. The exception is set aside while the cleanup runs, so a
   `finally` block does not see it as its own, and it is put back afterwards.
   If the cleanup throws, the new exception replaces the one that was set
   aside, and the old one is released. This matches C#.

A `try` with `catch` clauses and a `finally` block is treated as a `try`/`catch`
nested inside a `try`/`finally`.

### Catch dispatch

When the code in a `try` throws, control reaches the dispatch block. It tests
the exception against each clause in order with the same runtime type test that
`is` uses, and enters the first clause that matches. A catch-all clause matches
anything. The compiler treats `catch (Exception)` the same way, because every
exception derives from it. When no clause matches, the exception goes on to the
enclosing handler.

The catch clauses are outside the protected region. An exception thrown inside
a catch block goes to the next handler out and not back to its own clauses.

Inside a catch block the exception is held twice: once in a hidden slot that
`throw;` reads, and once in the catch variable. Both are released when the block
completes normally, and at `func.exit` otherwise.

### The exception library

The exception types are written in C#, in `corelib/Exceptions.cs`. The build
embeds that file in the compiler, and every compilation parses it together with
the user's files. This was chosen over adding the types to the reference
assembly in `ref/ArcSharp.References` because:

- The types are ordinary classes with fields, constructors, and a base class.
  Compiling them from source gives them vtables, type descriptors, and
  teardown with no special case.
- Roslyn binds `throw` and `catch` against `System.Exception` from the source.
  The check that it does so is the `exceptions_basic` sample.

`Exception` has one field, the message, and it must stay the first field. The
runtime raises its own exceptions without running a constructor: it allocates
the object and writes the message string at byte offset 24, which is the first
field slot. If the field order changes, `ARC_EXC_MESSAGE_OFFSET` in
`runtime/arc_runtime.c` must change with it. The runtime learns the classes it
raises through `arc_set_exception_types`, which `main` calls before it enters
`Main`.

The base `Exception` stores its message. `TypeInitializationException`
also stores the original cause. See [Static constructors](static-constructors.md).

### Runtime checks become exceptions

| Failure | Where it is detected | Exception |
|---|---|---|
| Member access, call, `.Length`, or element access on null | `arc_null_check` before the access. The emitter skips it for `this`, a fresh `new`, and string literals, which cannot be null | `NullReferenceException` |
| Array index outside the array | `arc_bounds_check`. `--no-bounds` still removes it | `IndexOutOfRangeException` |
| Explicit class cast of the wrong type | `arc_cast_check` | `InvalidCastException` |
| `/` or `%` by zero | `arc_div_check`. The emitter skips it when the divisor is a constant other than 0 and -1 | `DivideByZeroException` |
| The minimum value divided by -1 | `arc_div_check` | `OverflowException` |
| Store of the wrong type into a covariant array | `arc_array_store_check` | `ArrayTypeMismatchException` |
| `throw null` | `arc_throw` | `NullReferenceException` |

Before this change each of these printed a message and exited with code 70, and
the cast and null cases were not checked at all.

### Unhandled exceptions

`main` loads `arc_exception` after `Main` returns. When it is not null, the
runtime prints `Unhandled exception. Type: message` to stderr, releases the
exception, prints the `[arc]` line, and exits with code 70. The exit code is the
same one the earlier runtime failures used, so the documented contract does not
change. There is no stack trace.

### Finalisers that throw

A finaliser that throws is reported and ignored. The runtime sets aside any
exception that was already in flight before it runs the finaliser chain, so the
finaliser neither sees it nor replaces it. If the finaliser leaves an exception
behind, the runtime prints
`[arc] exception in finaliser of T ignored: Type: message` to stderr, releases
the exception, and carries on to the teardown that releases the object's
fields. The code that released the last reference does not see the exception,
and the outer exception is put back.

### The lifetime pass

The lifetime pass treats an unwind edge as a use in the conservative way:

- A `return local` inside a `try` is not turned into a move, because a `finally`
  block or a catch handler may still read the local after the return value is
  chosen.
- Locals still die at `func.exit`, so an object that an exception leaves behind
  is destroyed at the same place it would be destroyed by a `return`.

A tighter treatment that destroys a value on the unwind edge as early as the
normal path does is possible, and is not part of this change.

## What is not covered

- Exception filters, throw expressions, and `StackTrace`,
  `ToString()`, `AggregateException`, and the rest of the .NET exception types.
- Exceptions across threads. The runtime is single-threaded.

## How it is verified

Each sample checks its output and the full `[arc]` line, and every one ends
with `live=0`.

| Behaviour | Sample |
|---|---|
| `try`, `catch`, `finally`, nested handlers, and the order of clauses | `exceptions_basic` |
| Cleanup of temporaries, locals, constructor arguments, stores, concatenation, `return` through `finally`, rethrow, and a `finally` that replaces an exception | `exceptions_unwind` |
| Each runtime check, caught | `exceptions_runtime` |
| `Dispose()` while unwinding, and a `Dispose()` that throws | `exceptions_using` |
| A throwing finaliser, alone and during an unwind | `exceptions_finaliser` |
| Exit code 70, the message, and balanced accounting for an unhandled exception | `negative/exception_unhandled` |
| Exception filters, throw expressions, and a throw of a non-exception are rejected | `negative/exception_filter`, `negative/throw_expression`, `negative/throw_non_exception` |

The analyser's tests cover the same rejections, and they show that a plain
`try`, `catch`, `finally`, and `throw` are no longer reported.
