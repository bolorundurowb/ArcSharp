# ArcSharp — Architecture & Design

ArcSharp is a proof-of-concept compiler for a subset of C# 12 that replaces the
.NET garbage collector with **ARC (Automatic Reference Counting)**. It emits
textual **LLVM IR**, links against a small **C runtime**, and produces a native,
GC-less executable. The intended target is **Windows x64**
(`x86_64-pc-windows-msvc`); the compiler itself is written in C# (.NET 8,
LangVersion 12).

> Status: first pass / PoC. The goal is to prove the pipeline and the ARC memory
> model end-to-end, not to be a conformant C# implementation. See
> [Unsupported features](#features-arc-cannot-reasonably-support-first-pass),
> [Open questions](#open-questions), and the production-readiness
> **[ROADMAP.md](ROADMAP.md)**.

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
   arc_runtime.c ──(clang/gcc)──▶ runtime.o          llc ──▶ program.o ──┐
                                       │                                 │
                                       └──────────── linker ─────────────┴──▶ program.exe
```

Source layout:

| Path                    | Responsibility                                                                                                            |
|-------------------------|---------------------------------------------------------------------------------------------------------------------------|
| `src/Lexing/`           | `Token`, `SyntaxKind`, `Lexer` — text → token stream                                                                      |
| `src/Syntax/`           | AST node records + `Parser` (recursive descent)                                                                           |
| `src/Binding/`          | `TypeSymbol`, `MethodSymbol`, `FieldSymbol`, `Binder` — name/type resolution, layout, vtables, type checking → bound tree |
| `src/CodeGen/`          | `Emitter` — bound tree → LLVM IR, with ARC ops woven in                                                                   |
| `src/Driver/`           | `Compilation`, CLI, invocation of `llc` + linker                                                                          |
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
object. They never participate in ARC.

### Dispatch
- **Non-virtual / static calls** are emitted as direct `call`s to the mangled
  function symbol.
- **Virtual / override calls** load the function pointer from
  `obj->type->vtable[slot]` and call indirectly. Slots are assigned by the
  binder so an override occupies the same slot as the base method.
- **Interface calls** *(design)* search the type's interface-table for the
  interface id, then index the method slot. See status in Section 8.

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
| Discarded (expression statement)         | Released immediately                                                                         |

Two cleanup scopes guarantee balance:

1. **Statement temporaries** — every +1 not consumed by a store/return is
   released at the end of the enclosing full statement.
2. **Locals** — each reference-typed local is a stack slot initialised to null
   and released on every path that leaves its scope (fall-through *and*
   `return`).

Object teardown: the binder generates a `__deinit_<Type>` that releases each
strong reference field (and, for arrays, each element); `arc_release` calls it
when `strong` hits 0. This is what makes destruction recursive and
deterministic.

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
compiles unchanged with Roslyn** (verified in `VERIFICATION.md`).

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

- **Types**: `int` (i32), `uint` (i32), `long` (i64), `ulong` (i64), `bool` (i1),
  `void`, `string`, `char` (i32), `float` (f32), `double` (f64), `byte`/`sbyte`
  (i8), `short`/`ushort` (i16); user `class`es; single-dimension arrays `T[]`.
  `struct` declarations are parsed and bound; the emitter does not lower them yet.
- **Classes**: instance fields (including `WeakReference<T>`), constructors,
  instance methods, static methods and fields, single inheritance,
  `virtual`/`override` dispatch, and auto-properties (a private backing field
  plus a getter and a setter).
- **Interfaces**: declaration and itable dispatch.
- **Parameters**: `ref`, `out`, and `in`, lowered to managed references. A
  reference-typed `ref` borrows the slot and does not transfer ownership.
  Reassigning a reference-typed parameter is rejected.
- **Type tests and arrays**: `is` and `as` walk the `TypeInfo` parent chain at
  run time. A class downcast is checked; an interface downcast is accepted
  without a runtime test. A store into a reference-element array checks
  assignability. A mismatch prints `ArrayTypeMismatchException` and exits.
  Value-type arrays stay invariant.
- **`System.WeakReference<T>`**: `new`, `TryGetTarget(out ...)`, `SetTarget(...)` —
  plus the `Name<T>` parsing and `out` arguments that those calls need.
- **Statements**: blocks, local declarations with an initialiser, assignment,
  `if`/`else`, `while`, `for`, `return`, expression statements.
- **Expressions**: integer, unsigned, floating, `char`, `bool`, `string`, and
  `null` literals; identifiers; `this`; field, property, and static member
  access; method invocation (static, instance, virtual, interface); `new T(args)`;
  `new T[n]`; array indexing (load and store); `+ - * / %`, `== != < <= > >=`,
  `&& || !`, unary `-`; string `+` concatenation; explicit numeric casts;
  `is` / `as`; parenthesisation; assignment as an expression.
- **Built-ins**: `System.Console.Write` / `WriteLine` for `string`, `bool`, the
  integer types, `float`, and `double`, plus `.Length` on arrays and strings.
- **Compilation unit**: one or more `.cs` files. A `using` directive is skipped,
  so a file may keep `using System;`. A `namespace` declaration is flattened and
  its name is discarded. A trailing `?` on a type is accepted and ignored.
- **Entry point**: `static void Main()` or `static int Main()`.

---

## 7. Features ARC cannot reasonably support (first pass)

Catalogued honestly, split into *fundamental* (ARC semantics fight the feature)
and *scope* (just not built yet).

### Fundamental tensions with ARC
- **Uncollected reference cycles.** Pure ARC leaks any strong cycle. Mitigated,
  not solved, by `weak`/`unowned`; the burden moves to the programmer. A real
  product would need a backup cycle collector (the hard problem GC solves for
  free).
- **Multithreaded sharing.** Correct ARC across threads requires *atomic*
  retain/release (or ownership transfer rules). ArcSharp's counts are
  non-atomic, so shared mutable references across threads are unsound. Atomics
  are a known, measurable throughput cost.
- **`GC.*` / finaliser semantics.** `GC.Collect`, `GC.KeepAlive`,
  `WeakReference`, resurrection, and the two-phase finaliser queue assume a
  tracing collector. ArcSharp offers deterministic destructors instead; the GC
  API surface cannot be honoured.
- **`async`/`await` & iterator state machines.** Lifetimes that span suspension
  points must be threaded through a heap-allocated state machine; getting ARC
  retain/release correct across `await` boundaries (and avoiding cycles in
  captured continuations) is a substantial design effort. Out of scope.
- **Closures / lambda capture.** Captured variables become heap closure
  objects; capturing `this` readily forms cycles. Supportable in principle (with
  the same `weak`-capture discipline Swift uses) but deferred.

### Out of scope for the first pass (no fundamental conflict)
- **Generics.** Requires monomorphisation (or a uniform boxed representation).
  `Name<T>` is parsed only so `System.WeakReference<T>` can be recognised.
- **Reflection / `dynamic` / attributes at runtime**, `unsafe`/pointers,
  `stackalloc`, pinning.
- **Exceptions** (`try`/`catch`/`finally`). Every unwinding path must release
  live locals. The cleanup model anticipates that, and the emitter does not yet
  emit landing pads.
- **Still absent from the language subset**: `decimal`, `enum`, nullable value
  types, indexers, events, delegates, pattern matching, LINQ, `using`
  statements / `IDisposable`, operator overloading, partial classes, extension
  methods, `params`, optional and named arguments, and bitwise operators
  (`&`, `|`, `^`, shifts). A single `&` or `|` is a lexer error (`ARC0003`).
- **Null safety.** A trailing `?` is parsed and then ignored.
- **Namespace identity.** Declarations from every file are flattened into one
  program, so two types with the same simple name collide.
- **The BCL**, beyond a hard-coded `Console` shim.
- **Struct code generation.** Structs are parsed and bound. The emitter skips
  them, and copying a struct that contains a reference field is still an open
  design question (Section 10).

---

## 8. Implementation status (first pass)

| Area                                                   | Status                                |
|--------------------------------------------------------|---------------------------------------|
| Lexer, parser, AST                                     | Implemented                           |
| Binder: classes, inheritance, fields, methods, statics | Implemented                           |
| Type checking (core)                                   | Implemented (lightweight)             |
| Virtual / override dispatch (vtables)                  | Implemented                           |
| ARC: retain/release/deinit, statement & scope cleanup  | Implemented                           |
| Weak fields + cycle reclamation                        | Implemented                           |
| Arrays (value & reference element types)               | Implemented                           |
| Strings + `Console`                                    | Implemented                           |
| Control flow (`if`/`while`/`for`/`return`)             | Implemented                           |
| `float` / `double`                                     | Implemented                           |
| `byte` / `sbyte` / `short` / `ushort` / `uint` / `ulong` | Implemented                         |
| `ref` / `out` / `in` parameters                        | Implemented                           |
| Auto-properties                                        | Implemented                           |
| `is` / `as`                                            | Implemented                           |
| Array covariance (runtime store check)                 | Implemented                           |
| Structs (value types)                                  | Parsed and bound; code generation pending |
| Interfaces (itable dispatch)                           | Implemented and verified              |
| Diagnostics with codes (`ARC0001`…) and severities     | Implemented                           |
| Multi-file compilation (merged type declarations)      | Implemented                           |
| Definite-assignment analysis (conservative, warns)     | Implemented                           |
| Generics, async, closures, exceptions, `using`         | Not implemented (Section 7 / ROADMAP) |

See `VERIFICATION.md` (generated by the test run) for the exact set of sample
programs that compile and run, with ARC alloc/free accounting.

---

## 9. Targeting Windows x64 & portability

The emitter writes `target triple = "x86_64-pc-windows-msvc"` and the matching
`target datalayout` when invoked with `--target windows` (the default for
producing a `.exe`). Because both Windows and the Linux CI host are x86-64 and
the runtime is plain C, the *same IR* is validated on Linux
(`x86_64-pc-linux-gnu`) during automated testing via `llc` + `gcc`; only the
triple/datalayout and the C runtime's `printf` linpage differ. On Windows the
runtime object is produced with `clang -target x86_64-pc-windows-msvc` (or
MSVC's `cl`) and linked with `lld-link`/`link.exe`.

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
x86-64 Linux. The latest recorded run is Windows x64 (`VERIFICATION.md`).
What it does **not** yet provide: 32-bit, ARM, or big-endian targets. Making those work is the **P1 "Multi-target"** roadmap item
— parameterise the triple/data layout into a `TargetInfo`, lower ABIs per target,
and drop the hard-coded 8-byte-slot assumption. In short: GC-less native C# via
LLVM, currently x86-64-scoped, portable in design.

---

## 10. Open questions

1. **Cycle policy.** *(Partly decided.)* A strong cycle still leaks, and the
   programmer breaks it with `WeakReference<T>`. The compiler and a Roslyn
   analyser will warn when strong fields can form one (`ROADMAP.md` section 7).
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
   question: do we also want weak *locals* / an `unowned` (non-zeroing) variant?
5. **Value types with reference fields.** Copying such a struct must retain its
   reference fields, and releasing a copy must release them. Structs are parsed
   and bound, and code generation is still pending. When that lands, do we
   allow reference fields and accept the copy cost?
6. **Exceptions vs. ARC unwinding.** If exceptions are in scope, every cleanup
   scope must also be a landing pad. Confirm exceptions are wanted before
   committing the emitter to DWARF/SEH unwinding.
7. **Determinism guarantees.** Do we promise deterministic destruction order as
   part of the language contract (it changes valid optimisations)?
8. **Optimisation budget.** How much retain/release elision (ownership/borrow
   analysis) is expected for the PoC vs. a later milestone?
