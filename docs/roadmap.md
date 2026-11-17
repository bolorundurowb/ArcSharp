# ArcSharp — Roadmap

ArcSharp's target is a hybrid memory manager behind a Roslyn frontend.

```
C# source
    │  existing syntax
    ▼
Roslyn
    │  parse and semantic analysis
    ▼
Ownership and lifetime analysis
    │  a proved lifetime skips retain and release;
    │  anything else uses ARC
    ▼
LLVM
    │  native code
    ▼
Minimal runtime
    allocation, reference counts, weak references
```

The compiler does not do this yet. It still has its own lexer, parser, and
binder, and every heap object is reference-counted. This document is the plan.
Behaviour that already ships is marked **[done]**. Nothing else on this page
is something the compiler does today.

Items are prioritised as **P0** (required for the next milestone), **P1**
(expected once that milestone is in place), or **P2** (later quality work).

---

## Decisions

These are settled. Later sections follow them.

| Decision | Choice |
|---|---|
| **Fallback** | When a lifetime cannot be proved, the compiler inserts ARC and the program still compiles. There is no new ownership syntax. |
| **Representation** | Every heap object keeps the header it has today (`strong`, `weak`, `TypeInfo*`). A proved lifetime skips `arc_retain` and `arc_release`. An escape (a store into a field or array, or a call the analysis cannot see) starts using the count on that same object. Identity does not change, so reference equality and weak references stay valid. A second, headerless layout is the wrong shape for C#: promotion would have to copy the object or reserve the header anyway. |
| **Frontend** | Roslyn parses and binds. The hand-written lexer, parser, and name resolution are removed once the Roslyn path passes the same verification as the compiler does now. Layout, vtables, the lifetime pass, the LLVM emitter, and the C runtime stay ArcSharp's. |
| **Intermediate representation** | The ownership tree between Roslyn and LLVM. Stock CIL is not a stage in the pipeline. CIL emitted for the CLR no longer says which store escapes or when the last strong reference dies, and both the lifetime pass and deterministic finalisation need those facts. |
| **Order** | The Roslyn transition comes before any new language feature. The lifetime pass is the next compiler milestone after that transition, and it is written once against the Roslyn tree. |

Also settled, and recorded so they are not relitigated in the backlog:

