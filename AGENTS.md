# AGENTS.md

Guidance for AI coding agents, and for people, working on ArcSharp. Read this
before changing the compiler, the runtime, the samples, or the documentation.

## What ArcSharp is

ArcSharp is a proof-of-concept compiler for a subset of C# 12. It compiles C#
source to textual LLVM IR, links that IR with a small C runtime, and produces a
native executable that manages memory with automatic reference counting (ARC)
instead of a garbage collector.

The compiler is written in C# 12 on .NET 11. The runtime is a single C file. The
only supported architecture is x86-64, and the actively verified target is
Windows x64 (`x86_64-pc-windows-msvc`).

The project values a small, provably balanced ARC model over performance or
breadth. When a change has to choose between "simple and correct" and "fast or
clever", choose simple and correct, and record the optimisation in
`docs/roadmap.md`.

The direction of the project is that roadmap, not an open-ended C# compiler.
Roslyn is the frontend. A new language feature is lowered from Roslyn, not by
growing a parser. The ownership tree is the intermediate representation; stock
CIL is not a stage, because it drops the lifetime facts the memory model
needs. A proved lifetime skips retain and release on the existing header, and
everything else stays reference-counted. Finalisers run at the last strong
release. `ARC3100` warns on a visible cycle, the program still compiles, and
an ignored warning leaks. The compatibility target bounds the language and
libraries. Do not start work outside that target.

## Quick start

### Prerequisites

- The .NET 11 SDK. `global.json` pins the repository to the newest installed
  11 SDK (`rollForward` is `latestMinor`) and does not roll forward to .NET 12
  or later. Do not change the target framework or the pin unless the task asks
  for it.
- LLVM `clang`, by default at `C:/Program Files/LLVM/bin/clang.exe`. A Windows
  linker that clang can reach (Visual Studio Build Tools or the Windows SDK).
- PowerShell. `tools/verify.ps1` runs on both Windows PowerShell 5.1 and
  PowerShell 7.

### Commands

```powershell
# Build the compiler
dotnet build -c Release

# Compile and run one program (writes <name>.ll and <name>.exe next to -o)
dotnet bin/Release/net11.0/arcsharp.dll samples/inherit.cs -o $env:TEMP\inherit `
  --target windows --clang "C:/Program Files/LLVM/bin/clang.exe" `
  --runtime runtime/arc_runtime.c --run

# Emit LLVM IR only, for inspection
dotnet bin/Release/net11.0/arcsharp.dll samples/inherit.cs -o $env:TEMP\inherit --emit-llvm

# Run the full verification suite (the project's only test gate)
powershell -ExecutionPolicy Bypass -File tools\verify.ps1
```

Pass `-o` with a path under `%TEMP%` while experimenting. Without `-o`, output
files are written next to the current directory using the first input's name.
`*.ll`, `*.exe`, `*.obj`, `bin/`, and `obj/` are git-ignored, but do not leave
stray artefacts in `samples/`.

Compiler command-line options (see `src/Program.cs`):

| Option | Meaning |
|---|---|
| `<file.cs> [more.cs ...]` | One or more inputs; their types are merged into one program |
| `-o <base>` | Output base path, without an extension |
| `--target windows\|host` | `windows` (default) or the Linux x64 triple |
| `--emit-llvm` | Stop after writing the `.ll` file |
| `--clang <path>` | Build the `.exe` in one step with clang (the path used on Windows) |
| `--run` | Run the produced executable |
| `--runtime <path>` | C runtime source, default `runtime/arc_runtime.c` |
| `--no-bounds` | Omit array bounds checks |
| `-Werror` / `--warn-as-error` | Treat warnings as errors |
| `--llc <name>`, `--cc <name>` | Tools for the non-clang path (`llc`, then `gcc` on Linux) |

The compiler writes progress lines such as `[arcsharp] wrote ...` to **stderr**.
Program output goes to stdout. The runtime's ARC accounting line also goes to
stderr. Scripts that invoke the compiler must not treat stderr as failure; see
how `tools/verify.ps1` captures both streams.

