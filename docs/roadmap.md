# ArcSharp — Production Readiness Roadmap

This document responds to the production-readiness feedback for ArcSharp. Each
section restates the gap, the design decision, and the concrete work required.
Items are prioritised as **P0** (required for any real use), **P1** (expected by
production users), or **P2** (important quality-of-life / performance work).

The current codebase is a proof-of-concept compiler for a C# 12 subset, with
textual LLVM-IR emission and a small C runtime. It accepts more than one source
file. The goal of this roadmap is to turn that proof of concept into a usable
native C# toolchain.

---

## Implementation progress

Work is landing in phases. Each phase is run-verified on a Windows x64
developer machine with `tools\verify.ps1`; there is no CI yet. The latest
results are in [Verification](verification.md).

**Phase 1 — correctness & tooling foundation (done):**

- **Diagnostics with codes + severities** (§6) — every diagnostic now carries a
  stable id (`ARC0001`…) and an Error/Warning/Info severity; the driver gates
  failure on error count and supports `-Werror`.
- **Multi-file compilation** (§6) — the driver accepts multiple `.cs` inputs and
  the binder merges type declarations across them before binding.
- **Definite-assignment analysis** (§5) — a conservative structured flow pass
  warns on locals read before assignment (promotable to errors via `-Werror`).

**Phase 2 — language breadth (in progress):**

- **`using` / `IDisposable`** (§1) — `using` statements lower to deterministic
  ARC cleanup: the resource is owned by a scoped local, `Dispose()` runs at
  scope exit on normal completion and on `return`, and the resource is released.
- **Structs** (§1) — struct code generation is implemented: inline value
  storage, value-semantics copy, reference-field retain/release, nested
  structs, and structs inside classes. The remaining restrictions are listed
  in §1.
- **Authoring diagnostics** (§7) — `ARC3001` for unsupported constructs and
  `ARC3100` for possible strong cycles are emitted by the compiler, backed by
  a shared catalogue and a Roslyn analyser with unit tests.

**Next phases (sequenced, not yet implemented):** generics (monomorphisation) →
ARC hardening (atomics, borrow elision) → backend upgrade (LLVM C API, debug
info, optimisation, multi-target) → BCL & threading.
Per-item design and "concrete first steps" are in the sections below.

Status key in the section tables below: **[done]** shipped in a phase above;
unmarked items are still planned.

---

## 1. Language — close the feature gap

| Feature                                            | Priority         | Decision / approach                                                                                                                                                                                                                                                                                                                                                                                                                               |
|----------------------------------------------------|------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Generics**                                       | P0               | Implement **monomorphisation** (like C++ templates / Rust / Swift generics). Every closed generic type (`List<int>`, `Dictionary<string, Foo>`) gets its own `TypeInfo`, vtable, and `__deinit`. The current "uniform 8-byte slot" object model stays; generic value-type arguments are specialised by size. Generic methods are instantiated at call sites. Constraint checking (`where T : class`, `where T : IFoo`) is enforced in the binder. |
| **Exception handling (`try`/`catch`/`finally`)**   | P0               | Use **DWARF on Linux/macOS** and **SEH/Table-based exception handling on Windows**. Every cleanup scope (statement temps + locals) must emit a landing pad that releases live reference-typed locals and temporaries. `finally` blocks are inlined on both normal and unwind paths. `catch` requires RTTI (Section 5). `Exception` becomes the root class of a small exception hierarchy.                                                         |
| **Closures / lambdas**                             | P1               | Lower lambdas to **heap-allocated closure objects** that capture variables by reference into fields. ARC owns the closure. Capture `this` as a weak field by default unless explicitly strong (Swift-style `[strong self]`), to avoid the most common cycle. Anonymous methods and expression-bodied lambdas (`x => x + 1`) are parsed and bound to delegate-compatible types.                                                                    |
| **`async`/`await`**                                | P1               | Lower to a **state machine class** (like Roslyn). The compiler generates `IAsyncStateMachine`-like machinery manually. ARC retain/release must be threaded across suspension points: every captured local/reference field in the state machine is managed, and the state machine itself is ARC-owned. Initially target single-threaded continuations; thread-pool dispatch is P2.                                                                 |
| **Value types with reference fields**              | P0 **[done]**    | Struct code generation copies value fields and **retains** reference fields; assignment and teardown **release** them. Struct methods receive `this` by managed reference. The remaining restrictions (arrays of structs, struct return values, static struct fields, by-value struct parameters, struct-typed properties, printing, `virtual`/`override` members, interfaces, and self-containment) are reported as `ARC3001`. |
| **Properties, indexers, events, delegates, enums** | P0/P1            | **Properties** (P0) **[done]** lower to backing field + getter/setter method calls; auto-properties are implemented. **Indexers** (P1) are instance properties with parameters. **Events** (P1) are multicast delegate fields with `add`/`remove` accessors. **Delegates** (P1) are single/multicast function objects carrying a target reference and function pointer. **Enums** (P1) are integer-typed with named constants.                    |
| **`using` / `IDisposable`**                        | P0 **[done]**    | Lowered to deterministic ARC cleanup without full `try`/`finally`: the resource value is owned by a scoped local, `Dispose()` runs at scope exit (block completion and `return`, innermost first when nested) guarded against `null`, then the resource is released. `IDisposable` is structural — any type with a parameterless instance `void Dispose()` method can be a resource; an interface-typed resource dispatches through the itable. When exception handling lands, unwinding will adopt the same cleanup paths (Section 1). |
| **`ref` / `out` / `in` parameters**                | P0 **[done]**    | Generalised to **managed references** (`T&`) in the bound tree and LLVM IR. `ref` allows read/write aliasing of variables/fields/elements; `in` is read-only `ref`; `out` supports both existing-lvalue and declaration-via-inference (`out var` in `WeakReference<T>.TryGetTarget`). Reference-typed `ref` parameters do not change ownership — they borrow the slot.                                                                            |
| **Full numeric tower**                             | P0/P1 **[done]** | `byte`/`sbyte`/`short`/`ushort`/`uint`/`ulong` added. `float`/`double` already present. Integer conversions (`trunc`/`sext`/`zext`) and signed/unsigned arithmetic/comparisons operational. `decimal` remains emulated (omitted until BCL). Overflow-checked arithmetic (`checked`/`unchecked`, Section 5) is follow-up work.                                                                                                                     |

