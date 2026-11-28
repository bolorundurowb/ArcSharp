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
| **Shared libraries** | A library is C# source. ArcSharp does not load a compiled assembly, so a library is compiled together with the program that uses it, in one compilation. Local source references and source-only NuGet packages both feed one list of `.cs` files to the compiler. There is no ArcSharp-specific manifest, resolver, or Git fetcher. See [M14](#m14--shared-source-libraries). |
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
| Classes, structs, records | Full | Classes, structs, and record classes. Record structs and generic records are rejected. | [Shipped](#where-we-are) | Ordinary C# semantics, including the member forms, statements, and operators that come with them. |
| Interfaces, inheritance | Full | Single inheritance, transitive base interfaces, virtual tables, and interface tables. | [Shipped](#where-we-are) | Native dispatch tables. |
| Generics | Full | Monomorphisation, inference, virtual and interface generic methods, static abstract members, variance, and constraints. | [Shipped](#where-we-are) | Each closed type is specialised ahead of time. A single shared representation for all type arguments is not the plan. |
| Delegates, events | Full | Single and multicast delegates over method groups, local functions and nested lambdas, with captures, and `Action`, `Func` and `Predicate<T>`. Non-virtual field-like events on classes support subscription and removal. | [M7](#m7--delegates-lambdas-and-events) | The capture is an ARC object. Strong captures stay strong, and `ARC3100` still applies. |
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
- UTF-16 strings, common string methods, primitive text conversion,
  interpolation, composite formatting, and `StringBuilder`. See
  [Strings and formatting](features/strings.md).
- `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `Queue<T>`, and
  `Stack<T>`, with element equality and hashing lowered per element type. See
  [Collections](features/collections.md).
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
- The M6 ordinary C# surface. Statements, operators, member forms, enums, struct storage and calls, patterns, generics with virtual and static abstract members, record classes, object initialisers, nullable values with boxing and updates, and `decimal`. See [Records](features/records.md), [Object initialisers](features/object-initialisers.md), [Generic dispatch](features/generic-dispatch.md), [Nullable values](features/nullable-values.md) and [Decimal](features/decimal.md).
- M7 delegates, nested lambdas, local functions and field-like events. Escaping
  captures keep their own ARC ownership. `ARC3100` covers cycles through
  captures. See [Nested closures and local functions](features/nested-closures.md),
  [Delegates](features/delegates.md) and [Events](features/events.md).
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

**Landed.** The benchmark medians are in the [GC comparison](features/gc-comparison.md).

**Goal.** The full rows for classes, structs, records, interfaces, and
generics: the member forms, statements, and operators that ordinary C# code
uses. Each step below ships on its own.

**Steps.**

1. Statements: [constant-label switch statements](features/switch-statement.md)
   and [storage updates](features/increment.md) are implemented.
   [Post-test loops, loop branches, and array iteration](features/do-while.md)
   are implemented.
2. Operators: [bitwise operators and shifts](features/bitwise.md),
   [checked arithmetic](features/checked.md), and [direct operator overloads](features/operators-members.md)
   are implemented, including [overloaded short-circuit operators](features/conditional-values.md)
   and [overloaded storage updates](features/operator-updates.md).
3. Member forms: [static constructors](features/static-constructors.md),
   [constants, readonly fields, nested types and expression-bodied methods and
   constructors](features/operators-members.md),
   [named, optional and parameter-array arguments](features/arguments.md), and
   [accessor bodies and auto-property initialisers](features/accessors.md),
   [updates through accessors](features/accessor-updates.md), and
   [extension methods and partial classes](features/extensions-partial.md) are
   implemented within the documented subset.
4. [Enums](features/enums.md) and [indexer get and set](features/accessors.md)
   are implemented within the documented subset.
5. [Struct parameters and return values](features/struct-calls.md),
   [struct arrays, static fields and properties](features/struct-storage.md), and [struct interface dispatch, overrides and printing](features/struct-interfaces.md)
   are implemented within the documented subset.
6. [Record classes](features/records.md) with the members C# synthesises, and [object initialisers](features/object-initialisers.md), are implemented.
   [Switch expressions](features/conditional-values.md) are implemented.
   [Base interface lists and explicit implementations](features/base-interfaces.md)
   and [scalar, type, recursive and array list patterns](features/patterns.md)
   are implemented.
7. [Virtual and interface generic methods, static abstract members, full
   inference](features/generic-dispatch.md), and
   [interface variance and additional constraints](features/generic-variance.md)
   are implemented.
8. [Nullable storage, conversions, lifted operations, updates, user-defined
   operators and struct boxing](features/nullable-values.md) and [`decimal`](features/decimal.md)
   are implemented.

**Done when.** Each step has samples and must-fail samples, its `ARC3001`
entries are gone from the compiler and the analyser, and the language reference
lists it as supported.

---

## M7 — Delegates, lambdas, and events

**Goal.** Delegates, lambdas, local functions, and events, with captures as
ordinary ARC objects.

**Status.** Complete. See [Verification](verification.md) and the
[M7 benchmark medians](features/gc-comparison.md#m7-medians-25-november-2026).

**Steps.**

1. [Nested lambdas and local functions](features/nested-closures.md) share ARC
   capture cells and own captured receivers.
2. Lambdas and method groups are delegates with a handler list, so they share
   single-cast and multicast invocation, combine, remove and equality. Caching
   non-capturing lambda instances is a later optimisation.
3. Non-virtual field-like events on classes use delegate fields with synthesised `add` and `remove` accessors.
4. The lifetime pass conservatively treats a capture as an escape. Borrowing
   inside a confined capture environment can be considered when a lifetime
   proof preserves destruction timing.
5. Extend `ARC3100` to cycles introduced by captures. The compiler and
   analyser cover a lambda stored in a delegate field of its captured owner.
   The same check covers field-like event subscriptions.
   The program stays legal.

**Done when.** Samples cover capture, multicast invocation, event subscription
and removal, and a capture cycle that warns and leaks as documented.

---

## M8 — Core library, iterators, and LINQ

**Goal.** The libraries named in the compatibility target, written in ArcSharp
C# against the ARC rules. The rest of the .NET base class library is not built.

**Steps.**

1. **Done.** Embed the sources under `corelib/` in the compiler and compile them
   with every program. Build the same sources into the reference assembly
   so metadata consumers see the exception and delegate declarations.
2. **Done.** Common string methods, primitive `ToString()`, formatting,
   and `StringBuilder`. See [Strings and formatting](features/strings.md).
3. **Done.** Collections: `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`,
   `Queue<T>`, and `Stack<T>`. Element equality and hashing are lowered for each
   closed element type. See [Collections](features/collections.md).
4. **Done.** [Enumerable foreach](features/enumerable-foreach.md) supports
   `IEnumerable<T>` and reference enumerators. [Iterators](features/iterators.md)
   cover methods and property getters with `yield`. All five core collections
   support [generic enumeration](features/collection-enumeration.md).
   The state machine is an ARC object. Its lowering is the base for `async` in
   M9. Saving only the locals that are live across a `yield`, rather than every
   local, is a possible optimisation.
5. LINQ to objects, in method syntax and query syntax.

**Done when.** Every API has a lifetime test that checks its accounting. LINQ
samples run with `live=0`. Reusing an algorithm from the base class library is
allowed only when it does not assume a garbage collector.

Strings store UTF-16 code units for `Length`, indexing and `char`.
The current `StringBuilder` copies its text on each append. An amortised
character buffer is a possible optimisation when append-heavy workloads justify it.

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

## M14 — Shared source libraries

**Goal.** A developer shares code between ArcSharp programs the way they share
any .NET code: a project reference inside one repository, and a
`PackageReference` across repositories. Both routes hand the compiler one list
of `.cs` files. Nothing new is added to the native runtime.

**Starts after M13.** The SDK and the analyser package are the delivery
vehicle for steps 6 to 9. Steps 1 to 5 are compiler work and do not need the
SDK. Until M14 lands, share a library by passing its files on the command line,
as the `multifile` sample does, or by vendoring a pinned copy.

**Why source, and why NuGet.** ArcSharp cannot load a compiled assembly, so a
library has to arrive as source. NuGet already restores versioned files,
resolves transitive dependencies, picks one version for the whole graph, and
records hashes in `packages.lock.json`. That one-version rule matches the
compiler, which builds a single compilation and cannot hold two versions of
the same type. A Git-based fetcher would need its own manifest, resolver, and
source-set convention, and would be a second workflow for the same job. It is
not planned.

**What the compiler does today.** These facts were checked against the
compiler on 27 November 2026 with small probe programs. They set the work in
this milestone.

| Observation | Consequence |
|---|---|
| Every input joins one compilation, and a namespace is part of a type's identity. | Libraries need their own namespaces. This is the whole isolation model. |
| An `internal` type in one input is usable from another input. | Package boundaries do not hide `internal` members. Authors must not rely on them being private. This is documented, not fixed. |
| Two inputs that define the same type give `CS0101`, `CS0111`, and `CS0121`. The messages name the files, not the package. | Duplicate types are already an error. The message needs the package that owns each file. |
| A second `Main` gives `CS0017`. A library with no `Main` gives `CS5001` and nothing else. | A library cannot be checked on its own. Step 3 adds a check mode. |
| Swapping the input order changes the order of declarations in the `.ll` file, not their content. | The list must be in a fixed order for a reproducible build. Step 2 sets it. |
| `ARC3001` and `CS` errors in library code carry the file and line, including in methods nobody calls. | Unsupported constructs in a library are already found. They only need the package name. |
| `ARC3100` for a cycle inside library code prints no file. | A cycle warning cannot yet be traced to its package. Step 4 fixes it. |
| An unsupported BCL type, such as `System.Threading.Tasks`, gives `CS0234` in the library file. | The same rule applies. The diagnostic must say which package needs the API. |

**Decisions to settle first.** Step 1 settles each of these and records them in
a feature document, `docs/features/source-libraries.md`, before any code is
written.

1. **The package convention.** A source package puts its files under
   `contentFiles/cs/any/`, marks them `buildAction="Compile"`, and carries the
   tag `arcsharp-source`. NuGet then adds the files to the consumer's
   `Compile` items with `NuGetPackageId` and `NuGetPackageVersion` metadata,
   which is what the SDK reads. The open question is what the SDK does with a
   package that ships `.cs` content without the tag. The default is to refuse
   it with an error, because the package author has not said the source is
   compatible. A source library that references another source package must
   allow `contentFiles` to flow through that reference. Set `PrivateAssets`
   to `analyzers;build` on the source dependency so the generated NuGet
   dependency includes its content files. The consumer reads the resolved
   `Compile` items for both direct and transitive source packages.
2. **The origin label.** `<package id>/<version>` for a package, and the
   project name for a project reference. It appears in diagnostics.
3. **Warnings from library code.** Whether `-Werror` applies to them. The
   default is that it does, because an ignored `ARC3100` leaks, and the
   escape is the `--nowarn` option from step 4.
4. **Reserved namespaces.** Whether a package may declare types in `System` or
   `System.*`. The default is an error, because the embedded `corelib` owns
   that space and may grow into any name a package picked.

**Steps.**

1. **Design record.** Write `docs/features/source-libraries.md` with the four
   decisions above, the table of observations, and the trade-offs. Link it from
   `docs/README.md`.
2. **Response files, origin labels, and a fixed order.** Add `@<file>` to the
   command line, with one argument per line, blank lines ignored, and `#`
   comments. Add `--origin <label>`, which applies to the inputs that follow it
   until the next `--origin`. The application's own files come first and carry
   no label. The compiler keeps the order it is given. The SDK owns the order
   it generates: dependencies before dependents, then ordinal path order.
   Update `Program.cs`, `Options`, the usage text, and
   [`docs/cli-reference.md`](cli-reference.md).
3. **Check mode.** Add `--check`. It runs Roslyn binding, the unsupported-
   syntax and lifetime rules, and lowering, reports every diagnostic, needs no
   entry point, and writes no files. It compiles as a library for Roslyn, which
   removes `CS5001`. The step also finds out, and documents, which diagnostics
   fire for generic code that nothing instantiates, because a check run must
   not claim more than it covers.
4. **Diagnostics from library code.** Every diagnostic from a labelled input
   ends with `[from <label>]`. `ARC3100` gains a file and line, taken from the
   first type in the cycle. Duplicate-type errors name both owners. Add
   `--nowarn <code>` so that an application can silence a warning it cannot fix
   in a package's source, since a `#pragma` in that source is not the
   application's to edit. Update [`docs/diagnostics.md`](diagnostics.md).
5. **Boundary rules.** Document the rules a package author must follow: a
   package keeps its types in its own namespace, ships no `Main`, ships no
   `global using` that would leak into the application, and does not rely on
   `internal` being private. Enforce the reserved namespace rule with a new
   `ARC2xxx` error, allocated when it is written. Add the section to
   [`docs/language-reference.md`](language-reference.md) and the cycle rule to
   [`docs/memory-model.md`](memory-model.md): a cycle inside a library leaks in
   every application that uses it, and `WeakReference<T>` is the fix.
6. **Local source references (needs M13).** A library project sets
   `ArcSharpLibrary` to `true`. An application's `ProjectReference` to it adds
   the library's `Compile` items to the application's list, with the project
   name as the origin, instead of linking an assembly. References through
   other projects are followed. The library project must also build under an
   ordinary `Microsoft.NET.Sdk`, so its authors can test it on .NET.
7. **Source packages (needs M13).** `dotnet pack` on a library project produces
   the package described in decision 1. A `PackageReference` restores it, and
   the SDK passes the direct and transitive source files once, with their
   labels. The SDK reports an error if a package provides assembly assets
   for the program and supplies no ArcSharp source. Analyser and build-only
   assets can still be used by MSBuild and are excluded from ArcSharp's input
   list. This allows the M13 analyser package alongside source libraries.
   Applications commit `packages.lock.json` and restore in locked mode in CI.
   NuGet verifies the hashes, and the project builds no pin system of its own.
8. **Analyser coverage.** The analyser already reports `ARC3001` and `ARC3100`
   from `dotnet build`. Add an xUnit test with the unsupported construct in a
   second syntax tree, to prove the diagnostic points at the library file.
   Package files arrive as `Compile` items, so no analyser change is expected.
   If the test shows otherwise, fix it in this step.
9. **One real library, used twice.** Add a small library inside the supported
   subset under `samples/libs/`, such as a text helper written against
   `StringBuilder` and `List<T>`. Two applications use it: first through a
   project reference, then through a package from a local folder feed, so the
   restore needs no network. The two routes must print the same output and
   report the same `[arc]` counts. Give the library a dependency on a second
   source library and exercise code from it on both routes. The packaged app
   references only the first package and also uses the M13 analyser package.
   Extend the `verify.ps1` helpers to take a response file and a library
   directory.

**Verification.** `tools\verify.ps1` gains these checks.

- A sample for each route, from step 9, with `live=0` and its expected output.
  The package route must restore and compile the second library through the
  first package's dependency, and build with the M13 analyser enabled.
- A must-fail sample for each of these, with the expected code and the origin
  label in the message: a library that uses an unsupported construct, a library
  that uses an unsupported BCL type, two packages that define the same type,
  a package that declares a type in `System`, and an assembly-only program
  library package.
- A check-mode run over the library alone, which must succeed, and over a
  broken library, which must fail with the library's file and label.
- A reproducibility check. The SDK-generated list, built twice, gives
  identical `.ll` files, and a shuffled list gives the same program output and
  accounting.
- A warning check. An `ARC3100` from a package names the package, and
  `--nowarn ARC3100` silences it, including under `-Werror`.
- The analyser test from step 8.
- A sample that records the `internal` behaviour, so that a later change to it
  is deliberate.

**Documentation.** Besides the files named above, update
[`README.md`](../README.md) (feature summary and status),
[`docs/getting-started.md`](getting-started.md) (how to add a library),
[`docs/architecture.md`](architecture.md) (the pipeline and the status table),
[`docs/verification.md`](verification.md) (new samples and counts), and
`docs/README.md` (the milestone count). Every example program in the guides
must compile and run.

**Done when.** One library is used from two applications, by project reference
and by a restored package, with the same output and the same accounting, and
`tools\verify.ps1` passes. A broken library names its package and file in the
diagnostic. The boundary rules are documented. The medians from
`tools\compare-gc.ps1 -Runs 5` are added to the
[GC comparison](features/gc-comparison.md).

**Not part of this milestone.**

- Compiled assemblies as input. See [Decisions](#decisions).
- Several versions of one library in one program, and enforced `internal`
  hiding. Both need separate compilation units, which the compiler does not
  have.
- A Git-based fetcher, an ArcSharp manifest, and a private package registry.
  NuGet feeds already cover the last.
- Publishing a package to nuget.org. The tooling makes the package. Releasing
  it is the author's decision.
- Source generators and analysers that run during the restore.

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
| Compiled .NET assemblies as libraries, side-by-side library versions, and a Git-based package fetcher | Outside the target. A library is source compiled with the program. See [M14](#m14--shared-source-libraries). |
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