## Repository map

| Path | Responsibility |
|---|---|
| `src/Program.cs` | Command-line parsing |
| `src/Driver/Compilation.cs` | Orchestrates lowering, emission, and the clang or llc invocation |
| `src/Frontend/` | Roslyn compilation and the lowering into the bound tree |
| `src/Lexing/Diagnostic.cs`, `src/Lexing/TokenKind.cs` | Diagnostic records and the token kinds the emitter still names |
| `src/Syntax/Kinds.cs` | `LiteralKind` and `RefKind` |
| `src/Binding/Symbols.cs` | `TypeSymbol`, `FieldSymbol`, `MethodSymbol`, layout and name mangling |
| `src/Binding/BoundTree.cs` | Typed bound-tree nodes consumed by the emitter |
| `src/Binding/DefiniteAssignment.cs` | Conservative read-before-assignment analysis (warning `ARC2100`) |
| `src/Binding/CycleAnalysis.cs` | Strong-field graph and strongly connected components (warning `ARC3100`) |
| `src/CodeGen/Emitter.cs` | Bound tree to LLVM IR, with every ARC operation woven in |
| `runtime/arc_runtime.c` | Object header, retain and release, weak references, arrays, strings, type tests, console, accounting |
| `ref/ArcSharp.References/` | Reference assembly Roslyn binds against. Copied beside the compiler |
| `ref/ArcSharp.References.Check/` | Checks what Roslyn accepts and rejects against that assembly |
| `analyzers/ArcSharp.Diagnostics/` | Shared `ARC3xxx` ids and messages, with no Roslyn dependency |
| `analyzers/ArcSharp.Analyzers/` | Roslyn analyser for `ARC3001` and `ARC3100` |
| `analyzers/ArcSharp.Analyzers.Tests/` | xUnit tests that run the analyser over in-memory compilations |
| `samples/*.cs` | Test programs; each one is run by `tools/verify.ps1` |
| `samples/negative/*.cs` | Programs that must be rejected with a specific diagnostic |
| `tools/verify.ps1` | End-to-end verification harness |
| `tools/build-windows.ps1` | Convenience script: build and compile one file |
| `tools/env.ps1` | Environment variables and LLVM on `PATH` |
| `README.md` | Landing page |
| `docs/` | User guides, architecture, roadmap, verification results, and the logo (see "Documentation" below) |

`ArcSharp.csproj` excludes `samples/**/*.cs`, `analyzers/**/*.cs`, and
`ref/**/*.cs` from compilation, so those files are never built as part of the
compiler. The reference assembly is its own project. Keep it that way.

## The compiler pipeline

```
.cs files -> Roslyn -> lowering (bound tree, layouts, vtables)
          -> DefiniteAssignment (warnings) -> Emitter (.ll)
          -> clang (.ll + arc_runtime.c -> .exe) -> run -> [arc] accounting on stderr
```

Roslyn parses and binds every file against `ref/ArcSharp.References`, and the
lowering in `src/Frontend` builds the bound tree. `using` directives are
honoured, and a namespace is part of a type's identity. A global
`using System` is added so `Console` resolves without one.

The current milestone is the verification baseline in the
[Roadmap](docs/roadmap.md). Do not add a language feature ahead of that. A
defect in the emitter or the runtime may still be fixed. A defect in binding
is fixed in the Roslyn lowering.

### Adding or changing a language feature

Do this only for a change the roadmap still allows on the current compiler,
which today means an emitter or runtime defect. A new language feature is
lowered from a Roslyn operation in `src/Frontend`, not by adding a parser.

When a change does touch the current pipeline, work through this list in order:

1. **Lowering**: handle the Roslyn symbol or `IOperation` in `src/Frontend`,
   produce a bound node from `BoundTree.cs`, and type-check it. An operation
   the lowering does not handle reports `ARC3001`. Report other errors with a
   diagnostic code (see "Diagnostics").