### Concrete first steps for language features

1. **Add `byte`, `short`, `float`, `double` literals and operators** — **[done]** lexer, binder, emitter, runtime formatters.
2. **Generalise `out` to `ref`/`in`** — **[done]** introduced `ByRefArgExpr`/`BoundByRefArg`, by-ref parameter emission via `T*` / `T&`.
3. **Properties** — **[done]** added `PropertyDecl` to AST/binder; lowered auto-properties to private backing field + `get_Prop`/`set_Prop` methods.
4. **`using`** — **[done]** added `UsingStmt` to the parser; lowered to
   scoped-ownership cleanup: dispose at scope exit and on `return`, then
   release (`ARC2011` when the resource is not disposable).
5. **Generics** — add generic parameter lists to type/method declarations, build a substitution map, and instantiate closed types during binding.

---

## 2. ARC model — from "correct" to "efficient"

| Change                          | Priority | Decision / approach                                                                                                                                                                                                                                                                                                                                                                                                           |
|---------------------------------|----------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Ownership / borrow analysis** | P0       | Add a dataflow pass over the bound tree that classifies each reference value as **owned (+1)**, **borrowed**, or **dead**. Elide `arc_retain`/`arc_release` pairs when: (a) a local is read multiple times within a basic block, (b) a reference is passed as a read-only argument and not stored, (c) a local-to-local copy can move ownership instead of retain/release. This is the single biggest throughput win.         |
| **Atomic refcounts**            | P0       | Make `ObjHeader.strong` and `ObjHeader.weak` atomic (`_Atomic intptr_t` / `stdatomic.h`, lowered to `lock xadd`/`cmpxchg` on x64). Provide a compile-time or runtime flag to disable atomics for single-threaded programs. Atomic ops are used for all retain/release; `arc_assign_take` becomes a full atomic swap.                                                                                                          |
| **Backup cycle collector**      | P1       | Add a **tracing backup collector** that runs on demand or when live object growth exceeds a threshold. It roots globals, locals, and thread stacks, traces strong references, and reclaims unreachable cycles. It must coordinate with ARC: objects are only freed when both strong count == 0 and the collector marks them unreachable. Start with a stop-the-world implementation; incremental/concurrent collection is P2. |
| **Optimised weak table**        | P1       | Replace per-object weak-field slots with a **global weak table** keyed by target object address. On dealloc, zero all weak entries pointing to the object. This avoids the current "weak count keeps the allocation alive" design and enables safer `WeakReference<T>` semantics at scale.                                                                                                                                    |

