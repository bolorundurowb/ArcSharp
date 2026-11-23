# ArcSharp — Roadmap

This is the plan from the compiler that exists today to the end goal. It is a
sequence of milestones. Each milestone has a goal, small steps, and a
definition of done. The next milestone starts only when the current one is
done.

## The end goal

ArcSharp is a separate C# runtime with a documented compatibility profile. It
is not a garbage collector for the CLR.

```
C# source
    │  ordinary C# syntax
    ▼
Roslyn
    │  parsing and semantic analysis
    ▼
Ownership tree
    │  ArcSharp's own intermediate representation
    ▼
Lifetime pass
    │  a proved lifetime skips retain and release;
    │  anything else uses ARC on the same object
    ▼
LLVM
    │  native code
    ▼
Minimal runtime
    allocation, reference counts, weak references, finalisation
```

The program uses standard C# syntax. Its memory is managed by ARC, with no
tracing collector. Finalisers run deterministically, at the last strong
release. Weak references are explicit. The language and libraries are bounded
by the [compatibility target](#compatibility-target).

The compiler parses and binds with Roslyn. The lifetime pass now proves
selected borrows, moves and confined allocation lifetimes. [Where we are](#where-we-are) lists what has shipped.

---

## Decisions

These are settled. The milestones follow them.

| Decision | Choice |
|---|---|
| **Fallback** | When a lifetime cannot be proved, the compiler inserts ARC and the program still compiles. There is no new ownership syntax. |
| **Representation** | Every heap object keeps the header it has today (`strong`, `weak`, `TypeInfo*`). A proved lifetime skips `arc_retain` and `arc_release`. An escape, such as a store into a field or an array, or a call the analysis cannot see, uses the count on that same object. Identity does not change, so reference equality and weak references stay valid. There is no second, headerless layout. |
| **Frontend** | Roslyn parses and binds. Layout, vtables, the lifetime pass, the LLVM emitter, and the C runtime stay ArcSharp's. |
| **Intermediate representation** | The ownership tree between Roslyn and LLVM. Stock CIL is not a stage in the pipeline. CIL emitted for the CLR no longer records which store escapes or when the last strong reference dies, and both the lifetime pass and deterministic finalisation need those facts. Consuming precompiled assemblies is not a target. |
| **Cycles** | Closed. `ARC3100` warns when a cycle is visible in the type graph. The program still compiles. If the developer, or a library they use, ignores the warning, the cycle leaks and its finalisers do not run. There is no backup collector. `WeakReference<T>` is how a cycle is broken. |
| **Finalisation** | Specified in the [memory model](memory-model.md#finalisation). A finaliser runs synchronously when the last strong reference is released, on that thread, derived class before base, before the object's children are released. Lexical scope is not the trigger. `Dispose()` stays separate. Resurrection and `GC.ReRegisterForFinalize` are rejected. `GC.Collect` and `GC.WaitForPendingFinalizers` are documented no-ops. The compiler may delete a redundant retain, and it must not move a finaliser past the last release. |
| **Order** | Roslyn first, before any new language feature. The lifetime pass and finalisers come before the language features that create hidden lifetimes: exceptions, delegates, iterators, and `async`. |

---

## Compatibility target

This is the language and library surface ArcSharp is aiming at. It is not what
the compiler accepts today; the [language reference](language-reference.md)
describes that. Anything outside this table is not scheduled. In particular,
the rest of the .NET base class library, a tracing collector, CLR resurrection,
and threads are not targets.

| Feature | Target | Today | Milestone | Notes |
|---|---|---|---|---|
| Classes, structs, records | Full | Classes, and structs with restrictions. Records are rejected. | [M6](#m6--ordinary-c) | Ordinary C# semantics, including the member forms, statements, and operators that come with them. |
| Interfaces, inheritance | Full | Single inheritance, interfaces, virtual tables, and interface tables. A base interface list is parsed and then ignored. | [M6](#m6--ordinary-c) | Native dispatch tables. |
| Generics | Full | Monomorphisation, basic inference, and constraints for a subset. | [M6](#m6--ordinary-c) | Each closed type is specialised ahead of time. A single shared representation for all type arguments is not the plan. |
| Delegates, events | Full | Rejected (`ARC3001`). | [M7](#m7--delegates-lambdas-and-events) | The capture is an ARC object. Strong captures stay strong, and `ARC3100` still applies. |
| LINQ | Full | Rejected. | [M8](#m8--core-library-iterators-and-linq) | A library written against delegates, generics, iterators, and the core collections. Query syntax lowers to those methods. |
| `async` / `await` | Full | Rejected. | [M9](#m9--async-and-await) | A state machine whose suspended locals follow the ARC rules. Continuations run on one thread. |
| Exceptions | Full | `try`, `catch`, `finally`, `throw`, and a small `System` exception library. Exception filters and throw expressions are rejected. | [Shipped](#where-we-are) | Every unwind path releases live references. Finalisation and `using` both run on that path. |
| `IDisposable` / `using` | Full | `using` runs `Dispose()` on normal completion, on `return`, and while an exception unwinds, then releases the resource. | [Shipped](#where-we-are) | Disposal stays an explicit call. A finaliser does not call `Dispose()`. |
| `Span<T>` / `ref` struct | Full | A `ref` that would outlive the call is rejected. | [M10](#m10--spans-and-ref-structs) | Roslyn's escape rules stay. Stack storage does not, by itself, keep a heap object alive. |
| `WeakReference<T>` | Full | Supported, including as a field, a local, and a generic argument. | [Shipped](#where-we-are) | The native weak reference. It does not keep the target alive. |
| Reflection | Limited | Not implemented. | [M11](#m11--limited-surfaces) | Metadata the compiler has already emitted: type names, bases, fields, and methods. Not a runtime that loads or invokes arbitrary framework code. |
| `dynamic` | Limited | Rejected. | [M11](#m11--limited-surfaces) | Accepted only when the call binds to a member this compiler can already lower. The CLR runtime binder is not a target. |
| `unsafe` / pointers | Limited | Rejected. | [M11](#m11--limited-surfaces) | A pointer into an ARC object must not outlive the lifetime the compiler has proved for that object. Pointer arithmetic does not bypass a retain, a release, or a finaliser. |

---

## Rules for every milestone

- Work on one milestone at a time. A milestone is done when every item in its
  definition of done is true, `tools\verify.ps1` has been run, and the actual
  results are recorded in [Verification](verification.md).
- Each step leaves `tools\verify.ps1` passing. A step that cannot keep it green
  is too large and is split.
- When a construct becomes supported, its `ARC3001` entry is removed from the
  compiler and from the analyser in the same change, with the analyser tests
  updated. A stale analyser that rejects a supported construct is a bug.
- Every new behaviour lands with a sample that checks its observable output and
  its `[arc]` accounting. A must-fail sample checks every new rejection.
- The documents named in [`AGENTS.md`](../AGENTS.md) are updated in the same
  change as the behaviour they describe.
- Diagnostic codes are never renumbered or reused.
- The ARC invariants in [`AGENTS.md`](../AGENTS.md) hold at every step. Over-
  retaining is acceptable. An unbalanced retain or release is not.
- At the end of every milestone, before it is marked done, re-run
  `tools\compare-gc.ps1 -Runs 5` and add the new medians to the table in the
  [GC comparison](features/gc-comparison.md). A slower wall time does not fail
  a milestone. The milestone's own definition of done decides that.

---

## Where we are

Each of these was run on Windows x64 with `tools\verify.ps1`. GitHub Actions
runs the same harness for pull requests targeting `master`. The latest local
results are in [Verification](verification.md).

- The pipeline from C# to LLVM IR to a native executable, with ARC accounting
  on every run.
- Classes, single inheritance, interfaces, virtual tables, and interface tables.
- Generics for a subset: monomorphisation, basic inference, and constraints.
- Structs with reference fields, `ref` / `out` / `in` parameters, and instance
  and static auto-properties, with the restrictions in the language reference.
- The integer and floating-point numeric types, strings, single-dimensional
  arrays, `is`, `as`, and checked array covariance.
- `using` with an explicit `Dispose()` on the normal completion path.
- `WeakReference<T>`, the `ARC3100` cycle warning, and the opt-in leak report
  (`ARC_LEAKS=1`).
- An opt-in retain and release count (`ARC_STATS=1`). The baseline for every
  sample is in [Verification](verification.md).
- Interior-reference owner pins for the duration of a call.
- The M2 verification baseline. The harness asserts exact ARC counts, checks
  destruction timing with weak references, and covers the seven M2 defects.
  See [Verification](verification.md).
- The M3 lifetime pass. The original 34 samples have the same output and
  accounting, with 49 fewer retains and 49 fewer releases in total. See
  [Verification](verification.md) and the [M3 benchmark medians](features/gc-comparison.md#m3-medians-22-november-2026).
- The M4 deterministic finalisers. `~T()` runs at the last strong release,
  derived before base, with `GC.SuppressFinalize`, a worklist release that
  handles very deep graphs, and rejected resurrection. See
  [Deterministic finalisers](features/finalisers.md).
- The M5 exceptions. `try`, `catch`, `finally`, and `throw`, a small `System`
  exception library, `using` disposal while unwinding, runtime checks that
  raise catchable exceptions, an unhandled-exception exit with code 70, and a
  throwing finaliser that is reported and ignored. See
  [Exceptions](features/exceptions.md).
- Diagnostics with stable codes and severities, `-Werror`, definite-assignment
  warnings (`ARC2100`), and `ARC3001` for unsupported constructs.
- A Roslyn analyser that reports `ARC3001` and `ARC3100` in an ordinary
  `dotnet build`, with a shared catalogue and xUnit tests.
- Multi-file compilation.
- Roslyn is the frontend. The hand-written lexer, parser, and name resolution
  are gone. Layout, definite assignment, cycle analysis, and emission are
  unchanged.
- A comparison of peak memory and collector pauses against the ordinary .NET
  runtime, on source both compilers accept. The method and the M2 medians are in
  [Comparing ArcSharp with the .NET garbage collector](features/gc-comparison.md).
  `tools\compare-gc.ps1` is not part of the verification harness.

---

## M1 — Roslyn frontend

**Landed.**

**Goal.** The same compiler, with Roslyn at the front. It accepts today's
subset, rejects the same programs, and produces the same output and the same
ARC accounting. Memory behaviour does not change.

**Steps.**

1. Reference `Microsoft.CodeAnalysis.CSharp` from the compiler, on the same 4.8
   line the analyser already uses. This is the one package the compiler is
   allowed to take. The shared catalogue in `analyzers/ArcSharp.Diagnostics`
   stays free of Roslyn.    **Landed.** The compiler project references 4.8.0.
2. Add a reference assembly that describes exactly the surface ArcSharp
   implements: `object`, `string`, the numeric types, arrays,
   `Console.Write` and `WriteLine`, `WeakReference<T>`, and a parameterless
   `Dispose()`. Roslyn binds against that assembly, not the real base class
   library, so it cannot accept an API this compiler cannot lower. **Landed.**
   The assembly is `ref/ArcSharp.References`. The compiler builds it and copies
   it beside `arcsharp.dll`, and the compiler loads that copy. `String.Concat`, `Object.ToString`, `System.Enum`, and
   `System.Attribute` are visible because the C# compiler requires them; a
   direct use is still not part of the language surface. The architecture
   describes those gaps. `tools/verify.ps1` runs
   `ref/ArcSharp.References.Check` against the assembly.
3. Add the Roslyn path beside the existing one, behind a temporary driver
   option, so the two can be compared on every sample. **Landed.** The
   temporary option was removed in step 7.
4. Lower declarations from Roslyn's symbols into the existing symbols: types,
   fields, methods, properties, and generic instantiations. Layout, virtual
   tables, and interface tables do not change. **Landed.**
5. Lower method bodies from `IOperation` into the existing bound tree. Every
   operation kind the lowering does not handle reports `ARC3001`. An unhandled
   node must not become an empty block or a silent null, which is the main
   pitfall of the current binder. **Landed.** The lowering produces the same
   output and ARC accounting the hand-written path did.
6. Reach diagnostic parity. ArcSharp rules keep their codes (`ARC2xxx`,
   `ARC3001`, `ARC3100`). Syntax and name errors now come from Roslyn with its
   own `CS` codes. The must-fail samples that expected a lexer or parser code
   are updated to expect the Roslyn error. The retired `ARC0xxx` and `ARC1xxx`
   codes stay reserved and are not reused. **Landed.**
7. Make the Roslyn path the default. Delete the hand-written lexer, parser, and
   name resolution, and remove the temporary option. Definite assignment, cycle
   analysis, layout, and emission stay until each has a checked replacement.
   **Landed.** `tools\verify.ps1` runs the Roslyn path alone.

**Done when.**

- `tools\verify.ps1` passes with only the Roslyn path in the compiler.
- Every sample prints the same output with the same `alloc`, `dead`, `freed`,
  and `live` counts as before the milestone.
- Every must-fail sample fails with its documented code.
- The analyser tests still pass.
- The architecture, contributor guide, diagnostics, and language reference
  describe the Roslyn pipeline.

**Not in this milestone.** New language features, changes to retain and
release, and `~T()`. A parser defect that disappears because Roslyn binds
correctly, such as namespace identity, is fixed here and removed from the
known issues. Ignored field initialisers are an emitter defect and wait for M2.

---

## M2 — Verification baseline

**Landed.**

**Goal.** Tests strong enough that the lifetime pass cannot introduce a double
release, a use after free, or a moved destruction without the harness noticing.

**Steps.**

1. Add a runtime checking mode that traps a retain or a release of a dead
   object, a strong count that goes below zero, and a weak load of freed
   memory. The harness runs every sample with it switched on.
   **Landed.** See [ARC checking mode](features/arc-checking-mode.md).
2. Add an opt-in statistics line with the number of retains and releases each
   run performs. It is separate from the `[arc]` accounting line, whose format
   the harness depends on. Record the baseline for every sample.
   **Landed.** See [Retain and release statistics](features/arc-statistics.md).
3. Check exact `alloc`, `dead`, and `freed` counts for every sample, not only
   `live`. **Landed.** The harness compares the complete accounting line.
4. Add samples that observe destruction points directly: a `WeakReference<T>`
   that must turn `false` at a specific statement. These become the evidence
   that later milestones keep destruction in place. **Landed.**
5. Fix the known defects, each covered by a sample: ignored field
   initialisers, abstract methods that produce invalid IR, string `==`
   comparing references, get-only auto-properties that cannot be assigned,
   inherited auto-property lookup, rejected small integer casts, and reference
   temporaries on short-circuit paths. Defects that need exceptions, such as an
   unchecked class cast, waited for M5, which fixed them. **Landed.**
6. Run `tools\verify.ps1` in CI on Windows x64. **Landed.** The existing
   workflow runs the harness for pull requests targeting `master`.

**Done when.** The checking mode is on in the harness and every sample passes
under it. The retain and release baseline is in Verification. The fixed
defects are gone from the known issues. CI runs the harness on every change.

---

## M3 — Lifetime pass

**Landed.** See [Lifetime classification, borrows and moves](features/lifetime-reads-and-moves.md)
and [Confined allocation lifetimes](features/confined-allocation-lifetimes.md).

**Goal.** ARC only where a lifetime cannot be proved, on the existing header.

**Steps.**

1. Classify every reference value in the ownership tree as owned, borrowed, or
   shared. The emitter ignores the classification at first. A debug option
   prints it. Output does not change. **Implemented.** Use `--dump-ownership`.
2. Skip the retain and release for a borrowed read of a local or parameter
   whose slot cannot change during the use: repeated reads, a call receiver,
   and an argument the callee does not store. **Implemented** for stable slot
   reads, side-effect-free arguments and directly inspected callees.
3. Turn the last use of a local into a move when it is stored, passed, or
   returned, instead of a retain and a later release. **Implemented** for
   direct returns, final local declarations with compatible cleanup order,
   and final calls with a single non-escaping local argument.
4. Let an object that is created and never escapes skip count traffic until its
   proved end. That end is exactly where its last strong release used to be,
   so destruction does not move. **Implemented** for fresh class and array
   locals whose construction and uses prove confinement.
5. Treat everything else as shared. A store to a field or an array, a call the
   pass cannot see, or a capture is the promotion point. **Implemented** as
   a conservative fallback. A use outside the confinement proof keeps
   ordinary ARC for the allocation wherever the existing borrow and move
   rules cannot prove a lifetime.

**Done when.** Every sample prints the same output with the same accounting.
The checking mode reports nothing. Every destruction-point sample from M2 sees
the object die at the same statement. Retain and release counts are below the
M2 baseline, and the actual numbers are recorded in Verification.

**Not in this milestone.** Stack allocation, a headerless object, and LLVM
optimisation passes. Which cases the first version must prove is decided at
the start of this milestone. Because the fallback is ARC, a weak first proof
is safe.

---

## M4 — Deterministic finalisers

**Landed.** See [Deterministic finalisers](features/finalisers.md).

**Goal.** The [finalisation contract](memory-model.md#finalisation), in the
compiler and the runtime.

**Steps.**

1. Accept `~T()`. Remove it from `ARC3001` in the compiler and the analyser.
   **Implemented.**
2. Generate the finaliser chain, derived class before base, at the start of the
   generated teardown, before strong fields are released. **Implemented.** The
   chain is one generated function per class, reached through a new
   `finalizer` field in the type descriptor, and the runtime calls it before
   the teardown.
3. Replace the recursive release in the runtime with a worklist, so a very deep
   object graph cannot overflow the stack. **Implemented.** A list of 100,000
   nodes is now destroyed by a loop.
4. Support `GC.SuppressFinalize` with a flag on the object. The flag skips the
   user finaliser only. Where the flag sits in the header is decided in this
   step, and the emitter, the runtime, and the layout documentation change
   together. **Implemented.** The flag is packed into the weak word of the
   header, above the weak count, so the header is still three slots and no
   field offset moved.
5. Reject resurrection. A finaliser that stores `this`, or a reference derived
   from it, into a longer-lived location is a compile-time error. A strong
   count above zero when the finaliser returns aborts the process.
   **Implemented.** The compile-time check (`ARC2016`) covers the stores the
   compiler can see. The runtime abort covers the rest.
6. Accept `GC.Collect` and `GC.WaitForPendingFinalizers` as no-ops that report
   info `ARC3200`. Reject `GC.ReRegisterForFinalize`. **Implemented.**

**Done when.** Samples show the derived-then-base order, children still
reachable inside the finaliser, a graph deep enough to overflow the old
recursive release, `GC.SuppressFinalize`, and a cycle whose finaliser never
runs. A must-fail sample shows resurrection is rejected. The "Compiler today"
column of the memory model matches the compiler.

**Not in this milestone.** A finaliser could not throw, because `throw` was
not accepted. The report-and-continue policy for finaliser exceptions landed
in M5.

---

## M5 — Exceptions

**Landed.** See [Exceptions](features/exceptions.md).

**Goal.** `try`, `catch`, `finally`, and `throw`, with deterministic ARC
cleanup on every unwind path.

**Steps.**

1. Add a minimal `Exception` hierarchy. **Implemented**, with one change from
   the plan: the types are C# source in `corelib/Exceptions.cs`, embedded in
   the compiler and compiled with each program, not types in the reference
   assembly.
2. Make every exit from a cleanup scope release live temporaries and locals, so
   a release that reaches zero still finalises on the unwind path.
   **Implemented**, with a different mechanism from the one first planned: a
   pending-exception flag that the compiled code tests after each call and
   runtime check, not Windows structured exception handling through LLVM. The
   flag reuses `func.exit` and has no platform dependency. See
   [Exceptions](features/exceptions.md#a-pending-exception-flag-not-structured-exception-handling).
3. Run `using` cleanup and `finally` on both the normal and the unwind path.
   **Implemented.**
4. Teach the lifetime pass that an unwind edge is a use. A value whose lifetime
   ends on that edge is destroyed there. **Implemented** conservatively: a
   `return local` inside a `try` is never a move, and locals still die at
   `func.exit`. Destroying a value on the unwind edge as early as the normal
   path would is a possible refinement, not scheduled.
5. Turn the runtime checks into exceptions: an index out of range, an invalid
   class cast, a null dereference, and division by zero. An unhandled exception
   prints a message, prints the accounting line, and exits with a documented
   code. **Implemented.** The exit code is 70.
6. A finaliser that throws is reported, and teardown continues. The exception
   does not reach the code that released the last reference. **Implemented.**

**Later optimisation.** The compiled code tests the pending-exception flag after
every call. Skipping the test after a call that provably cannot throw would
remove most of those tests. It needs a pass over the call graph, so it waits
until a measurement shows the cost matters.

**Done when.** Samples show cleanup on every unwind path with `live=0`, each
runtime exception caught, `using` disposal during unwinding, and a throwing
finaliser. The differences from .NET in the language reference are updated.

---

## M6 — Ordinary C#

**Goal.** The full rows for classes, structs, records, interfaces, and
generics: the member forms, statements, and operators that ordinary C# code
uses. Each step below ships on its own.

**Steps.**

1. Statements: [constant-label switch statements](features/switch-statement.md)
   and [storage updates](features/increment.md) are implemented.
   [Post-test loops, loop branches, and array iteration](features/do-while.md)
   are implemented.
2. Operators: operator overloading. [Bitwise operators and shifts](features/bitwise.md)
   and [checked arithmetic](features/checked.md) are implemented.
3. Member forms: `const`, `readonly`, static constructors, nested types,
   expression-bodied members, accessor bodies, `params`, and optional and named
   arguments.
4. Enums and indexers.
5. The remaining struct forms: arrays of structs, struct returns, static struct
   fields, by-value struct parameters, struct-typed properties,
   `virtual`/`override` and interfaces on structs, and printing a struct.
6. Base interface lists, records with the members C# synthesises, and pattern
   matching.
7. The rest of generics: virtual and interface generic methods, variance, full
   inference, and the remaining constraint forms.
8. Nullable value types and `decimal`.

**Done when.** Each step has samples and must-fail samples, its `ARC3001`
entries are gone from the compiler and the analyser, and the language reference
lists it as supported.

---

## M7 — Delegates, lambdas, and events

**Goal.** Delegates, lambdas, local functions, and events, with captures as
ordinary ARC objects.

**Steps.**

1. Lower a lambda or local function to a closure object that owns its captured
   variables. Strong captures stay strong, including `this`.
2. Add single and multicast delegates that hold a target reference and a
   function pointer.
3. Add events as delegate fields with `add` and `remove` accessors.
4. Teach the lifetime pass that a capture is an escape unless it is proved not
   to outlive the frame.
5. Extend `ARC3100` to the cycles a capture makes visible, such as a handler
   that captures `this` and is stored in a field of the same object. The
   program stays legal.

**Done when.** Samples cover capture, multicast invocation, event subscription
and removal, and a capture cycle that warns and leaks as documented.

---

## M8 — Core library, iterators, and LINQ

**Goal.** The libraries named in the compatibility target, written in ArcSharp
C# against the ARC rules. The rest of the .NET base class library is not built.

**Steps.**

1. Create `corelib/`, compiled with every program, and extend the reference
   surface from M1 to match it.
2. Strings: the common methods, `ToString()` for the primitive types,
   formatting, and `StringBuilder`.
3. Collections: `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`,
   `Queue<T>`, and `Stack<T>`.
4. Iterators: `yield`, `IEnumerable<T>`, and `foreach` over any enumerable.
   The state machine is an ARC object. Its lowering is the base for `async` in
   M9.
5. LINQ to objects, in method syntax and query syntax.

**Done when.** Every API has a lifetime test that checks its accounting. LINQ
samples run with `live=0`. Reusing an algorithm from the base class library is
allowed only when it does not assume a garbage collector.

**Decided at the start of this milestone.** String representation. Today a
string is UTF-8 and `Length` counts bytes. Full compatibility needs UTF-16 code
units for `Length`, indexing, and `char`.

---

## M9 — `async` and `await`

**Goal.** `async` and `await` on one thread, with ARC-managed state machines.

**Steps.**

1. Lower `async` methods to a state machine object, reusing the iterator
   lowering from M8. Suspended locals are fields of that object and follow the
   ARC rules.
2. Add a minimal `Task`, `Task<T>`, and a single-threaded scheduler.
3. Carry exceptions across `await`, using M5.
4. Add `IAsyncDisposable` and `await using`, kept separate from finalisation.

**Done when.** Samples show suspension and resumption, exceptions across
`await`, and `await using`, each with `live=0`.

---

## M10 — Spans and ref structs

**Goal.** `Span<T>`, `ReadOnlySpan<T>`, `ref` structs, ref locals, ref returns,
and `stackalloc`.

**Steps.**

1. Accept Roslyn's ref-safety rules as the escape rules for spans and ref
   structs.
2. Extend the owner pin from one call to the proved scope of the span or the
   ref local, so the heap object behind it stays alive for that whole scope.
3. Add `Memory<T>` as an ordinary struct that holds a strong reference to its
   owner.

**Done when.** Samples show a span over an array, a ref local, and a ref
return. Must-fail samples show each escape Roslyn or the lifetime pass rejects.

---

## M11 — Limited surfaces

**Goal.** The three limited rows of the compatibility target.

**Steps.**

1. Reflection: `typeof`, `GetType()`, and a `Type` with its name, base, fields,
   and methods, from metadata the compiler already emits.
2. `dynamic`, accepted only when the call binds to a member this compiler can
   already lower. Every other use is rejected.
3. `unsafe` code, pointers, and `fixed`. A pointer into an ARC object is pinned
   by a retain for the pointer's proved scope. Pointer arithmetic cannot skip a
   retain, a release, or a finaliser.

**Done when.** Each surface has samples, and every unsupported use has a
must-fail sample.

---

## M12 — Production backend

**Goal.** A backend fit for real programs, without changing the memory
contract.

**Steps.**

1. Build modules through the LLVM C API (LLVMSharp) instead of text, and emit
   object files directly.
2. Emit debug information, including PDB files on Windows, and allocation
   stacks in the leak report.
3. Run the LLVM optimisation pipeline. `arc_retain` and `arc_release` stay
   opaque side effects, so LLVM cannot move or delete a release the lifetime
   pass kept. That release is what runs a finaliser.
4. Make the accounting counters and `arc_report()` part of debug and
   verification builds only.
5. Verify the Linux x64 target that the compiler can already emit.

**Done when.** The harness passes with optimisation switched on. A debugger
steps through a sample on Windows. The Linux x64 run is recorded in
Verification.

---

## M13 — Tooling

**Goal.** ArcSharp as an ordinary project experience.

**Steps.**

1. Ship `ArcSharp.Sdk`, used in place of `Microsoft.NET.Sdk`, so `dotnet build`
   and `dotnet run` invoke ArcSharp and produce the native executable.
2. Publish the analyser package, with its rules generated from the shared
   catalogue.
3. Add incremental compilation.
4. Add a language server on Roslyn's workspace.

**Done when.** A new project builds and runs through `dotnet run`, and the
analyser package matches the compiler release it describes.

---

## Not scheduled

These are outside the compatibility target, or closed. They are recorded so
they are not reopened by accident.

| Item | Status |
|---|---|
| Threads, atomic reference counts, and `ARC3300` | Outside the target. They need a verified concurrent ownership protocol, not only atomic counts. `async` continuations stay on one thread until then. |
| A tracing or backup cycle collector | Closed. See [Decisions](#decisions). |
| CIL as an input or an intermediate stage | Closed. See [Decisions](#decisions). |
| A headerless object layout | Closed. See [Decisions](#decisions). |
| Stack allocation of a non-escaping object | Possible after M12, as an optimisation on the same object identity. Not planned. |
| A global weak table and a non-zeroing `unowned` reference | Not decided. `WeakReference<T>` meets the target as it is. |
| The rest of the .NET base class library | Outside the target. |
| 32-bit, ARM, and big-endian targets | Outside the target. They need the 8-byte slot assumption removed. |

---

## Appendix: diagnostics policy

The compiler and the Roslyn analyser report the same `ARC3xxx` rules from one
shared catalogue, `analyzers/ArcSharp.Diagnostics`. The analyser lets a
developer see the problems inside an ordinary `dotnet build`. The compiler
still reports them itself, because the analyser is optional and can be older
than the compiler.

| Rule | Severity | Status | Purpose |
|---|---|---|---|
| Unsupported construct (`ARC3001`) | Error | Implemented | A construct this compiler cannot lower. Each milestone removes the entries it makes supported. |
| Possible strong cycle (`ARC3100`) | Warning | Implemented | Classes whose strong fields can reach each other. The program stays legal, and an ignored warning leaks. `WeakReference<T>` is not an edge. |
| ARC and CLR disagree (`ARC3200`) | Info | Implemented | `GC.Collect()` and `GC.WaitForPendingFinalizers()` are no-ops. Only these two calls report it today. `GC.KeepAlive` is not in the library, and a `TryGetTarget` check that waits for a collection is not detected. |
| Threads (`ARC3300`) | Warning | Reserved | Cross-thread sharing, which the non-atomic counts make unsound. Not scheduled. |

`ARC3xxx` is reserved for this catalogue. Ordinary compiler errors use the code
range of the stage that reports them.

### What a cycle warning can and cannot know

The check looks at declared field types, including arrays of a class, static
fields, and class references reached through a struct field. It builds a graph
of strong edges and warns once per strongly connected component. That is a
statement about types, not about one object graph.

It warns on a real back-edge, such as `Parent.child` and `Child.owner` both
strong, which is the leak in `cycle_strong`. It also warns on a binary tree and
on a singly linked list, which do not leak unless an instance is later wired
into a loop. Those are false positives. The suppression is an ordinary
`#pragma warning disable ARC3100` or `[SuppressMessage]` on the type. There is
no separate tree heuristic, because a self-typed field is how both a tree and a
doubly linked list are written, and a doubly linked list does leak.

It misses a cycle that only exists at run time: a field typed as `object` or as
a base class, or two objects assigned into each other through locals. No static
check proves that a cycle will happen, and none proves that it will not. The
accounting line and the leak report are the evidence.

The suggested fix belongs in the message, which is to make the back-edge a
`WeakReference<T>`. It is not an automatic rewrite, because changing a field's
type changes every reader.