2. **Definite assignment**: if the feature reads or writes locals, or changes
   control flow, update `DefiniteAssignment.cs` so the analysis stays
   conservative.
3. **Emitter**: add a case to `EmitStmt` or `EmitR` and follow the ARC rules
   below exactly.
4. **Runtime**: if new runtime support is needed, add the C function **and**
   its matching `declare` line in `Emitter.EmitRuntimeDecls`.
5. **Sample and harness**: add a sample and an `Invoke-Sample` line (see
   "Testing").
6. **Documentation**: update `docs/language-reference.md`, the status in
   `docs/architecture.md` and `docs/roadmap.md`, `docs/verification.md`, and
   `README.md` (see "Documentation").

**Unhandled nodes fail silently. This is the most important pitfall in the
codebase.**

- The lowering's unknown-operation path must report `ARC3001`. A missing case
  that returns an empty block or a null literal drops the statement.
- `Emitter.EmitR` returns the constant `"0"` for an unknown bound expression,
  and `Emitter.EmitStmt` has no default case.

So a new node that is missing from one of these switches can compile cleanly
and still produce a wrong program. After adding a node, confirm that it reaches
the emitter. Inspect the `.ll` output with `--emit-llvm`, and make sure a sample
checks the program's observable output.

## Invariants you must preserve

### ARC ownership rules

These rules are what keep every sample at `live=0`. They are described in
`docs/architecture.md` section 4, and the emitter applies them as follows.

- **Every evaluated reference expression yields an owned (+1) value.** `new`
  and reference-returning calls already return +1. Reading a local, parameter,
  field, array element, or `this` retains the value to +1 (`arc_retain`).
- **A +1 value is consumed exactly once.** Storing it in a local, field, or
  array element transfers ownership with `arc_assign_take`, which releases the
  slot's old value and does not retain the new one. Returning it hands it to
  the caller. Anything else is a temporary.
- **Temporaries are released at the end of the full statement.** The emitter
  records them in `_stmtTemps`. Each statement takes a `mark`, emits its
  expressions, and calls `ReleaseTemps(mark)`. A new emit path that produces a
  reference value that is not stored or returned must add it to `_stmtTemps`.
- **Reference-typed locals start as null and are released at `func.exit`.**
  Every `return` stores its value in `%retval` and branches to `func.exit`; do
  not emit a `ret` anywhere else.
- **Use the right store primitive.** `arc_assign_take` stores a value that is
  already +1. `arc_store_strong` retains and then stores a value that is
  borrowed. `arc_store_weak` and `arc_load_weak` handle weak slots;
  `arc_load_weak` returns +1 or null.
- **Destructors.** The emitter generates `<Type>__deinit` for every class. It
  releases each strong reference field and clears each weak field.
  `arc_release` calls it when the strong count reaches zero. This is generated
  teardown, not a C# finaliser. `~T()` stays rejected until the finalisation
  milestone (M4 in `docs/roadmap.md`). When that lands, the user finaliser runs
  before those field releases, on the releasing thread, and a worklist replaces
  the recursive release. Do not implement `~T()` on the current parser.
- **Counts are non-atomic and the model is single-threaded.** Do not introduce
  threads or shared state. Threads are outside the compatibility target, and
  the roadmap lists them as not scheduled.

Over-retaining is acceptable; an unbalanced retain or release is not. Do not
remove or skip retain and release pairs in the current emitter. The lifetime
pass that will do that is a later milestone
(M3 in `docs/roadmap.md`). It keeps this header on every heap object and
uses ARC wherever it cannot prove a lifetime. It does not add a second layout.

### Object layout

The layout uses uniform 8-byte slots and must stay identical in the emitter,
the symbols, and the runtime.

| Object | Layout (byte offsets) |
|---|---|
| Header (all heap objects) | `strong` at 0, `weak` at 8, `TypeInfo*` at 16 |
| Class instance | Field `i` at `24 + 8 * i` (`FieldSymbol.ByteOffset`) |
| Struct value | Field `i` at `8 * i`; size is `HeapSize` bytes (whole 8-byte slots) |
| Array | Length at 24, element `TypeInfo*` at 32, elements at 40 in 8-byte slots |
| String | Length at 24, UTF-8 bytes at 32, NUL-terminated |
| `WeakReference<T>` | One weak slot at 24 |

