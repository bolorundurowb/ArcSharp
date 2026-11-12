# AGENTS.md

Guidance for AI coding agents, and for people, working on ArcSharp. Read this
before changing the compiler, the runtime, the samples, or the documentation.

## What ArcSharp is

ArcSharp is a proof-of-concept compiler for a subset of C# 12. It compiles C#
source to textual LLVM IR, links that IR with a small C runtime, and produces a
native executable that manages memory with automatic reference counting (ARC)
instead of a garbage collector.

The compiler is written in C# 12 on .NET 8. The runtime is a single C file. The
only supported architecture is x86-64, and the actively verified target is
Windows x64 (`x86_64-pc-windows-msvc`).

The project values a small, provably balanced ARC model over performance or
breadth. When a change has to choose between "simple and correct" and "fast or
clever", choose simple and correct, and record the optimisation in
`docs/roadmap.md`.

## Quick start

### Prerequisites

- The .NET 8 SDK. `global.json` pins the repository to the newest installed
  8.0 SDK and does not roll forward to .NET 9 or later. Do not change the target
  framework or the pin unless the task asks for it.
- LLVM `clang`, by default at `C:/Program Files/LLVM/bin/clang.exe`. A Windows
  linker that clang can reach (Visual Studio Build Tools or the Windows SDK).
- PowerShell. `tools/verify.ps1` runs on both Windows PowerShell 5.1 and
  PowerShell 7.

### Commands

```powershell
# Build the compiler
dotnet build -c Release

# Compile and run one program (writes <name>.ll and <name>.exe next to -o)
dotnet bin/Release/net8.0/arcsharp.dll samples/inherit.cs -o $env:TEMP\inherit `
  --target windows --clang "C:/Program Files/LLVM/bin/clang.exe" `
  --runtime runtime/arc_runtime.c --run

# Emit LLVM IR only, for inspection
dotnet bin/Release/net8.0/arcsharp.dll samples/inherit.cs -o $env:TEMP\inherit --emit-llvm

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
| `src/Driver/Compilation.cs` | Orchestrates lexing, parsing, binding, emission, and the clang or llc invocation |
| `src/Lexing/Lexer.cs` | `TokenKind`, keywords, literals, `Diagnostic`, `DiagnosticSeverity` |
| `src/Syntax/Ast.cs`, `src/Syntax/Parser.cs` | AST node classes and the recursive-descent parser |
| `src/Binding/Symbols.cs` | `TypeSymbol`, `FieldSymbol`, `MethodSymbol`, layout and name mangling |
| `src/Binding/Binder.cs` | Name and type resolution, layout, vtables, interface selectors, property expansion, conversions |
| `src/Binding/BoundTree.cs` | Typed bound-tree nodes consumed by the emitter |
| `src/Binding/DefiniteAssignment.cs` | Conservative read-before-assignment analysis (warning `ARC2100`) |
| `src/Binding/CycleAnalysis.cs` | Strong-field graph and strongly connected components (warning `ARC3100`) |
| `src/CodeGen/Emitter.cs` | Bound tree to LLVM IR, with every ARC operation woven in |
| `runtime/arc_runtime.c` | Object header, retain and release, weak references, arrays, strings, type tests, console, accounting |
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

`ArcSharp.csproj` excludes `samples/**/*.cs` from compilation, so samples are
never built as part of the compiler. Keep it that way.

## The compiler pipeline

```
.cs files -> Lexer -> Parser (AST) -> Binder (bound tree, layouts, vtables)
          -> DefiniteAssignment (warnings) -> Emitter (.ll)
          -> clang (.ll + arc_runtime.c -> .exe) -> run -> [arc] accounting on stderr