### Concrete first steps for ARC efficiency

1. Add `ARC_ATOMIC` build flag to `runtime/arc_runtime.c` and implement atomic retain/release.
2. Implement the **borrow pass** as a separate compiler phase before LLVM emission: mark `BoundLocal`/`BoundParam` reads as borrowed when they are not stored or returned.
3. Design the weak-table API (`arc_weak_table_add`, `arc_weak_table_remove`, `arc_weak_table_get`) and switch `WeakReference<T>` lowering to use it.
4. Prototype a simple cycle collector behind a flag; use the existing ARC accounting hooks (`g_alloc`/`g_dead`) for testing.

---

## 3. Runtime / BCL — standard library

| Component                   | Priority | Decision / approach                                                                                                                                                                                                                                                                    |
|-----------------------------|----------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Collections**             | P0       | Implement `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Stack<T>`, `Queue<T>` as ArcSharp-source libraries under ARC rules. Internally use arrays (`T[]`) and weak/strong references as appropriate. These live in a new `corelib/` directory and are compiled together with user code. |
| **String operations**       | P0       | Expand the runtime string API: `Substring`, `Split`, `IndexOf`, `Replace`, `ToString()` for primitives, culture-invariant comparisons. Eventually implement `StringBuilder`.                                                                                                           |
| **I/O**                     | P1       | File streams, networking, and a `Stream` abstraction. Initially shim to C stdio / sockets; later provide pure ArcSharp wrappers.                                                                                                                                                       |
| **`Span<T>` / `Memory<T>`** | P1       | `Span<T>` is a stack-only `byref`-like struct (pointer + length, never ARC'd). `Memory<T>` is a heap object holding a reference to an array/owner. This is critical for high-performance code and interop.                                                                             |
| **Reflection / `Type`**     | P1       | Embed enough metadata in `TypeInfo` to support `typeof(T)`, `GetType()`, basic `is`/`as`, and enumeration of fields/methods. Keep it minimal; full reflection is expensive under ARC.                                                                                                  |
| **Threading**               | P1       | Once refcounts are atomic, add `Thread`, `Task<T>`, `Monitor`/`lock`, and basic synchronisation. Define ARC lifetime rules for cross-thread sharing (prefer move/unique ownership; shared references require atomic counts).                                                           |

### Strategy choice

**Re-implement a minimal corelib in ArcSharp C#** with a thin C runtime shim for platform calls (file I/O, threads, console). This keeps ARC semantics uniform across library and user code and avoids impedance mismatch with a foreign GC. Reuse well-tested algorithms from the BCL where possible.

### Concrete first steps for BCL

1. Create `corelib/` directory with `System.String`, `System.Collections.Generic.List<T>`, `System.IDisposable`.
2. Extend `runtime/arc_runtime.c` with string helpers (`arc_str_substring`, `arc_str_index_of`, etc.).
3. Add `typeof(T)` intrinsic and runtime type metadata in `TypeInfo`.

---

## 4. Code generation — production backend

| Change                                          | Priority | Decision / approach                                                                                                                                                                                                                                                                           |
|-------------------------------------------------|----------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **LLVM C API / object emission**                | P0       | Replace textual IR string-building with the **LLVMSharp / libLLVM C API** to construct modules and emit `.ll`, `.bc`, or `.obj` directly. Eliminate the `llc` subprocess dependency.                                                                                                          |
| **Debug info (DWARF/PDB)**                      | P0       | Emit `llvm.dbg.cu`, `DILocation`, `DILocalVariable`, and line tables so source-level debugging works in WinDbg/VS Code/lldb. Map ArcSharp source files, lines, and columns to LLVM debug metadata.                                                                                            |
| **Optimisation pipeline**                       | P0       | Run the standard LLVM pass pipeline (`PassBuilder` `-O2`/`-O3`) on generated modules. Combine with ARC borrow analysis so LLVM can elide redundant loads/stores without breaking retain/release invariants.                                                                                   |
| **Multi-target**                                | P1       | Parameterise target triple and data layout: Windows (`x86_64-pc-windows-msvc`), Linux (`x86_64-pc-linux-gnu`), macOS (`x86_64-apple-macosx` / `arm64-apple-macosx`). Use LLVM target machine for code generation. ABI lowering (Windows x64 vs System V AMD64) must match calling convention. |
| **Remove `arc_report()` accounting in release** | P1       | Make accounting conditional on a debug/verification build. In release, omit `arc_report()` and the global counters to remove overhead and binary bloat.                                                                                                                                       |

### Concrete first steps for code generation

1. Spike LLVMSharp integration: build a module in memory and emit `.ll` text, then `.obj`.
2. Add `DILocation` emission tied to `Node.Line`/`Column` in the bound tree.
3. Add `--opt` / `-O` CLI flag that runs the LLVM pass pipeline.
4. Refactor target selection into a `TargetInfo` record (triple, data layout, ABI).

---

## 5. Correctness & safety

| Gap                     | Priority      | Decision / approach                                                                                                                                                                                                                                                                                                                                                 |
|-------------------------|---------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Null safety**         | P0            | Enforce the existing `?` annotation. Reference types without `?` are non-null; assign `null` only to nullable types. Add definite-assignment checks (below) to ensure non-null locals are initialised.                                                                                                                                                              |
| **Type soundness**      | P0 **[done]** | Runtime type checks for `is`/`as` implemented via `arc_is_instance()` with parent-chain walk in `TypeInfo`. Interface downcasts unchecked at runtime; array casts verify `TypeInfo` compatibility via `arc_array_store_check`. `TypeInfo` extended with `base` pointer.                                                                                             |
| **Array covariance**    | P0 **[done]** | Arrays store their element `TypeInfo` at offset 32 (offset 40 for elements). Every store to a reference-type array performs a runtime assignability check via `arc_array_store_check` that throws `ArrayTypeMismatchException` on mismatch. Value-type arrays are invariant. `S[]` → `T[]` binder conversion allowed when `S` is reference-compatible with `T`.     |
| **Definite assignment** | P0 **[done]** | A conservative structured flow pass (`DefiniteAssignment.cs`) tracks whether each local is definitely assigned before use, with completes-normally tracking so `if`/loop joins don't false-positive. Currently reports `ARC2100` as a warning (promotable via `-Werror`); tightening to a hard error and extending to `out`-parameter exit paths is follow-up work. |
| **Overflow checking**   | P1            | Support `checked`/`unchecked` contexts. Integer arithmetic in `checked` emits `llvm.sadd.with.overflow` / `smul.with.overflow` and branches to an overflow exception. `unchecked` uses plain LLVM integer ops.                                                                                                                                                      |
| **Known defects**       | P0            | Fix the defects recorded in [Known issues](language-reference.md#known-issues): ignored field initialisers, abstract methods and static auto-properties that produce invalid IR, same-arity overloads, unchecked class casts, `WeakReference<T>` locals, reference equality for `string ==`, and `new` on an abstract class. Each fix lands with a sample that checks its output. |
| **Verification suite**  | P0 **[partly done]** | The harness now runs 23 samples, 14 must-fail diagnostics, 3 command-line checks, 4 cycle checks, and 62 analyser unit tests. Grow to a **thousands-of-tests conformance suite**. Adopt or write a test harness that runs ArcSharp output against expected output and ARC accounting. Target the C# spec incrementally; include negative tests (must-fail diagnostics), which the harness supports. |

### Concrete first steps for correctness

1. Implement definite-assignment analysis in the binder. — **[done]**
2. Add `is`/`as` operators and runtime type-check helper in `arc_runtime.c`. — **[done]**
3. Add array-store type check for reference-element arrays. — **[done]**
4. Set up an xUnit/NUnit test project with golden-file tests for samples and targeted unit tests for binder diagnostics. — **[partly done]** the analyser has an xUnit project; binder and golden-file tests remain.

---

## 6. Tooling

| Tool                        | Priority             | Decision / approach                                                                                                                                                                                                           |
|-----------------------------|----------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Incremental compilation** | P1                   | Cache per-type and per-method compiled LLVM modules on disk keyed by source hash. Only recompile changed files/methods.                                                                                                       |
| **Multi-file projects**     | P0 **[partly done]** | Compiling multiple `.cs` files is implemented: the driver accepts several inputs and the binder merges type declarations across them. A project file format (`arcproj.json` or MSBuild-compatible subset) is still to do.     |
| **Diagnostic quality**      | P0 **[partly done]** | Error codes (`ARC0001`…) and severity levels (error/warning/info) are implemented, with error-count gating and `-Werror`. Richer messages with source snippets and column tracking through every phase remain follow-up work. |
| **IDE integration (LSP)**   | P2                   | Implement a **Language Server Protocol** server exposing document symbols, diagnostics, go-to-definition, and autocomplete. Reuse the binder's symbol tables.                                                                 |

### Concrete first steps for tooling

1. Change the driver to accept multiple input files and a `--project` option.
2. Introduce a `DiagnosticDescriptor` with id/severity/message-template.
3. Expose a `--verify` mode that compares output and ARC accounting against expected files.
4. Begin an LSP server project that hosts the lexer/parser/binder as a library.

---

## 7. Roslyn analyser — warnings inside an ordinary C# project

### Decision

Ship both, with one shared set of rules. A NuGet analyser is not a replacement
for compiler diagnostics, and compiler diagnostics are not a replacement for
the analyser.

ArcSharp source is meant to stay valid C#, so the program people actually
edit is built by Roslyn (`dotnet build`, Visual Studio, VS Code). That build
accepts generics, `try`, `async`, lambdas, and a strong parent/child cycle,
then produces a program ArcSharp will reject, ignore, or leak. The author
finds out only later, by running `arcsharp`. An analyser referenced from the
project reports the same problems as squiggles and `dotnet build` warnings,
before anyone invokes the native compiler.

The compiler still has to report them itself. The analyser is optional, it can
be missing or older than the compiler, and only the compiler knows what this
version will lower. Before this rule the compiler was the weaker of the two at
this job: an unhandled statement became an empty block and an unhandled
expression became an error value with no diagnostic
([`AGENTS.md`](../AGENTS.md)). A strong cycle compiled cleanly and leaked, which
`samples/cycle_strong.cs` records on purpose. Warning only in an analyser would
leave `arcsharp` itself silent, so both report `ARC3001` and `ARC3100`.

So the rule catalogue lives in one place, and both tools emit it. The
analyser package version matches the compiler it describes. When a feature in
this roadmap is marked **[done]**, the matching rule is removed or downgraded
in the same release. A stale analyser that warns about a feature the compiler
now accepts is a bug.

This does not change the cycle-collector plan in section 2. A warning tells
the programmer about a cycle. `WeakReference<T>` breaks it. A collector would
reclaim the ones nobody broke. Those are three different tools.

### What each rule is for

| Rule | Severity | Where it fires | Why |
|---|---|---|---|
| **Unsupported construct** | Error (`ARC3001` and following) | Compiler and analyser — implemented | Generics other than `WeakReference<T>`, `try`/`catch`/`finally`, `throw`, `async`/`await`, `yield`, lambdas, local functions, delegates, events, enums, indexers, records, pattern matching, LINQ, `lock`, `unsafe`, `stackalloc`, bitwise operators, `params`, optional and named arguments, and the struct restrictions. The compiler must stop dropping these on the floor. |
| **Possible strong cycle** | Warning (`ARC3100`) | Compiler and analyser — implemented | A set of classes whose strong fields reach each other can form a runtime cycle. ARC leaks it. `WeakReference<T>` and the legacy `weak` modifier are not edges in this graph. |
| **ARC and CLR disagree** | Info (`ARC3200`) | Planned | `GC.Collect`, `GC.KeepAlive`, finalisers that assume a collector, and `TryGetTarget` used as if it waited for a GC. The code compiles on both, and the timing does not match. |
| **Threads** | Warning (`ARC3300`) | Planned | `Thread`, `Task`, `lock`, and other shared cross-thread use. Refcounts are non-atomic, so this is unsound rather than merely unimplemented. |

`ARC3xxx` is the shared catalogue. Do not spend those codes on ordinary
lexer, parser, or binder errors.

Unsupported constructs are errors because the program cannot mean what it says
under ArcSharp. A possible cycle is a warning because the program is legal and
sometimes intentional: `cycle_strong` documents the leak, and the harness does
not pass `-Werror`. A tree or a singly linked list is also legal and does not
leak. The warning has to say so.

### What a cycle warning can and cannot know

The check looks at declared field types, including arrays of a class, static
fields, and class references reached through a struct field. It builds a graph
of strong edges and warns once per strongly connected component. That is a
statement about *types*, not about a particular object graph.

It will warn on a real back-edge (`Parent.child` and `Child.owner` both
strong), which is the leak in `cycle_strong`. It will also warn on a binary
tree (`Node` with `left` and `right`) and on a singly linked list (`Node`
with `next`), which do not leak unless some instance is later wired into a
loop. Those are false positives. The message says that, and the suppression is
a normal `#pragma warning disable ARC3100` or `[SuppressMessage]` on the type.
There is no separate "tree" heuristic: a self-typed field is exactly how both
a tree and a doubly linked list are written, and a doubly linked list does
leak.

It will miss a cycle that is only visible at run time: a field typed as
`object` or as a base class, a collection that does not exist yet, or two
objects assigned into each other through locals with no long-lived field. No
static check proves that a cycle will happen, and no static check proves that
one will not. The runtime accounting line remains the evidence.

A suggested fix belongs in the message ("make the back-edge a
`WeakReference<T>`"), not in an automatic rewrite. Changing a field to
`WeakReference<T>` changes its type and every reader. A code fix can come
after the diagnostic is trustworthy.

### Package

`analyzers/ArcSharp.Analyzers`, packaged as a Roslyn analyser. A consumer adds
it with a `PackageReference` and an analyser asset, and keeps building with
the normal SDK. It does not invoke the native toolchain and it does not change
the program's semantics.

The compiler project stays free of NuGet dependencies. The analyser project is
separate and may reference the Roslyn analyser packages; that is the one
planned exception, because an analyser cannot be loaded without them. Target
`netstandard2.0` so Visual Studio and `dotnet build` can both load it.

Prefer one shared descriptor assembly, also `netstandard2.0`, referenced by
the analyser and by the compiler, so the id, severity, and message cannot
drift. That is implemented as `analyzers/ArcSharp.Diagnostics`; it has no
Roslyn reference, so the compiler stays NuGet-free. This section is the
specification; the implementation should not grow a further copy of its own.

### Concrete first steps

1. Make the binder and the parser report `ARC3001` (and a distinct code per
   construct once the messages settle) for syntax they currently drop. A
   sample that must fail to compile belongs next to `tools/verify.ps1`; the
   harness today only checks programs that run. — **[done]**
2. Add `ARC3100` in the binder from the strong-field graph. `cycle_strong`
   should warn; `cycle_weakref` and `cycle_weak` should not. Keep the warning
   out of `-Werror` for those samples. — **[done]**
3. Create `ArcSharp.Analyzers` with the unsupported-syntax rules and
   `ARC3100`, plus unit tests that feed Roslyn a small compilation. This can
   be the repository's first test project; it does not need clang. — **[done]**
4. Publish the package only when its rule list is generated from the same
   catalogue as the compiler.

---

## Suggested execution order

A pragmatic path from PoC to production:

1. **Correctness foundation** — **[done]** definite assignment, `is`/`as`, array covariance. Null safety and a conformance test harness remain.
2. **Language breadth** — **[partly done]** `ref`/`out`/`in`, properties, the
   full numeric tower, `using`/`IDisposable`, and value types with reference
   fields done. The remaining struct restrictions and the rest of the language
   gaps (section 1) remain.
3. **Authoring diagnostics** — **[done]** unsupported syntax is reported as
   `ARC3001`, possible strong cycles as `ARC3100`, and the Roslyn analyser in
   `analyzers/ArcSharp.Analyzers` mirrors those rules with unit tests
   (section 7).
4. **Generics** — monomorphisation engine; re-implement collections once generics land.
5. **ARC hardening** — atomic refcounts, borrow/ownership elision, weak table, cycle collector.
6. **Backend upgrade** — LLVM C API, debug info, optimisations, multi-target.
7. **BCL & threading** — corelib, `Span<T>`/`Memory<T>`, `Task<T>`, reflection.
8. **Tooling** — multi-file projects, incremental build, diagnostics, LSP.
9. **Async / closures** — state machines and closure lowering.

---

## Files touched by this roadmap

- `src/Lexing/Lexer.cs` — new tokens/literals.
- `src/Syntax/Ast.cs`, `src/Syntax/Parser.cs` — new statement/expression/member nodes.
- `src/Binding/Symbols.cs`, `src/Binding/BoundTree.cs`, `src/Binding/Binder.cs` — generics, nullability, definite assignment, `is`/`as`, unsupported-syntax errors, possible-cycle warnings.
- `src/CodeGen/Emitter.cs` — new IR patterns, debug locations, borrow analysis.
- `runtime/arc_runtime.c` — atomic ops, weak table, cycle collector, string/BCL helpers.
- `corelib/` — new ArcSharp standard library.
- `tests/` — conformance suite (planned).
- `analyzers/ArcSharp.Diagnostics/` — shared `ARC3xxx` ids and messages.
- `analyzers/ArcSharp.Analyzers/` — Roslyn analyser for the supported subset and possible strong cycles (section 7).
- `analyzers/ArcSharp.Analyzers.Tests/` — xUnit tests for the analyser.
- `ArcSharp.Lsp/` — new language server.