`TypeInfo` has ten fields: name, instance size, deinit, vtable, vtable
length, itable, itable length, base, strong-slot metadata, and slot count.
The slot record (`ArcSlot`) is a strong reference or an inline struct. It is
declared in two places that must match: the `%TypeInfo` and `%ArcSlot` types
emitted at the top of every module in `Emitter.Emit`, and `struct TypeInfo`
and `struct ArcSlot` in `runtime/arc_runtime.c`. Change both together or
neither. When documentation shows a different layout, the code is correct;
update the documentation.

Symbol names in the IR are useful when you read `.ll` files: methods are
`<Type>__<Name>__<paramCount>`, constructors are `<Type>__ctor__<paramCount>`,
static fields are `@<Type>__sf__<Name>`, type descriptors are `@<Type>__ti`, and
destructors are `@<Type>__deinit`.

### Runtime boundary

- Each runtime function called from IR needs a `declare` in
  `EmitRuntimeDecls`, with a signature that matches the C definition: `i8*` for
  object pointers, `i64` for `intptr_t`, `i32` for C `int`. A mismatch compiles
  and links but corrupts arguments at run time.
- Runtime failures such as an index out of range or an array type mismatch
  print an exception-style message, call `arc_report()`, and exit with code 70.
  There is no exception handling yet.
- `arc_report()` prints `[arc] alloc=A dead=D freed=F live=L` to stderr at the
  end of `Main`. The harness depends on that exact format. Do not change it
  without updating `tools/verify.ps1`. `ARC_STATS=1` prints
  `[arc-stats] retain=R release=S` on the following line. That line is not
  part of the harness format.
- Keep the runtime portable C with only the standard library. It is compiled
  with clang for Windows and with gcc or clang on Linux.
- Windows x64 uses the LLP64 model: C `long` is 32 bits there, while it is 64
  bits on Linux. Never use `long` or `%ld` for a C# `long` or for a pointer-sized
  value. Use `intptr_t` or `long long`, and format with `%lld` after a cast to
  `long long`.

### Source compatibility with Roslyn

ArcSharp source should also be valid C# that the normal .NET toolchain
accepts. Cycle breaking uses the standard `System.WeakReference<T>` for this
reason. Do not invent non-standard syntax.

### Diagnostics

Each diagnostic has a stable code and a severity (`Diagnostic` in
`src/Lexing/Diagnostic.cs`). The driver fails compilation when the error count is non-zero;
warnings fail it only with `-Werror`.

| Range | Stage | Codes in use |
|---|---|---|
| `CS` | Roslyn | C# syntax, binding and type checking |
| `ARC2xxx` | ArcSharp compiler | `ARC2001` binding or lowering failure, `ARC2002` unresolved lowered member or parameter, `ARC2011` invalid `using` resource, `ARC2012` abstract class instantiation, `ARC2013` generic specialisation, `ARC2015` managed-reference lifetime, `ARC2050` no entry point, `ARC2100` definite assignment (warning) |
| `ARC3xxx` | Shared catalogue | `ARC3001` unsupported construct (error), `ARC3100` possible strong cycle (warning). `ARC3200` and `ARC3300` are reserved in the catalogue for planned rules; do not allocate `ARC3xxx` for other errors. |

Never renumber or reuse an existing code. The former lexer and parser ranges
are retired and stay reserved. A new ArcSharp diagnostic gets a new code in
the applicable active range. Definite-assignment warnings point to the
enclosing method because bound nodes do not carry source positions.

## Testing

`tools/verify.ps1` is the only test gate. GitHub Actions runs it on Windows x64
for pull requests targeting `master`. The harness builds the compiler and
compiles every sample with clang. It runs each executable and
checks two things:

1. The program's stdout contains an expected substring.
2. The `live=` count in the `[arc]` line equals the expected value.

It also compiles the must-fail samples and checks the expected diagnostic code,
checks that `ARC3100` appears for the leaking cycle samples and not for the
broken ones, runs the analyser's xUnit project with `dotnet test`, and runs
`ref/ArcSharp.References.Check` against the reference assembly.

Run it after every change to the compiler or the runtime. All samples must
pass. `cycle_strong` and `cycle_struct` are expected to report `live=2`; those
leaks are deliberate. Cycles are a closed decision: `ARC3100` warns, the
program stays legal, and an ignored warning leaks, including when the cycle
comes from a library. Do not turn `ARC3100` into an error, and do not add a
cycle collector. `WeakReference<T>` is how a cycle is broken.

### Adding a sample

1. Add `samples/<name>.cs`, with a `static void Main()` or `static int Main()`.
   Print a distinctive line that proves the feature worked, not only that the
   program ran.
2. Add a line to `tools/verify.ps1`:

   ```powershell
   Invoke-Sample <name>  <expectedLive>  "<expected stdout substring>"  @("<name>")
   ```

   For a multi-file sample, list every file, as `multifile` does.
3. Run the harness, then copy the **actual** alloc, dead, and live counts and the
   output into the table in `docs/verification.md`.

### Reading ARC accounting

| Symptom | Usual cause |
|---|---|
| `live` greater than expected | A missing release: a temporary not added to `_stmtTemps`, a local not released, or a missing destructor field |
| A crash, or `dead` and `freed` counts that make no sense | A double release, or a +1 value consumed twice |
| Exit code 70 and an exception-style message | A runtime check failed (index out of range or array type mismatch); the `[arc]` line is still printed |
| No `[arc]` line | The program crashed before `Main` returned, or the build failed before anything ran |
| The output is wrong but `live=0` | Often a node the binder or emitter dropped silently; inspect the `.ll` |

The clang warning `overriding the module target triple ... [-Woverride-module]`
is expected and harmless.

### Honesty about verification

Report only what was actually run. If the .NET 11 SDK or clang is not available
in your environment, say that the harness could not be run. Never write
accounting numbers or "passed" into `docs/verification.md` or a commit message
without a real run. The Linux x64 path (`--target host` with `llc` and `gcc`)
exists, but it is not part of the current verification; do not claim it was
tested unless you tested it.

## Code style

Follow `.editorconfig`. The points that matter most:

- CRLF line endings, UTF-8, four-space indentation, and a final newline.
- Braces on their own lines, and braces required for every control block
  (`IDE0011` is a warning).
- File-scoped namespaces that match the folder (`ArcSharp.Binding` for
  `src/Binding`; `IDE0130` is a warning).
- `var` when the type is apparent; keyword types (`int`, `string`) instead of
  BCL names.
- Private fields are `_camelCase`. Interfaces start with `I`.
- Nullable reference types are enabled. Do not suppress warnings with `!`
  unless the value is guaranteed non-null.
- Target C# 12 and .NET 11. Do not use C# syntax newer than C# 12.
- The compiler references `Microsoft.CodeAnalysis.CSharp` 4.8.0, the same
  version as the analyser. Do not add any other package unless the roadmap
  says so. LLVMSharp stays a later roadmap item, after the lifetime pass.
  `analyzers/ArcSharp.Diagnostics` stays free of Roslyn.

Match the surrounding code. The emitter builds IR with string interpolation
through the helpers `Inst`, `Do`, `Term`, and `Lbl`. Use them, because they
track the current basic block and the termination state.

Comments should explain constraints that the code cannot show, such as an LLVM
quirk, an ARC invariant, or a runtime layout dependency. Do not restate what
the code does.

## Documentation

For every feature you implement, add a feature document under `docs/features/`
in the same change and link it from `docs/README.md`. Record the implementation
decisions and why they were made, including relevant constraints, trade-offs,
and how the behaviour is verified.

Keep the documentation accurate in the same change that alters behaviour.

