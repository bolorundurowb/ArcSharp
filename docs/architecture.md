# ArcSharp — Architecture & Design

ArcSharp is a proof-of-concept compiler for a subset of C# 12. The compiler
that exists today replaces the .NET garbage collector with **ARC (Automatic
Reference Counting)**: every heap object has a count, and it is destroyed when
that count reaches zero. It emits textual **LLVM IR**, links against a small
**C runtime**, and produces a native executable. The verified target is
**Windows x64** (`x86_64-pc-windows-msvc`). The compiler itself is written in
C# (.NET 11, LangVersion 12).

> Status: first pass / PoC. The compiler that exists today retains every
> reference and parses with its own frontend. The direction is a separate
> runtime, not a collector for the CLR: Roslyn at the front, an ownership tree
> rather than CIL, ARC only where a lifetime cannot be proved, deterministic
> finalisation, and the [compatibility target](roadmap.md#compatibility-target)
> rather than the whole language or framework. See
> [Current limits](#7-current-limits),
> [Decisions and remaining questions](#10-decisions-and-remaining-questions),
> and the **[Roadmap](roadmap.md)**.
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
| Cycles          | Collected automatically             | Warned, permitted, and a leak if the warning is ignored |
| Determinism     | Finalisers run later, on another thread, and may resurrect | At the last strong release, on that thread. No resurrection. `~T()` is not accepted yet |

The defining trade-off — and the reason GC exists — is reference cycles. Naive
ARC cannot reclaim them. The architectural decision is to warn, permit, and
document the leak. `ARC3100` warns when the type graph can form a cycle. The
program still compiles. `System.WeakReference<T>` (Section 5) is how a cycle is
broken. If the developer or a library ignores the warning, the objects leak
and that result is intended. There is no backup collector.

The counts above are what every heap object does today. The lifetime pass will
skip a retain and release when it can prove the lifetime, on this same header.
Stock CIL is not the representation that pass reads.

---

## 2. Compiler pipeline

What the compiler does today:

```
 .cs source
    │
    ▼
┌───────────┐   symbols    ┌────────────┐  bound tree ┌──────────┐
│  Roslyn   │ ───────────▶ │  Lowering  │ ──────────▶ │ Layout   │
└───────────┘              └────────────┘             └────┬─────┘
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

Roslyn parses and binds, and the lowering builds the same bound tree the
emitter already consumes. A lifetime pass will sit between that tree and the
emitter. The bound tree stays the ownership representation. Stock CIL is not
inserted in front of it: the CLR's IL has already dropped the escape and
last-release facts that finalisation and the lifetime pass require. The
emitter, the object header, and the C runtime stay. The lifetime pass is the
current milestone.

`ref/ArcSharp.References` is the library Roslyn binds against instead of
the .NET base class library. `dotnet build` builds it and copies it beside
the compiler. The compiler loads that copy and lowers declarations and
method bodies into the existing bound tree. It defines `object`, `string`, `void`, `System.ValueType`, the numeric types,
`System.Array`, `System.Console`, `System.WeakReference<T>`, and
`System.IDisposable`. A call written `System.Console.WriteLine` binds to that
type, and so does `Console.WriteLine` after `using System`. `String.Concat` and
`Object.ToString` are public because Roslyn lowers `+` through them; a direct
call is not a supported library feature. `string` has no equality operator, so
`==` stays reference equality. `System.Enum` and `System.Attribute` exist
because the C# compiler cannot emit the assembly without them, so an enum or a
custom attribute binds here even though the language reference rejects both.
A `using` on a type that has `Dispose()` but does not implement `IDisposable`
is rejected. Method bodies in the assembly are never run.

Source layout:

| Path                    | Responsibility                                                                                                            |
|-------------------------|---------------------------------------------------------------------------------------------------------------------------|
| `src/Frontend/`         | Roslyn compilation, declaration and body lowering, ArcSharp diagnostic rules                                              |
| `src/Lexing/`           | `TokenKind` and `Diagnostic`, the kinds and messages the lowering and the emitter share                                   |
| `src/Syntax/`           | `LiteralKind` and `RefKind`                                                                                               |
| `src/Binding/`          | `TypeSymbol`, `MethodSymbol`, `FieldSymbol`, the bound tree, definite assignment, and cycle analysis                      |
| `src/CodeGen/`          | `Emitter` — bound tree → LLVM IR, with ARC ops woven in                                                                   |
| `src/Driver/`           | `Compilation` — orchestrates the stages and invokes `clang`, or `llc` and a linker                                        |
| `src/Program.cs`        | Command-line parsing                                                                                                      |
| `runtime/arc_runtime.c` | Object header, retain/release, weak table, type info, strings, arrays, console                                            |
| `ref/ArcSharp.References/` | Library surface Roslyn binds against. Built with the compiler and loaded from beside `arcsharp.dll` |
| `ref/ArcSharp.References.Check/` | Snippets compiled against that assembly alone. The harness runs them |
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
typedef struct ArcSlot {
    intptr_t  offset;             // byte offset from the object, or from the struct
    int32_t   kind;               // 1 = strong reference, 2 = inline struct
    int32_t   pad;
    TypeInfo* nested;             // struct TypeInfo when kind is 2
} ArcSlot;

typedef struct TypeInfo {
    const char* name;
    intptr_t    instanceSize;     // header + fields, in bytes
    void      (*deinit)(void*);   // releases all strong fields of an instance
    void**      vtable;           // virtual method slots (function pointers)
    intptr_t    vtableLen;
    void**      itable;           // interface method slots, indexed by selector
    intptr_t    itableLen;
    TypeInfo*   base;             // base class, or NULL; used by is/as
    ArcSlot*    slots;            // strong edges; weak fields are omitted
    intptr_t    slotCount;
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
that function when `strong` hits 0, and the field releases recurse. This is
the compiler's behaviour today. It is not a C# finaliser. `~T()` is rejected.

The [finalisation contract](memory-model.md#finalisation) changes the order
inside that function once `~T()` is accepted. The user finaliser runs first,
derived class before base, while children are still alive. Field release
happens afterwards, through a worklist rather than a recursive call. A
suppressed finaliser skips the user body and still releases fields.
`__deinit` remains the generated teardown. It gains the user finaliser in
front of the releases it already performs. `GC.SuppressFinalize` needs a flag
on the object. Where that flag sits in the header is chosen with the
implementation, and the emitter, the runtime, and this layout are updated
together. The header does not change before then.

> This "retain every read to +1, release temporaries per statement, release
> slots at scope exit" rule over-retains slightly versus the hybrid model in
> the [Roadmap](roadmap.md#m3--lifetime-pass). That is
> intentional until the lifetime pass exists: it is simple, local, and
> provably balanced. The pass will skip retain and release where it can prove
> a lifetime. It will not remove the header, and it will not introduce a
> second object layout. An escape still uses the count on the same object.

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
  methods. Each class and struct `TypeInfo` also lists its strong reference
  slots, including references nested inside an inline struct. Weak fields are
  not slots. Reference-typed static fields are emitted as a root list passed
  to `arc_set_roots` at the start of `main`.
- **Leak reports** are off unless the process environment contains
  `ARC_LEAKS=1`. The runtime then keeps a side table of allocations (not
  itself reference-counted) and, after the accounting line, prints each
  surviving object with its type, counts, source site, and whether a static
  root reaches it. Unreachable survivors are searched for strong cycles. The
  report does not release or reclaim anything. Allocation stacks are not
  available until debug info exists.
- **Structs** lower to an opaque `[HeapSize / 8 x i64]` value. Field `i` lives at
  slot `i`, so nested structs keep their full size. Every struct gets
  `<T>__copy` (value semantics: copy value fields, retain reference fields) and
  `<T>__drop` (release reference fields); a class `__deinit` calls `__drop` for
  each struct field. A struct local is zero-initialised and dropped at
  `func.exit`, and a struct temporary is dropped at the end of its statement.
- **`ref`, `out`, and `in`** lower to pointers to the slot. A reference-typed
  `ref` borrows the slot and does not transfer ownership. When the slot is
  inside a heap object, the caller retains that object until the call returns
  and then releases it. The same pin covers a struct method's `this`. Stack
  slots, static fields, and by-ref parameters borrowed from the caller are not
  pinned. Weak slots and writes through an `in` parameter are rejected before
  emission.
- **`is` and `as`** call `arc_is_instance`, which walks the `TypeInfo` base
  chain. An explicit cast `(T)x` is a `BoundConversion` with no runtime test,
  for classes and interfaces alike.
- **Array stores** into a reference-element array call
  `arc_array_store_check`, which prints `ArrayTypeMismatchException` and exits
  on a mismatch. Value-type arrays stay invariant.
- **Generics** are monomorphised during binding. Open declarations remain
  templates; closed types cache an independent substituted AST, layout, vtable,
  itable, and destructor. Closed methods are cached and their bodies enter a
  worklist, allowing recursive calls and types discovered while binding.
  LLVM identifiers use a reserved `$g$` prefix and hex-encoded closed names.
  Constraints are checked before emission; expansion limits report `ARC2013`
  instead of exhausting the compiler stack. ARC operations follow the concrete
  substituted types, including inline struct size and reference-field cleanup.
- **`System.WeakReference<T>`** remains a runtime intrinsic, including when
  used as a generic argument or local. It does not require template expansion.
- **`using`** owns the resource value in a scoped local (`arc_assign_take`),
  calls its `Dispose()` method at scope exit — guarded against `null`,
  dispatched through the vtable or itable when the method is `virtual` or the
  resource is interface-typed — and then releases the resource and nulls the
  slot, so the `func.exit` release of that local sees `null`. A `return` inside
  the body runs the same cleanup, innermost first for nested `using`
  statements, before branching to `func.exit`.
- **`Console.Write` and `WriteLine`** and **`.Length`** are bound directly to
  runtime functions; there is no library behind them. A `bool` passed to the
  runtime, including one concatenated into a string, is zero-extended from
  `i1` to `i32` so that it matches the C `int` parameter. A `char` is written
  as its UTF-8 character, and the same encoding is used when a `char` is
  concatenated into a string.
- **Compilation units** are merged: `using` directives are skipped, namespace
  names are discarded, and a trailing `?` on a type is ignored.
- **Field initialisers** are parsed and then discarded. This is a known defect,
  not a design choice.

---

## 7. Current limits

Two lists. The first is inherent in the memory model. The second is inside the
[compatibility target](roadmap.md#compatibility-target) and is simply not built.

### Fundamental tensions with ARC
- **Uncollected reference cycles.** Closed decision: warn with `ARC3100`,
  permit the program, and document the leak. A cycle the developer or a
  library leaves in place is never collected. `WeakReference<T>` breaks it.
  There is no backup collector.
- **Multithreaded sharing.** Correct ARC across threads requires *atomic*
  retain/release (or ownership transfer rules). ArcSharp's counts are
  non-atomic, so shared mutable references across threads are unsound. Atomics
  have workload-dependent costs. Safe shared-reference acquisition, weak
  promotion, synchronisation, and reclamation are also required; atomic counts
  alone cannot protect the interval between loading a pointer and retaining it.
- **CLR finalisation.** The CLR runs finalisers later, on another thread, and
  allows resurrection. ArcSharp's contract is the opposite, and it is
  documented in the [memory model](memory-model.md#finalisation): synchronous
  finalisation at the last strong release, no resurrection, no finaliser
  queue. `GC.Collect` and `GC.WaitForPendingFinalizers` become no-ops.
  `GC.ReRegisterForFinalize` is rejected. User finalisers are not accepted yet.
- **`async`/`await` and delegates.** Both are in the [compatibility target](roadmap.md#compatibility-target). A capture or a suspended local is an ARC-managed object. Strong captures stay strong. They wait until the lifetime pass and finalisers exist, because each one creates a lifetime the current emitter does not have.
- **Closures / lambda capture.** Captured variables become heap closure objects. Capturing `this` can form a cycle, which `ARC3100` warns about and which remains legal. Closures are the lowering for delegates, and they wait with the rest of that target.

- **Interior references.** Calls pin the heap owner of a managed reference for
  the duration of the call. Ref locals, ref returns, and heap-backed spans
  remain rejected: their lifetime is longer than one call, and stack
  confinement does not keep a heap object alive.

### In the compatibility target, not built yet
The [compatibility target](roadmap.md#compatibility-target) is the bound. These items are inside it and absent from the compiler today.

- **Remaining generic features.** Virtual generic methods, interface generic methods, variance, full overload inference, and further constraint forms. Closed types stay specialised.
- **Reflection, `dynamic`, and `unsafe`.** Limited, as that table says. Reflection is emitted metadata. `dynamic` binds only to members this compiler can lower. A pointer must not outlive the proved lifetime of the object it addresses.
- **Exceptions** (`try`/`catch`/`finally`). Every unwinding path must release live locals. The cleanup model anticipates that, and the emitter does not yet emit landing pads.
- **Delegates, events, LINQ, and `Span<T>`.** Specified in the target table. None of them is accepted yet.
- **Struct forms still rejected.** Arrays of structs, struct return values, static struct fields, by-value struct parameters, properties of struct type, printing a struct, `virtual`/`override` members on structs, interfaces on structs, `using` a struct resource, and self-containing structs are `ARC3001`. They belong to the full struct row.
- **Enums, indexers, and the rest of ordinary C#.** Enums, indexers, pattern matching, records, operator overloading, partial classes, extension methods, `params`, optional and named arguments, bitwise operators and shifts, `decimal`, and nullable value types are absent. They belong to the ordinary C# milestone, [M6](roadmap.md#m6--ordinary-c). A single `&` or `|` is a lexer error (`ARC0003`) today.
- **Name handling.** A trailing `?` is accepted and ignored. A namespace is part of the type's identity.

### Not part of the target
- The rest of the .NET base class library beyond `Console`, the core library in the target, and `WeakReference<T>`.
- Threads and cross-thread sharing.
- CLR garbage-collector semantics: a tracing collector, a finaliser thread, and resurrection.
- Loading code at run time, and consuming CIL or precompiled assemblies.
- 32-bit, ARM, and big-endian targets.

---

## 8. Implementation status (first pass)

| Area                                                   | Status                                |
|--------------------------------------------------------|---------------------------------------|
| Roslyn frontend                                        | Implemented                           |
| Binder: classes, inheritance, fields, methods, statics | Implemented                           |
| Type checking (core)                                   | Implemented (lightweight)             |
| Virtual / override dispatch (vtables)                  | Implemented                           |
| ARC: retain/release/deinit, statement & scope cleanup  | Implemented                           |
| `WeakReference<T>` fields, locals, and generic arguments | Implemented |
| Arrays (value & reference element types)               | Implemented                           |
| Strings + `Console`                                    | Implemented                           |
| Control flow (`if`/`while`/`for`/`return`)             | Implemented                           |
| `float` / `double`                                     | Implemented                           |
| `byte` / `sbyte` / `short` / `ushort` / `uint` / `ulong` | Implemented                         |
| `ref` / `out` / `in` parameters                        | Implemented                           |
| Interior-reference owner pins                          | Implemented for calls; longer lifetimes rejected |
| Auto-properties                                        | Implemented for instance and static properties with both accessors |
| `is` / `as`                                            | Implemented                           |
| Array covariance (runtime store check)                 | Implemented                           |
| Structs (value types)                                  | Implemented with the restrictions in Section 7 |
| `using` / `IDisposable` (deterministic `Dispose`)      | Implemented                           |
| Interfaces (itable dispatch)                           | Implemented and verified              |
| Diagnostics with codes (`ARC0001`…) and severities     | Implemented                           |
| `ARC3001` unsupported-construct diagnostics            | Implemented (compiler and analyser)   |
| `ARC3100` possible strong-cycle warning                 | Implemented (compiler and analyser)   |
| Roslyn analyser + unit tests (`analyzers/`)            | Implemented (68 xUnit cases)          |
| Multi-file compilation (merged type declarations)      | Implemented                           |
| Definite-assignment analysis (conservative, warns)     | Implemented                           |
| Field initialisers                                     | Parsed and discarded (defect)         |
| Generics | Monomorphisation, basic inference, and constraints; remaining limits in Section 7 |
| Leak report (`ARC_LEAKS=1`) | Implemented: survivor list, static roots, unreachable cycles |
| Async, closures, exceptions | Not implemented (Section 7 / [Roadmap](roadmap.md)) |
| Roslyn frontend | Landed. Roslyn parses and binds, and the lowering builds the bound tree. |
| Lifetime pass | Not started. The emitter still retains every read. See the [Roadmap](roadmap.md). |

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
What it does **not** provide is 32-bit, ARM, or big-endian targets. Those would
need the triple and data layout parameterised, ABI lowering per target, and the
hard-coded 8-byte slot removed. They are outside the compatibility target and
are listed under [Not scheduled](roadmap.md#not-scheduled). The Linux x64 path
is scheduled for verification in [M12](roadmap.md#m12--production-backend).

---

## 10. Decisions and remaining questions

The memory-model and frontend choices are recorded in the
[Roadmap](roadmap.md#decisions). In short: Roslyn becomes the frontend before
any new language feature; every heap object keeps its header; a proved lifetime
skips retain and release; every other reference uses ARC; there is no backup
cycle collector. Finalisation runs on the releasing thread when the strong
count reaches zero, before children are released, and it is not tied to
lexical scope. `Dispose()` stays separate. Resurrection is rejected. The
current compiler still rejects `~T()`.

Still open, and not required to start the Roslyn transition:

1. **Threading.** The runtime stays single-threaded. Threads are outside the
   compatibility target. They would need a verified concurrent ownership
   protocol, including atomic counts and safe shared-reference acquisition,
   and the roadmap does not schedule that work.
2. **Library surface.** The libraries to build are the ones named in the
   compatibility target: collections, LINQ, and the small set of types that
   table already lists. The rest of the .NET BCL is not a target. Each API
   still needs a lifetime test, and that work waits until the lifetime pass
   exists.
3. **`unowned`.** `WeakReference<T>` is the weak surface. A non-zeroing
   `unowned` reference is not decided. The legacy `weak` field modifier stays
   a compatibility alias only.
4. **Exceptions.** They are a full row of the compatibility target. Landing
   pads have to release proved and shared values, and run `using` cleanup and
   finalisers. They come after deterministic finalisers, not before.
5. **How precise the first lifetime pass is.** The fallback is ARC, so a weak
   first proof is safe. Which escapes the first version must prove (repeated
   local reads, a call that only borrows, a local-to-local move) and which ones
   fall back immediately is decided at the start of
   [M3](roadmap.md#m3--lifetime-pass).
6. **String representation.** Strings are UTF-8 today, and `Length` counts
   bytes. Full compatibility needs UTF-16 code units. This is decided at the
   start of [M8](roadmap.md#m8--core-library-iterators-and-linq).