```

Every file is lexed and parsed separately. The binder merges the type
declarations from all files into one flat program before binding. `using`
directives are skipped, and `namespace` names are discarded, so two types with
the same simple name collide even in different namespaces.

### Adding or changing a language feature

A feature usually touches every stage. Work through this list in order:

1. **Lexer**: add the `TokenKind` and, for a keyword, the `Keywords` entry.
2. **AST and parser**: add the node class to `Ast.cs` and parse it in
   `Parser.cs`.
3. **Binder**: add a case to `BindStatement` or `BindExpr`, produce a bound
   node from `BoundTree.cs`, and type-check it. Report errors with a diagnostic
   code (see "Diagnostics").
4. **Definite assignment**: if the feature reads or writes locals, or changes
   control flow, update `DefiniteAssignment.cs` so the analysis stays
   conservative.
5. **Emitter**: add a case to `EmitStmt` or `EmitR` and follow the ARC rules
   below exactly.
6. **Runtime**: if new runtime support is needed, add the C function **and**
   its matching `declare` line in `Emitter.EmitRuntimeDecls`.
7. **Sample and harness**: add a sample and an `Invoke-Sample` line (see
   "Testing").
8. **Documentation**: update `docs/language-reference.md`, the status in
   `docs/architecture.md` and `docs/roadmap.md`, `docs/verification.md`, and
   `README.md` (see "Documentation").

**Unhandled nodes fail silently. This is the most important pitfall in the
codebase.**

- `Binder.BindStatement` turns an unknown statement into an empty
  `BoundBlock`. The statement disappears.
- `Binder.BindExpr` turns an unknown expression into `Err()`, which is an
  error-typed null literal **with no diagnostic**.
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
  `arc_release` calls it when the strong count reaches zero.
- **Counts are non-atomic and the model is single-threaded.** Do not introduce
  threads or shared state without the atomic-refcount work from the roadmap.

Over-retaining is acceptable; an unbalanced retain or release is not. Do not
remove or "optimise away" retain and release pairs. Borrow and ownership
elision is planned as a separate, deliberate compiler pass
(`docs/roadmap.md` section 2).

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

`TypeInfo` has eight fields: name, instance size, deinit, vtable, vtable
length, itable, itable length, and base. It is declared in two places that must
match: the `%TypeInfo` type emitted at the top of every module in
`Emitter.Emit`, and `struct TypeInfo` in `runtime/arc_runtime.c`. Change both
together or neither. When documentation shows a different layout, the code is
correct; update the documentation.

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
  without updating `tools/verify.ps1`.
- Keep the runtime portable C with only the standard library. It is compiled
  with clang for Windows and with gcc or clang on Linux.
- Windows x64 uses the LLP64 model: C `long` is 32 bits there, while it is 64
  bits on Linux. Never use `long` or `%ld` for a C# `long` or for a pointer-sized
  value. Use `intptr_t` or `long long`, and format with `%lld` after a cast to
  `long long`.

### Source compatibility with Roslyn

ArcSharp source should also be valid C# that the normal .NET toolchain
accepts. Cycle breaking uses the standard `System.WeakReference<T>` for this
reason. The old `weak` field modifier still works only as a legacy alias, for
`samples/cycle_weak.cs` and `samples/weak_null.cs`. Do not use it in new code or
samples, and do not invent new non-standard syntax.

### Diagnostics

Each diagnostic has a stable code and a severity (`Diagnostic` in
`Lexer.cs`). The driver fails compilation when the error count is non-zero;
warnings fail it only with `-Werror`.

| Range | Stage | Codes in use |
|---|---|---|
| `ARC0xxx` | Lexer | `ARC0001` (default), `ARC0002` unterminated literal, `ARC0003` unsupported operator |
| `ARC1xxx` | Parser | `ARC1001` (default) |
| `ARC2xxx` | Binder | `ARC2001` (default), `ARC2002` unknown name, `ARC2010` conversion, `ARC2011` invalid `using` resource, `ARC2050` no entry point, `ARC2100` definite assignment (warning) |
| `ARC3xxx` | Shared catalogue | `ARC3001` unsupported construct (error), `ARC3100` possible strong cycle (warning). `ARC3200` and `ARC3300` are reserved in the catalogue for planned rules; do not allocate `ARC3xxx` for other errors. |

Never renumber or reuse an existing code. A new kind of error gets a new code
in the range for its stage. Binder diagnostics report column 0, and
definite-assignment warnings are reported at the enclosing method's line,
because bound nodes do not carry source positions.

## Testing

`tools/verify.ps1` is the only test gate. There is no CI yet. The harness builds
the compiler and compiles every sample with clang. It runs each executable and
checks two things:

1. The program's stdout contains an expected substring.
2. The `live=` count in the `[arc]` line equals the expected value.

It also compiles the must-fail samples and checks the expected diagnostic code,
checks that `ARC3100` appears for the leaking cycle samples and not for the
broken ones, and runs the analyser's xUnit project with `dotnet test`.

Run it after every change to the compiler or the runtime. All samples must
pass. `cycle_strong` and `cycle_struct` are expected to report `live=2`; those
leaks are deliberate, and they document that ARC cannot reclaim a strong cycle,
including one that runs through a struct field.

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

Report only what was actually run. If the .NET 8 SDK or clang is not available
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
- Target C# 12 and .NET 8. Do not use APIs or syntax from later versions.
- The compiler has no NuGet dependencies. Do not add one without a strong
  reason. Planned exceptions such as LLVMSharp are roadmap decisions, not
  incidental changes.

Match the surrounding code. The emitter builds IR with string interpolation
through the helpers `Inst`, `Do`, `Term`, and `Lbl`. Use them, because they
track the current basic block and the termination state.

Comments should explain constraints that the code cannot show, such as an LLVM
quirk, an ARC invariant, or a runtime layout dependency. Do not restate what
the code does.

## Documentation

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
| `docs/roadmap.md` | A roadmap item is delivered (mark it **[done]**) or the plan changes |
| `docs/verification.md` | Samples are added or changed, or the harness is re-run with different results |

Every example program in the user guides must compile and run with the
current compiler. When you change behaviour, re-run the affected examples and
update the output shown on the page. When a known issue is fixed, remove it
from `docs/language-reference.md` and from the roadmap's known-defects row.

Write prose in UK English (for example "finalisation", "behaviour",
"recognise"). Keep established identifiers and external API names as they are
(`Initializer`, `WeakReference`). Use plain, complete sentences.

## Current limits

Do not assume that these work. Each is unimplemented or deliberately limited:

- Generics (`Name<T>` is parsed only to recognise `WeakReference<T>`)
- Exceptions (`try`, `catch`, `finally`)
- Struct arrays, struct return values, static struct fields, by-value struct
  parameters, struct-typed properties, printing a struct, `virtual`/`override`
  members on structs, interfaces on structs, and self-containing structs (all
  `ARC3001`)
- Enums, delegates, events, and indexers
- Lambdas, closures, `async`, and `await`
- Bitwise and shift operators: a single `&` or `|`, and `<<` or `>>`, are lexer
  error `ARC0003`
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

See `docs/roadmap.md` for the priority and the planned design of each item.

## Git workflow

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
