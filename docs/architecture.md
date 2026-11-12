# ArcSharp — Architecture & Design

ArcSharp is a proof-of-concept compiler for a subset of C# 12 that replaces the
.NET garbage collector with **ARC (Automatic Reference Counting)**. It emits
textual **LLVM IR**, links against a small **C runtime**, and produces a native,
GC-less executable. The intended target is **Windows x64**
(`x86_64-pc-windows-msvc`); the compiler itself is written in C# (.NET 8,
LangVersion 12).

> Status: first pass / PoC. The goal is to prove the pipeline and the ARC memory
> model end-to-end, not to be a conformant C# implementation. See
> [Unsupported features](#7-features-arc-cannot-reasonably-support-first-pass),
> [Open questions](#10-open-questions), and the production-readiness
> **[Roadmap](roadmap.md)**.
>
> This document describes the compiler's internals. For the user-facing
> description of the language and the memory model, read the
> [Language reference](language-reference.md) and the
> [Memory model](memory-model.md).

---

## 1. Why ARC instead of GC

A tracing GC discovers liveness by periodically walking the object graph from
roots. ARC instead makes liveness *local and deterministic*: every object
carries a count of how many strong references point at it, the count is adjusted
as references are created and destroyed, and the object is freed the instant the
count reaches zero.

|                 | Tracing GC (.NET)                   | ARC (ArcSharp)                               |
|-----------------|-------------------------------------|----------------------------------------------|
| Reclamation     | Deferred, at collection time        | Immediate, at last release                   |
| Pauses          | Stop-the-world phases               | None (work is spread inline)                 |
| Per-object cost | Amortised                           | A counter field + retain/release traffic     |
| Cycles          | Collected automatically             | **Leak** unless broken by `WeakReference<T>` |
| Determinism     | Finalisers run nondeterministically | Destructors run deterministically            |

The defining trade-off — and the reason GC exists — is the last row: naive ARC
cannot reclaim **reference cycles**. ArcSharp addresses this with the standard
`System.WeakReference<T>` type (Section 5).

---

## 2. Compiler pipeline

```
 .cs source
    │
    ▼
┌───────────┐   tokens     ┌──────────┐   AST       ┌──────────┐
│  Lexer    │ ───────────▶ │  Parser  │ ─────────▶ │  Binder  │
└───────────┘              └──────────┘             └────┬─────┘
                                                         │ typed bound tree
                                                         │ + type layouts, vtables
                                                         ▼
                                                ┌───────────────┐
                                                │  ARC model    │  (ownership rules
                                                │  (in CodeGen) │   applied during emit)
                                                └──────┬────────┘
                                                       ▼
                                                ┌───────────────┐   .ll
                                                │ LLVM emitter  │ ───────┐
                                                └───────────────┘        │
                                                                         ▼
                    arc_runtime.c ──────────────▶ clang ◀──────── program.ll
                                                    │
                                                    ▼
                                               program.exe
```

`clang` compiles the IR and the runtime and links them in one step; this is
the `--clang` path and the only one in the current verification. Without
`--clang`, the driver runs `llc` to produce an object file. On Windows it then
stops and prints the link command; with `--target host` it links the object and
the runtime with `gcc`.

Source layout:

| Path                    | Responsibility                                                                                                            |
|-------------------------|---------------------------------------------------------------------------------------------------------------------------|
| `src/Lexing/`           | `Token`, `TokenKind`, `Diagnostic`, `Lexer` — text → token stream                                                         |
| `src/Syntax/`           | AST node classes + `Parser` (recursive descent)                                                                           |
| `src/Binding/`          | `TypeSymbol`, `MethodSymbol`, `FieldSymbol`, `Binder` — name/type resolution, layout, vtables, type checking → bound tree; `DefiniteAssignment` warnings |
| `src/CodeGen/`          | `Emitter` — bound tree → LLVM IR, with ARC ops woven in                                                                   |
| `src/Driver/`           | `Compilation` — orchestrates the stages and invokes `clang`, or `llc` and a linker                                        |
| `src/Program.cs`        | Command-line parsing                                                                                                      |
| `runtime/arc_runtime.c` | Object header, retain/release, weak table, type info, strings, arrays, console                                            |
| `samples/`              | Example `.cs` programs used as tests                                                                                      |

---

## 3. The object model

Every heap value (class instance, array, string) is preceded by a fixed header:

```c
typedef struct ObjHeader {
    intptr_t   strong;   // strong reference count
    intptr_t   weak;     // weak reference count (+1 while object is live)
    TypeInfo*  type;     // pointer to the type descriptor
} ObjHeader;
```

The object's fields follow immediately after the header. A reference (`T r`) is
just a pointer to the header; field/element access adds the field offset.

`TypeInfo` is a statically emitted, per-type descriptor:

```c
typedef struct TypeInfo {
    const char* name;
    intptr_t    instanceSize;     // header + fields, in bytes
    void      (*deinit)(void*);   // releases all strong fields of an instance
    void**      vtable;           // virtual method slots (function pointers)
    intptr_t    vtableLen;
    void**      itable;           // interface method slots, indexed by selector
    intptr_t    itableLen;
    TypeInfo*   base;             // base class, or NULL; used by is/as
} TypeInfo;
```

Value types (`int`, `bool`, `long`, structs) are **not** boxed and carry no
header; they live in registers, on the stack, or inline inside a containing
object. They never participate in ARC themselves, but a struct can contain
reference fields, and those are retained and released by the struct helpers
described in Section 6.

### Dispatch
- **Non-virtual / static calls** are emitted as direct `call`s to the mangled
  function symbol.
- **Virtual / override calls** load the function pointer from
  `obj->type->vtable[slot]` and call indirectly. Slots are assigned by the
  binder so an override occupies the same slot as the base method.
- **Interface calls** load the function pointer from
  `obj->type->itable[selector]`. The binder gives every interface method a
  selector that is unique across the whole program, and each class's itable has
  one slot per selector, with `null` for the selectors it does not implement.
  This makes a call a single indexed load, at the cost of itables that grow with
  the number of interface methods in the program.

---

## 4. The ARC model (ownership rules)

ArcSharp uses a deliberately simple, locally-checkable discipline that is
**correct for single-threaded programs**. The counts are non-atomic.

### Runtime primitives (in `arc_runtime.c`)

```
arc_alloc(type)            → strong=1, weak=1, zeroed fields
arc_retain(p)              → if p: ++p->strong
arc_release(p)             → if p && --p->strong == 0:
                                 p->type->deinit(p)      // release strong fields
                                 arc_weak_release(p)     // drop the implicit live-count
arc_weak_retain(p)         → if p: ++p->weak
arc_weak_release(p)        → if p && --p->weak == 0: free(p)
arc_assign_take(slot,v)    → old=*slot; *slot=v; release old   // v is already +1
arc_store_strong(slot,v)   → retain v; old=*slot; *slot=v; release old
arc_store_weak(slot,v)     → weak_retain v; old=*slot; *slot=v; weak_release old
arc_load_weak(slot)        → p=*slot; (p && p->strong>0) ? (retain p, p) : null
```

The `weak=1`-at-birth trick keeps the *allocation* alive (so weak slots never
dangle) even after the object is logically dead (`strong==0`); the backing
memory is freed only when the last weak reference also goes away.

### Compile-time discipline (applied by the emitter)

The emitter normalises every evaluated reference expression to **+1 (owned)**:

- `new T(...)` and reference-returning calls yield **+1** by convention.
- Reading a local, field, parameter, `this`, or array element yields a borrowed
  value that the emitter immediately **retains to +1**.

A +1 value is then *consumed* in exactly one of these ways, or released:

| Context                                  | Disposition of the +1 value                                                                  |
|------------------------------------------|----------------------------------------------------------------------------------------------|
| Stored into a local / field / array slot | Ownership transferred: release the slot's **old** value, write the new one (no extra retain) |
| Passed as a call argument                | Borrowed by callee; caller releases it at **end of statement**                               |
| Used as a call receiver / member target  | Borrowed; released at end of statement                                                       |
| `return`ed                               | Becomes the caller's +1; not released                                                        |
| Discarded (expression statement)         | Released at end of statement                                                                 |

Two cleanup scopes guarantee balance:

1. **Statement temporaries** — every +1 not consumed by a store/return is
   released at the end of the enclosing full statement.
2. **Locals** — each reference-typed local is a stack slot initialised to null
   and released at `func.exit`. Every `return` stores its value in `%retval`
   and branches there, so locals are released once per call on every path.
   A local declared in an inner block therefore lives until the method
   returns, not until the block ends.

Object teardown: the emitter generates `<Type>__deinit` for every class. It
releases each strong reference field and clears each weak field; the runtime
releases each element when an array of references dies. `arc_release` calls
the destructor when `strong` hits 0. This is what makes destruction recursive
and deterministic.

> This "retain every read to +1, release temporaries per statement, release
> slots at scope exit" rule over-retains slightly versus an optimising ARC
> implementation. That is intentional: it is simple, local, and provably
> balanced, which is what a first pass needs. Optimisation (eliding
> retain/release pairs) is future work.

---

## 5. Weak references (cycle breaking) via `System.WeakReference<T>`

Pure ARC leaks strong reference cycles. Rather than invent syntax, ArcSharp
recognises the **standard BCL type `System.WeakReference<T>`** and lowers it to a
non-owning (weak) handle. This keeps ArcSharp source **idiomatic C# that also
compiles unchanged with Roslyn** (see [Verification](verification.md)).

```csharp
using System;
class Parent { public Child child; }                 // strong
class Child  { public WeakReference<Parent> owner; }  // weak: breaks the cycle

child.owner = new WeakReference<Parent>(parent);
if (child.owner.TryGetTarget(out var p)) { /* p is a live, +1 reference */ }
```

Lowering:

- A `WeakReference<T>` value is itself a small ARC-managed object: a standard
  24-byte header plus one 8-byte slot at offset 24 holding a **weak** reference to
  the target. It is strongly owned by whatever field/local holds it; when that
  owner is released, the `WeakReference` object's destructor weak-releases its
  target. So `Child` strongly owns the `WeakReference`, which only weakly points at
  `Parent` — no cycle.
- `new WeakReference<T>(x)` → `arc_weakref_new(x)` (weak-retains `x`).
- `wr.TryGetTarget(out var p)` → `arc_weakref_try_get(wr)`, which returns a live
  `+1` reference or `null` (via `arc_load_weak`); the result is stored into `p`
  and the call yields `true`/`false`. Under ARC this flips to `false` the instant
  the target's last strong reference is gone — **deterministically**, unlike the
  CLR where it depends on GC timing.
- `wr.SetTarget(x)` → `arc_weakref_set(wr, x)` (`arc_store_weak`).

To support this the compiler also gained minimal **generic-type parsing**
(`Name<T>` in type and `new` positions) and **`out` arguments**
(`out var p`, `out T p`, `out existing`).

This is the mechanism that lets parent⇄child and other cyclic graphs be reclaimed.
The verification suite includes a cycle that leaks under plain strong references
and is fully reclaimed once the back-edge is a `WeakReference<T>`.

> A legacy, non-standard `weak` **field modifier** also still works (it lowers to
> the same `arc_store_weak`/`arc_load_weak` primitives) but is not idiomatic C# and
> will not compile with Roslyn; `WeakReference<T>` is the recommended form. Weak
> *locals* and an `unowned` (non-zeroing) variant remain future work.

---

## 6. Supported C# 12 subset (first pass)

The [Language reference](language-reference.md) is the authoritative list of
what the compiler accepts, how it differs from .NET, and its known defects.
This section records only how the subset is lowered.

- **Value types** map to LLVM integers and floats: `bool` is `i1`, `byte` and
  `sbyte` are `i8`, `short` and `ushort` are `i16`, `int`, `uint`, and `char`
  are `i32`, `long` and `ulong` are `i64`, `float` is `float`, and `double` is
  `double`. Every field and array element still occupies an 8-byte slot.
- **Classes** get a `TypeInfo`, a vtable when they have virtual methods, an
  itable when the program declares interface methods, and a generated
  `__deinit`. Auto-properties lower to a backing field plus `get_` and `set_`
  methods.
- **Structs** lower to an opaque `[HeapSize / 8 x i64]` value. Field `i` lives at
  slot `i`, so nested structs keep their full size. Every struct gets
  `<T>__copy` (value semantics: copy value fields, retain reference fields) and
  `<T>__drop` (release reference fields); a class `__deinit` calls `__drop` for
  each struct field. A struct local is zero-initialised and dropped at
  `func.exit`, and a struct temporary is dropped at the end of its statement.
- **`ref`, `out`, and `in`** lower to pointers to the slot. A reference-typed
  `ref` borrows the slot and does not transfer ownership.
- **`is` and `as`** call `arc_is_instance`, which walks the `TypeInfo` base
  chain. An explicit cast `(T)x` is a `BoundConversion` with no runtime test,
  for classes and interfaces alike.
- **Array stores** into a reference-element array call
  `arc_array_store_check`, which prints `ArrayTypeMismatchException` and exits
  on a mismatch. Value-type arrays stay invariant.
- **`System.WeakReference<T>`** is the only generic type. `Name<T>` is parsed in
  field types and `new` expressions so that it can be recognised.
- **`using`** owns the resource value in a scoped local (`arc_assign_take`),
  calls its `Dispose()` method at scope exit — guarded against `null`,
  dispatched through the vtable or itable when the method is `virtual` or the
  resource is interface-typed — and then releases the resource and nulls the
  slot, so the `func.exit` release of that local sees `null`. A `return` inside
  the body runs the same cleanup, innermost first for nested `using`
  statements, before branching to `func.exit`.
- **`Console.Write` and `WriteLine`** and **`.Length`** are bound directly to
  runtime functions; there is no library behind them.
- **Compilation units** are merged: `using` directives are skipped, namespace
  names are discarded, and a trailing `?` on a type is ignored.
- **Field initialisers** are parsed and then discarded. This is a known defect,
  not a design choice.

---

## 7. Features ARC cannot reasonably support (first pass)

Catalogued honestly, split into *fundamental* (ARC semantics fight the feature)
and *scope* (just not built yet).

### Fundamental tensions with ARC
- **Uncollected reference cycles.** Pure ARC leaks any strong cycle. Mitigated,
  not solved, by `WeakReference<T>`; the burden moves to the programmer. A real
  product would need a backup cycle collector (the hard problem GC solves for
  free).
- **Multithreaded sharing.** Correct ARC across threads requires *atomic*
  retain/release (or ownership transfer rules). ArcSharp's counts are
  non-atomic, so shared mutable references across threads are unsound. Atomics
  are a known, measurable throughput cost.
- **`GC.*` / finaliser semantics.** `GC.Collect`, `GC.KeepAlive`,
  resurrection, the two-phase finaliser queue, and the timing of
  `WeakReference<T>.TryGetTarget` assume a tracing collector. ArcSharp offers deterministic destructors instead; the GC
  API surface cannot be honoured.
- **`async`/`await` & iterator state machines.** Lifetimes that span suspension
  points must be threaded through a heap-allocated state machine; getting ARC
  retain/release correct across `await` boundaries (and avoiding cycles in
  captured continuations) is a substantial design effort. Out of scope.
- **Closures / lambda capture.** Captured variables become heap closure
  objects; capturing `this` readily forms cycles. Supportable in principle (with
  the same weak-capture discipline Swift uses) but deferred.

### Out of scope for the first pass (no fundamental conflict)
- **Generics.** Requires monomorphisation (or a uniform boxed representation).
  `Name<T>` is parsed only so `System.WeakReference<T>` can be recognised.
- **Reflection / `dynamic` / attributes at runtime**, `unsafe`/pointers,
  `stackalloc`, pinning.
- **Exceptions** (`try`/`catch`/`finally`). Every unwinding path must release
  live locals. The cleanup model anticipates that, and the emitter does not yet
  emit landing pads.
- **Still absent from the language subset**: `decimal`, `enum`, nullable value
  types, indexers, events, delegates, pattern matching, LINQ, operator
  overloading, partial classes, extension methods, `params`, optional and named
  arguments, and bitwise operators (`&`, `|`, `^`, shifts). A single `&` or `|`
  is a lexer error (`ARC0003`).
- **Null safety.** A trailing `?` is parsed and then ignored.
- **Namespace identity.** Declarations from every file are flattened into one
  program, so two types with the same simple name collide.
- **The BCL**, beyond a hard-coded `Console` shim.
- **Struct features outside the current subset.** Arrays of structs, struct
  return values, static struct fields, by-value struct parameters, properties
  of struct type, printing a struct, `virtual`/`override` members on structs,
  interfaces on structs, `using` a struct resource, and self-containing
  structs are rejected as `ARC3001`.

---

## 8. Implementation status (first pass)

| Area                                                   | Status                                |
|--------------------------------------------------------|---------------------------------------|
| Lexer, parser, AST                                     | Implemented                           |
| Binder: classes, inheritance, fields, methods, statics | Implemented                           |
| Type checking (core)                                   | Implemented (lightweight)             |
| Virtual / override dispatch (vtables)                  | Implemented                           |
| ARC: retain/release/deinit, statement & scope cleanup  | Implemented                           |
| `WeakReference<T>` fields + cycle reclamation          | Implemented (fields only, not locals) |
| Arrays (value & reference element types)               | Implemented                           |
| Strings + `Console`                                    | Implemented                           |
| Control flow (`if`/`while`/`for`/`return`)             | Implemented                           |
| `float` / `double`                                     | Implemented                           |
| `byte` / `sbyte` / `short` / `ushort` / `uint` / `ulong` | Implemented                         |
| `ref` / `out` / `in` parameters                        | Implemented                           |
| Auto-properties                                        | Implemented for instance properties; static ones produce invalid IR |
| `is` / `as`                                            | Implemented                           |
| Array covariance (runtime store check)                 | Implemented                           |
| Structs (value types)                                  | Implemented with the restrictions in Section 7 |
| `using` / `IDisposable` (deterministic `Dispose`)      | Implemented                           |
| Interfaces (itable dispatch)                           | Implemented and verified              |
| Diagnostics with codes (`ARC0001`…) and severities     | Implemented                           |
| `ARC3001` unsupported-construct diagnostics            | Implemented (compiler and analyser)   |
| `ARC3100` possible strong-cycle warning                 | Implemented (compiler and analyser)   |
| Roslyn analyser + unit tests (`analyzers/`)            | Implemented (62 xUnit cases)          |
| Multi-file compilation (merged type declarations)      | Implemented                           |
| Definite-assignment analysis (conservative, warns)     | Implemented                           |
| Field initialisers                                     | Parsed and discarded (defect)         |
| Generics, async, closures, exceptions                  | Not implemented (Section 7 / [Roadmap](roadmap.md)) |

See [Verification](verification.md) for the exact set of sample programs that
compile and run, with ARC alloc/free accounting, and the
[Known issues](language-reference.md#known-issues) for defects found outside the
samples.

---

## 9. Targeting Windows x64 & portability

The emitter writes `target triple = "x86_64-pc-windows-msvc"` and the matching
`target datalayout` when invoked with `--target windows`, which is the default.
With `--clang`, `clang` compiles that module together with
`runtime/arc_runtime.c` and links the result with the Microsoft linker. This is
the only path the [verification harness](verification.md) exercises.

`--target host` writes `x86_64-pc-linux-gnu` instead, and the driver then runs
`llc` and `gcc`. Both targets are x86-64 and the runtime is portable C, so the
module differs only in its triple and data layout. The Linux path has no
automated testing and is not part of the current verification; treat it as
untested.

The runtime must respect the data model of each target. Windows x64 is LLP64,
so C `long` is 32 bits there; the runtime uses `intptr_t` and `long long` for
every 64-bit value, and formats with `%lld`.

### Is LLVM IR platform-agnostic?

Partly, and it is worth being precise about it. Lowering through LLVM means
ArcSharp does *not* emit machine code itself, so the back end is portable in
principle. But the IR it emits is **not** target-independent:

- Every module carries a fixed `target triple` and data layout. A data layout
  encodes endianness, pointer size, and alignment, so IR built for one layout is
  not guaranteed correct on another.
- ArcSharp's object model assumes a **uniform 8-byte slot** — 64-bit pointers,
  8-byte field/element stride, 24-byte header. That bakes a 64-bit, 8-byte-aligned
  assumption into field offsets and array indexing.
- ABIs differ per target (System V AMD64 vs Microsoft x64 differ in calling
  convention and aggregate passing); the C runtime must also be compiled per
  platform.

What this buys today: the emitted IR is portable across targets that share an
architecture and ABI family, so one module shape covers x86-64 Windows and
x86-64 Linux. The latest recorded run is Windows x64 ([Verification](verification.md)).
What it does **not** yet provide: 32-bit, ARM, or big-endian targets. Making those work is the **P1 "Multi-target"** roadmap item
— parameterise the triple/data layout into a `TargetInfo`, lower ABIs per target,
and drop the hard-coded 8-byte-slot assumption. In short: GC-less native C# via
LLVM, currently x86-64-scoped, portable in design.

---

## 10. Open questions

1. **Cycle policy.** *(Partly decided.)* A strong cycle still leaks, and the
   programmer breaks it with `WeakReference<T>`. The compiler and the Roslyn
   analyser warn with `ARC3100` when strong fields can form one
   ([Roadmap section 7](roadmap.md#7-roslyn-analyser--warnings-inside-an-ordinary-c-project)).
   Whether a backup cycle collector is also required stays open; that work is
   section 2 of the roadmap.
2. **Threading.** Do we commit to atomic refcounts (and the throughput cost) or
   to a single-threaded / actor-style ownership model?
3. **BCL strategy.** Re-implement a minimal corelib under ARC, or shim to an
   existing native library? `string`, collections, and `IDisposable` all need a
   home.
4. **`weak` surface.** *(Resolved.)* ArcSharp lowers the standard
   `System.WeakReference<T>` so source stays idiomatic and Roslyn-compatible; the
   old non-standard `weak` keyword is retained only as a legacy alias. Remaining
   question: do we also want an `unowned` (non-zeroing) variant? Separately,
   `WeakReference<T>` locals are a parser defect to fix, not an open question.
5. **Value types with reference fields.** *(Resolved.)* Copying a struct
   retains its reference fields, and dropping a copy releases them. The
   remaining restrictions (arrays, returns, static fields, by-value parameters,
   struct-typed properties, printing, `virtual`/`override`, interfaces, and
   self-containment) are `ARC3001` and are tracked in the
   [Roadmap](roadmap.md#1-language--close-the-feature-gap).
6. **Exceptions vs. ARC unwinding.** If exceptions are in scope, every cleanup
   scope must also be a landing pad. Confirm exceptions are wanted before
   committing the emitter to DWARF/SEH unwinding.
7. **Determinism guarantees.** Do we promise deterministic destruction order as
   part of the language contract (it changes valid optimisations)?
8. **Optimisation budget.** How much retain/release elision (ownership/borrow
   analysis) is expected for the PoC vs. a later milestone?