- **Cycles are permitted.** This is a closed architectural decision. The compiler warns with `ARC3100` when a cycle is visible in the type graph. It does not reject the program. If the developer, or a library they use, ignores that warning, the cycle leaks, its finalisers do not run, and that leak is the documented result. There is no backup collector. `WeakReference<T>` is how a cycle is broken.
- Finalisation is part of the compatibility profile, specified in the [memory model](memory-model.md#finalisation). It runs synchronously when the last strong reference is released, on that thread, before the object's children are released, derived class before base. Lexical scope is not the trigger. `Dispose()` stays separate. Resurrection and `GC.ReRegisterForFinalize` are rejected. `GC.Collect` and `GC.WaitForPendingFinalizers` are documented no-ops with info `ARC3200`. The compiler may delete a redundant retain, and it must not move a finaliser past that last release.
- The current compiler does not accept `~T()` yet. The Roslyn transition keeps rejecting it. Implementing the contract is the milestone immediately after the lifetime pass.
- Threads, atomic counts, exceptions, closures, and `async` wait until the lifetime pass exists. Nullable annotations stay ignored until then.
- `arc_report()` stays a verification aid. The harness depends on the `live=` line.
- Stack allocation of a value that never escapes is a later optimisation. It is not part of the lifetime pass, and it must not become a second object representation for values that do escape.

## Sequencing

1. **Roslyn transition.** Port the behaviour the compiler has today. No new language feature until `tools\verify.ps1` is green on that path.
2. **Lifetime pass.** Single header, ARC only where the proof fails. Destruction stays at the last strong release, so a later finaliser has a fixed point to run.
3. **Deterministic finalisers.** Implement the [finalisation contract](memory-model.md#finalisation). This is the first language feature after the Roslyn transition, and it follows the lifetime pass.
4. **The compatibility target below.** Sections 1 to 7 are how that matrix is reached. They are not a plan to implement the whole language or the whole framework. Features that create a hidden lifetime (delegates, `async`, exceptions) are lowered once, under the hybrid rules and the finalisation contract.

## Compatibility target

This is the language and library surface ArcSharp is aiming at. It is not what the compiler accepts today. The [language reference](language-reference.md) is the current subset. Anything outside this table is not scheduled. In particular, the rest of the .NET base class library, a tracing collector, and CLR resurrection are not targets.

| Feature | Target | Today | Notes |
|---|---|---|---|
| Classes, structs, records | Full | Classes, and structs with the restrictions in section 1. Records are rejected. | Ordinary C# semantics. A record lowers to a class with the members C# would synthesise. |
| Interfaces, inheritance | Full | Single inheritance, interfaces, virtual tables, and interface tables. A base interface list is parsed and then ignored. | Native dispatch tables. |
| Generics | Full | Monomorphisation, basic inference, and constraints for the supported subset. | Each closed type is specialised ahead of time. A single shared representation for all type arguments is not the plan. |
| Delegates, events | Full | Rejected (`ARC3001`). | The capture is an ARC object. Strong captures stay strong, and `ARC3100` still applies. |
| LINQ | Full | Rejected. | A library written against delegates, generics, and the collections in section 3. Query syntax lowers to those methods. |
| `async` / `await` | Full | Rejected. | A state machine whose suspended locals follow the ARC rules. Continuations stay on the releasing thread until the threading protocol in section 2 exists. |
| Exceptions | Full | Rejected. | Every unwind path releases live references. Finalisation and `using` both run on that path. |
| `IDisposable` / `using` | Full | `using` runs `Dispose()` on the normal completion path, then releases the resource. No exception unwinding yet. | Disposal stays an explicit call. A finaliser does not call `Dispose()`. |
| `Span<T>` / `ref` struct | Full | A `ref` that would outlive the call is rejected. | The existing escape rules stay. Stack storage does not, by itself, keep a heap object alive. |
| `WeakReference<T>` | Full | Supported, including as a field, a local, and a generic argument. | The native weak reference. It does not keep the target alive. |
| Reflection | Limited | Not implemented. | Metadata the compiler has already emitted: type names, bases, fields, and methods. Not a runtime that can load or invoke arbitrary framework code. |
| `dynamic` | Limited | Rejected. | Accepted only when the call binds to a member this compiler can already lower. The CLR runtime binder is not a target. |
| `unsafe` / pointers | Limited | Rejected. | A pointer into an ARC object must not outlive the lifetime the compiler has proved for that object. Pointer arithmetic does not bypass a retain, a release, or a finaliser. |

Enums, indexers, the remaining struct forms, and checked arithmetic stay in section 1. They are part of making the full rows ordinary C#, not a second target. Threads stay outside this table, gated on the ownership protocol in section 2.

---

## Already landed

Each of these was run on Windows x64 with `tools\verify.ps1`. There is no CI
yet. The latest results are in [Verification](verification.md).

- Diagnostics with stable ids and severities, `-Werror`, and multi-file compilation.
- Definite-assignment warnings (`ARC2100`).
- `using` / `IDisposable`, structs with reference fields, `ref` / `out` / `in`, properties, and the integer and floating numeric tower, with the restrictions in section 1.
- `ARC3001` and `ARC3100`, shared with the Roslyn analyser.
- Generics for the supported subset: monomorphisation, basic inference, and constraints.
- Interior-reference owner pins for the duration of a call, and the opt-in leak report.

Owner lifetimes for the managed references that exist today are in place. Allocation stacks in the leak report still wait on debug info.

Status key in the tables below: **[done]** means it has shipped. Unmarked items are planned, and language items are blocked on the Roslyn transition.

---

## Roslyn transition

This is the current milestone. New language work does not start until it has landed.

The aim is a compiler that accepts today's subset, rejects the same programs with the same diagnostic codes, and produces the same ARC accounting. The emitter still retains every read. The lifetime pass is the following milestone, so this one must not change memory behaviour.

| Step | What changes |
|---|---|
| **Dependency** | Reference `Microsoft.CodeAnalysis.CSharp` from the compiler, at the same 4.8 line the analyser already uses. This is the NuGet dependency the compiler is allowed to take. The shared catalogue stays in `analyzers/ArcSharp.Diagnostics`, which has no Roslyn reference. |
| **Reference surface** | Give Roslyn metadata for the types ArcSharp actually implements (`object`, `string`, the numeric types, arrays, `Console`, `WeakReference<T>`, and a parameterless `Dispose()`). Binding against the real BCL would accept APIs this compiler cannot lower. |
| **Lowering** | Drive the driver from Roslyn's syntax tree and semantic model. Lower `IOperation` into the existing bound tree, which remains the ownership representation. Do not emit CIL and read it back. Keep the emitter, the runtime, layouts, and vtables. |
| **Diagnostics** | Roslyn reports parse errors. The negative samples still expect ArcSharp codes (`ARC3001`, `ARC3100`, `ARC2015`, and the others the harness checks). Re-emit those codes from the lowering. |
| **Removal** | When `tools\verify.ps1` passes on this path, delete the hand-written lexer, parser, and name-resolution binder. Keep definite assignment, cycle analysis, layout, and emission until each has a checked replacement. |

During this milestone:

- Do not add a language feature to the hand-written parser. That work would be thrown away.
- A defect in the emitter or the runtime may still be fixed. That code survives the port.
- A defect in the lexer or the parser is fixed in the Roslyn lowering instead.

---

## 1. Language — close the feature gap

This backlog is how the [compatibility target](#compatibility-target) is reached. It resumes after the [Roslyn transition](#roslyn-transition), the lifetime pass, and deterministic finalisers. Do not implement these rows on the hand-written parser, and do not add language or library surface that the target leaves out.

| Feature                                            | Priority         | Decision / approach                                                                                                                                                                                                                                                                                                                                                                                                                               |
|----------------------------------------------------|------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Generics**                                       | P0 **[done]**    | Implemented **monomorphisation** for the supported subset. Every closed generic type (`Box<int>`, `Pair<string>`) gets its own `TypeInfo`, vtable, and `__deinit`. The current "uniform 8-byte slot" object model stays; generic value-type arguments are specialised by size. Generic methods are instantiated at call sites. Constraint checking (`where T : class`, `where T : IFoo`) is enforced in the binder. |
| **Exception handling (`try`/`catch`/`finally`)**   | P0               | Use **DWARF on Linux/macOS** and **SEH/Table-based exception handling on Windows**. Every cleanup scope (statement temps + locals) must emit a landing pad that releases live reference-typed locals and temporaries. `finally` blocks are inlined on both normal and unwind paths. `catch` requires RTTI (Section 5). `Exception` becomes the root class of a small exception hierarchy.                                                         |
| **Closures / lambdas**                             | P1               | Lower lambdas to **heap-allocated closure objects** that capture variables by reference into fields. ARC owns the closure. Preserve ordinary C# strong captures, including `this`; do not add weak-by-default captures or non-standard syntax. The cycle policy is already settled: strong captures stay strong, `ARC3100` warns, and `WeakReference<T>` is how a cycle is broken. There is no backup collector. Anonymous methods and expression-bodied lambdas (`x => x + 1`) are parsed and bound to delegate-compatible types.                                                                    |
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
5. **Generics** — **[done]** generic parameter lists, syntax substitution, cached
   closed types and methods, basic inference, and constraints. Remaining work:
   virtual/interface generic methods, variance, full overload inference, and
   additional constraint forms.

---

## 2. Memory model — hybrid lifetimes

The Roslyn transition keeps today's rule: every evaluated reference is retained to +1. This section is the milestone after that transition. It does not change the object header.

| Change                          | Priority | Decision / approach                                                                                                                                                                                                                                                                                                                                                                                                           |
|---------------------------------|----------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Interior-reference owner lifetimes** | P0 **[done]** | A `ref`/`out`/`in` argument, and a struct method's `this`, retains the heap object that contains the slot until the call returns, including when the callee drops every named reference to that owner. Stack slots, static fields, and borrows from the caller are not pinned. A weak slot is rejected (`ARC2015`). An `in` parameter's own storage is read-only. Ref locals, ref returns, and any other reference that would outlive the call are rejected (`ARC3001`) until a proof covers that longer lifetime. Stack confinement alone does not keep heap storage alive. |
| **Lifetime pass** | P0       | After the Roslyn transition, add a dataflow pass over the bound tree. A reference whose lifetime is proved is borrowed: the emitter skips `arc_retain` and `arc_release` for it, and destroys it at the end of that lifetime. Anything the pass cannot prove is shared: the existing +1 rules apply, and the object dies when its strong count reaches zero. A store to a field or array, or a call the pass cannot see, is the promotion point, and it uses the count already in the header. There is one layout. The pass must keep `live=0` on the existing samples. It may retain less. It must not release twice. It must not move a destruction past the release that drops the last strong reference, because that release is the finalisation point in the [memory model](memory-model.md#finalisation). Stack allocation and a headerless unique object are out of scope. |
| **Deterministic finalisers** | P0       | Immediately after the lifetime pass. Accept `~T()` and run it when the strong count reaches zero, on the releasing thread, derived class before base, before strong children are released. Then release those children through a worklist, not by recursion. `GC.SuppressFinalize` skips the user finaliser only. Reject resurrection and `GC.ReRegisterForFinalize`. `GC.Collect` and `GC.WaitForPendingFinalizers` compile as no-ops and report `ARC3200`. A finaliser exception is reported and does not stop teardown or propagate to the releaser. The generated field release in `__deinit` stays, and it runs after the user finaliser. `~T()` stays rejected through the Roslyn transition. |
| **Atomic refcounts**            | P1       | After the lifetime pass, and only as part of the threading protocol. Make `ObjHeader.strong` and `ObjHeader.weak` atomic (`_Atomic intptr_t` / `stdatomic.h`, lowered to `lock xadd`/`cmpxchg` on x64). Provide a compile-time or runtime flag to disable atomics for single-threaded programs. Atomic ops are used for all retain/release. Atomic counts alone do not protect a shared pointer between loading and retaining it. Require a verified protocol for shared-reference acquisition, weak promotion, memory ordering, synchronisation, and reclamation before threading; an atomic swap alone is insufficient.                                                                                                          |
| **Runtime leak detector**      | P0 **[done]** | Opt-in with `ARC_LEAKS=1`. The runtime records each allocation without retaining it, then prints survivors with type, address, strong and weak counts, and a source site (`file:line`, or `<runtime>` for strings built inside the runtime). Static roots are distinguished from unreachable objects, and unreachable strong cycles are printed from emitted field and root metadata, including references nested in structs and reference arrays. The report does not reclaim objects. Allocation stacks remain future work, once debug info exists (section 4). |
| **Backup cycle collector**      | —        | Closed: there will not be one. A statically visible cycle is warning `ARC3100`, and the program still compiles. A cycle that the developer or a library leaves in place leaks, including its finalisers. `WeakReference<T>` is the break. The leak report documents what survived. |
| **Optimised weak table**        | P1       | Replace per-object weak-field slots with a **global weak table** keyed by target object address. On dealloc, zero all weak entries pointing to the object. This avoids the current "weak count keeps the allocation alive" design and enables safer `WeakReference<T>` semantics at scale.                                                                                                                                    |

### Concrete first steps for ARC safety and efficiency

1. Add the debug allocation registry and survivor reports, then field/root metadata and cycle diagnostics. Verify against deliberate cycle samples and static-root cases. — **[done]** `ARC_LEAKS=1`, checked by `samples/leaks.cs`, `cycle_strong`, `cycle_struct`, and `cycle_weak`.
2. Audit existing managed-reference calls, including owner reassignment during a call. Add regression cases and define owner tracking and escape rules before expanding support. — **[done]** `samples/owners.cs` reads a field, an array element, a nested reference, and a struct `this` after the callee clears the named owner. `ref_escape` and `ref_owner` must fail.
3. Cycle policy is a closed decision: `ARC3100` warns, the cycle is legal, and an ignored warning leaks. `WeakReference<T>` breaks the edge. There is no collector to add later. Closures, events, and async still preserve C# strong captures when they arrive, after the lifetime pass.
4. After the lifetime pass, define the concurrent ownership protocol, then add `ARC_ATOMIC` and verify shared-reference acquisition, weak promotion, synchronisation, and reclamation before enabling threads.
5. Design the weak-table API (`arc_weak_table_add`, `arc_weak_table_remove`, `arc_weak_table_get`) and switch `WeakReference<T>` lowering to use it.
6. After the Roslyn transition, implement the lifetime pass in section 2 before LLVM emission. A borrowed value still needs a proved owner for its whole lifetime. Skipping a retain because a value is not stored is not that proof.

---

## 3. Runtime / BCL — standard library

This resumes after the lifetime pass. New library APIs should be written against the hybrid rules, with a lifetime test for each one.

| Component                   | Priority | Decision / approach                                                                                                                                                                                                                                                                    |
|-----------------------------|----------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Collections**             | P0       | Implement `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Stack<T>`, `Queue<T>` as ArcSharp-source libraries under ARC rules. Internally use arrays (`T[]`) and weak/strong references as appropriate. These live in a new `corelib/` directory and are compiled together with user code. |
| **String operations**       | P0       | Expand the runtime string API: `Substring`, `Split`, `IndexOf`, `Replace`, `ToString()` for primitives, culture-invariant comparisons. Eventually implement `StringBuilder`.                                                                                                           |
| **I/O**                     | P1       | File streams, networking, and a `Stream` abstraction. Initially shim to C stdio / sockets; later provide pure ArcSharp wrappers.                                                                                                                                                       |
| **`Span<T>` / `Memory<T>`** | P1       | `Span<T>` is a stack-only `byref`-like struct; heap-backed spans require proven owner lifetimes or hidden owner retention, beyond pointer + length. `Memory<T>` is an ordinary struct retaining its backing owner. Gate both on owner-lifetime tracking (section 2). This is critical for high-performance code and interop.                                                                             |
| **Reflection / `Type`**     | P1       | Embed enough metadata in `TypeInfo` to support `typeof(T)`, `GetType()`, basic `is`/`as`, and enumeration of fields/methods. Keep it minimal; full reflection is expensive under ARC.                                                                                                  |
| **Threading**               | P1       | Only after the concurrent ownership protocol in section 2 is specified and verified, add `Thread`, `Task<T>`, `Monitor`/`lock`, and basic synchronisation. Define ARC lifetime rules for cross-thread sharing (prefer move/unique ownership; shared references require atomic counts).                                                           |

### Strategy choice

**Re-implement a minimal corelib in ArcSharp C#** with a thin C runtime shim for platform calls (file I/O, threads, console). This keeps ARC semantics uniform across library and user code and avoids impedance mismatch with a foreign GC. Reuse well-tested algorithms from the BCL where possible, with lifetime tests for each API. Full .NET BCL compatibility is a separate objective requiring review of GC-dependent ownership and concurrency assumptions.

### Concrete first steps for BCL

1. Create `corelib/` directory with `System.String`, `System.Collections.Generic.List<T>`, `System.IDisposable`.
2. Extend `runtime/arc_runtime.c` with string helpers (`arc_str_substring`, `arc_str_index_of`, etc.).
3. Add `typeof(T)` intrinsic and runtime type metadata in `TypeInfo`.

---

## 4. Code generation — production backend

This waits until the Roslyn transition and the lifetime pass have landed. Until then, textual LLVM IR and clang remain the verified backend. The lifetime pass is written against that emitter.

| Change                                          | Priority | Decision / approach                                                                                                                                                                                                                                                                           |
|-------------------------------------------------|----------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **LLVM C API / object emission**                | P0       | Replace textual IR string-building with the **LLVMSharp / libLLVM C API** to construct modules and emit `.ll`, `.bc`, or `.obj` directly. Eliminate the `llc` subprocess dependency.                                                                                                          |
| **Debug info (DWARF/PDB)**                      | P0       | Emit `llvm.dbg.cu`, `DILocation`, `DILocalVariable`, and line tables so source-level debugging works in WinDbg/VS Code/lldb. Map ArcSharp source files, lines, and columns to LLVM debug metadata.                                                                                            |
| **Optimisation pipeline**                       | P0       | Run the standard LLVM pass pipeline (`PassBuilder` `-O2`/`-O3`) on generated modules. `arc_retain` and `arc_release` stay side effects. LLVM may delete a redundant load. It must not reorder or delete a release the lifetime pass kept, because that release is what runs a destructor.                                                                                   |
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
| **Known defects**       | P0            | Fix the defects recorded in [Known issues](language-reference.md#known-issues): ignored field initialisers, abstract methods that produce invalid IR, unchecked class casts, reference equality for `string ==`, inherited auto-property lookup, and reference temporaries in short-circuit expressions. Each fix lands with a sample that checks its output. |
| **Verification suite**  | P0 **[partly done]** | The harness now runs 30 samples, 4 leak-report checks, 25 must-fail diagnostics, 3 command-line checks, 4 cycle checks, and 68 analyser unit tests. Grow to a **thousands-of-tests conformance suite**. Adopt or write a test harness that runs ArcSharp output against expected output and ARC accounting. Target the C# spec incrementally; include negative tests (must-fail diagnostics), which the harness supports. |

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
| **Multi-file projects**     | P0 **[done]**        | The driver accepts several `.cs` inputs and the binder merges their type declarations before binding. The compiler keeps taking that explicit file list. A consumer project discovers the files through the SDK below.                                                                        |
| **Consumer SDK**            | P1                   | Ship a NuGet SDK, `ArcSharp.Sdk`, that a project uses in place of `Microsoft.NET.Sdk`. MSBuild still globs the `.cs` files and tracks which inputs are out of date. The SDK replaces `CoreCompile` and `Run`, so `dotnet build` and `dotnet run` invoke ArcSharp and produce the native executable. Replacing `csc` through `CscToolPath` does not fit: the `Csc` task writes a Roslyn response file, and the rest of `Microsoft.NET.Sdk` expects a managed assembly. `PackageReference` stays out until ArcSharp can consume a referenced assembly. |
| **Diagnostic quality**      | P0 **[partly done]** | Error codes (`ARC0001`…) and severity levels (error/warning/info) are implemented, with error-count gating and `-Werror`. Richer messages with source snippets and column tracking through every phase remain follow-up work. |
| **IDE integration (LSP)**   | P2                   | After the Roslyn transition, implement a **Language Server Protocol** server on Roslyn's workspace: document symbols, diagnostics, go-to-definition, and autocomplete.                                                                 |

### Concrete first steps for tooling

1. Accept multiple input files in the driver. — **[done]** The remaining consumption step is `ArcSharp.Sdk`: a NuGet package whose targets replace `CoreCompile` and `Run` and pass `@(Compile)` to the compiler.
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
accepts `try`, `async`, lambdas, and a strong parent/child cycle,
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

Cycles are a closed decision, not an open follow-up. `ARC3100` warns when the
type graph can form one. The program is still legal. `WeakReference<T>` breaks
the edge. If the developer or a library ignores the warning, the objects leak
and their finalisers do not run. That leak is the architecture, not a defect
waiting for a collector.

### What each rule is for

| Rule | Severity | Where it fires | Why |
|---|---|---|---|
| **Unsupported construct** | Error (`ARC3001` and following) | Compiler and analyser — implemented | Virtual/interface generic methods, `try`/`catch`/`finally`, `throw`, `async`/`await`, `yield`, lambdas, local functions, delegates, events, enums, indexers, records, pattern matching, LINQ, `lock`, `unsafe`, `stackalloc`, bitwise operators, `params`, optional and named arguments, and the struct restrictions. The compiler must stop dropping these on the floor. |
| **Possible strong cycle** | Warning (`ARC3100`) | Compiler and analyser — implemented | A set of classes whose strong fields reach each other can form a runtime cycle. ARC leaks it. `WeakReference<T>` and the legacy `weak` modifier are not edges in this graph. |
| **ARC and CLR disagree** | Info (`ARC3200`) | Planned | `GC.Collect` and `GC.WaitForPendingFinalizers` are no-ops. `GC.KeepAlive` and a `TryGetTarget` check that assumes a future collection are the other cases. The code compiles on both runtimes, and the timing does not match. Resurrection and `GC.ReRegisterForFinalize` are errors, not this info. |
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

1. **Correctness, language subset, diagnostics, generics, and the ARC safety foundation** — **[done]**. See [Already landed](#already-landed).
2. **Roslyn transition** — the current milestone. Port today's subset. Keep the same samples, diagnostic codes, and `live=` counts. Remove the hand-written lexer, parser, and name resolution only after `tools\verify.ps1` passes. No new language feature during this step.
3. **Lifetime pass** — section 2. One header. ARC where the proof fails. Destruction stays at the last strong release. Textual LLVM IR and clang remain the verified backend.
4. **Deterministic finalisers** — the [finalisation contract](memory-model.md#finalisation). `~T()` stays rejected until this step.
5. **Language backlog** — section 1, on the Roslyn frontend, under the hybrid rules and that contract. Exceptions, closures, and `async` come after finalisers.
6. **Backend upgrade** — LLVM C API, debug info, optimisations, multi-target. LLVM must treat `arc_retain` and `arc_release` as side effects, so a later pass cannot reorder or delete a release the lifetime pass kept. That release is what runs a finaliser.
7. **Minimal corelib, then concurrency** — ARC-aware APIs. Spans wait on a lifetime proof longer than one call. Threads wait on the concurrent ownership protocol.
8. **Tooling** — `ArcSharp.Sdk`, incremental build, and a language service. The language service should sit on Roslyn's workspace once the frontend has moved.

---

## Files touched by this roadmap

- The compiler project file — `Microsoft.CodeAnalysis.CSharp` during the Roslyn transition.
- A reference assembly for the supported surface — new, consumed by Roslyn and not by the real BCL.
- `src/Driver/Compilation.cs` — host Roslyn and lower `IOperation` into the bound tree.
- `src/Lexing/` and `src/Syntax/` — frozen, then removed when the Roslyn path is verified.
- `src/Binding/` — name resolution moves to Roslyn. Layout, vtables, definite assignment, and cycle analysis remain until replaced.
- `src/CodeGen/Emitter.cs` — unchanged ARC rules through the transition. The lifetime pass then skips proved retains and releases.
- `runtime/arc_runtime.c` — the header stays through the Roslyn transition. Finalisers add a suppress flag, a non-recursive release worklist, and the user-finaliser call before field release. Later: atomics, the weak table, and string helpers. No cycle collector.
- `corelib/` — new ArcSharp standard library, after the lifetime pass.
- `tests/` — conformance suite (planned).
- `analyzers/ArcSharp.Diagnostics/` — shared `ARC3xxx` ids and messages.
- `analyzers/ArcSharp.Analyzers/` — Roslyn analyser for ordinary `dotnet build` (section 7). It does not become the compiler.
- `analyzers/ArcSharp.Analyzers.Tests/` — xUnit tests for the analyser.
- `sdk/ArcSharp.Sdk/` — NuGet SDK a consumer uses in place of `Microsoft.NET.Sdk` (section 6).