The documentation lives in `docs/`, with `docs/README.md` as its index.
`README.md` at the root is the landing page, and this file stays at the root
for tools that look for it there.

| Document | Update it when |
|---|---|
| `README.md` | The feature summary, the status, the prerequisites, or the quick-start commands change |
| `docs/getting-started.md` | The prerequisites, the build steps, or the first-run output change |
| `docs/language-reference.md` | A language feature is added, a difference from .NET changes, or a known issue is found or fixed |
| `docs/memory-model.md` | The user-visible lifetime rules or `WeakReference<T>` support change |
| `docs/cli-reference.md` | A command-line option, an output file, or an exit code changes |
| `docs/diagnostics.md` | A diagnostic code or message is added or changed |
| `docs/troubleshooting.md` | A new failure mode and its fix are known |
| `docs/architecture.md` | The lowering of the subset (section 6), the unsupported list (section 7), the status table (section 8), the object model, or the ARC rules change |
| `docs/roadmap.md` | A milestone is done (move it to "Where we are"), or the plan changes |
| `docs/verification.md` | Samples are added or changed, or the harness is re-run with different results |

Every example program in the user guides must compile and run with the
current compiler. When you change behaviour, re-run the affected examples and
update the output shown on the page. When a known issue is fixed, remove it
from `docs/language-reference.md` and from the defect list in the roadmap's
M2 milestone.

Write prose in UK English (for example "finalisation", "behaviour",
"recognise"). Keep established identifiers and external API names as they are
(`Initializer`, `WeakReference`). Use plain, complete sentences.

## Current limits

The [compatibility target](docs/roadmap.md#compatibility-target) is the language surface the project is aiming at. Do not treat it as implemented, and do not start work outside it. Do not assume that these work today. Each is unimplemented or deliberately limited:

- Generics beyond the specialised subset already in the compiler
- Exceptions (`try`, `catch`, `finally`)
- Struct arrays, struct return values, static struct fields, by-value struct
  parameters, struct-typed properties, printing a struct, `virtual`/`override`
  members on structs, interfaces on structs, and self-containing structs (all
  `ARC3001`)
- Enums, delegates, events, and indexers
- Lambdas, closures, `async`, and `await`
- Bitwise and shift operators, including single `&` and `|` (`ARC3001`).
- Null safety: a trailing `?` is accepted and ignored
- Reassigning a reference-typed parameter (rejected by the binder)
- Any BCL beyond `Console.Write` and `WriteLine`, `.Length`, and `WeakReference<T>`
- Threads, which the non-atomic refcounts do not support

The Roslyn analyser in `analyzers/ArcSharp.Analyzers` flags the unsupported
constructs and possible strong cycles from an ordinary `dotnet build`. It does
not replace compiler diagnostics. The compiler has to report the same rules,
because the analyser is optional. `ARC3xxx` is the shared catalogue.

The defects found outside this list, such as ignored field initialisers, are
recorded in the known-issues table of `docs/language-reference.md`.

See `docs/roadmap.md` for the milestone that delivers each item and the order
of the milestones.

## Git workflow

- New branch names must start with `ft/` for features, `bg/` for bug fixes,
  `doc/` for documentation, or `ch/` for other changes. These are the only
  allowed branch prefixes.
- Make small commits, each grouped by one effect on the code.
- Commit messages use a type prefix and a lower-case summary:
  `ft:` feature, `ch:` change, `bg:` bug fix, `doc:` documentation, `tst:` tests.
  Example: `bg: stop verify.ps1 aborting on compiler stderr output`. When a
  GitHub issue exists, use its number as the prefix instead (`#12: ...`).
- Do not add AI tool attribution: no `Co-authored-by` trailers, and no
  "made with" lines that name an assistant or a tool.
- Never commit generated files (`*.ll`, `*.exe`, `*.obj`, `bin/`, `obj/`) or
  IDE folders (`.idea/`).
- Run `tools\verify.ps1` before committing a compiler or runtime change.
- Do not commit or push unless the person you are working for has asked you to.
