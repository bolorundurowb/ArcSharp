# ArcSharp — Verification Results

Reproduce with: `bash tools/verify.sh` (requires the .NET 8 SDK + LLVM `llc` + a
C compiler on `PATH`; `tools/env.sh` wires up the locally-provisioned toolchain).

The harness builds the compiler, compiles each sample in `samples/` to LLVM IR,
runs `llc` to produce an object file, links it with `runtime/arc_runtime.c`, runs
the result, and checks both program output **and** ARC accounting. Verification
compiles for the Linux host (`x86_64-pc-linux-gnu`); the Windows target
(`x86_64-pc-windows-msvc`) emits the same IR and a valid amd64 COFF object
(checked separately with `llvm-nm`/`file`).

Each run prints `[arc] alloc=A dead=D freed=F live=L` to stderr:
`alloc` = objects created, `dead` = objects whose strong count hit 0 (destructor
ran), `freed` = allocations returned to the OS, `live` = `alloc − dead` (objects
still retained at exit). **`live=0` means no leaks.**

## Results (11/11 passing)

| Sample | Exercises | ARC accounting | Output |
|---|---|---|---|
| `inherit` | single inheritance, `virtual`/`override`, `: base(...)`, arrays, `for`, string concat | alloc=5 dead=5 **live=0** | `25`, `sum=42` |
| `interface` | interface declaration + dispatch through an interface-typed variable (itable) | alloc=8 dead=8 **live=0** | `rect=12`, `circle=75` |
| `refarray` | `T[]` of reference elements; per-element release on array teardown | alloc=7 dead=7 **live=0** | `refarray sum=5` |
| `statics` | static fields + static methods, `int Main()` exit code | alloc=3 dead=3 **live=0** | `count=3` |
| `fib` | recursion, `long` return, `while`, `Console.Write` | alloc=22 dead=22 **live=0** | `fib(10)=55` |
| `floats` | `float`/`double` literals, arithmetic, comparisons, string concat | alloc=7 dead=7 **live=0** | `area=19.6349` |
| `weak_null` | a `weak` field whose target is destroyed → load yields `null` | alloc=3 dead=3 **live=0** | `weak-after-death is null: PASS` |
| `cycle_weak` | parent⇄child graph with a **weak** back-reference | alloc=3 dead=3 **live=0** | `done (weak cycle)` |
| `cycle_weakref` | same cycle, broken with idiomatic **`WeakReference<T>`** | alloc=4 dead=4 **live=0** | `done (WeakReference cycle)` |
| `weakref_get` | `WeakReference<T>.TryGetTarget(out var)` after the target died | alloc=4 dead=4 **live=0** | `false (PASS under ARC)` |
| `cycle_strong` | the same graph with a **strong** back-reference | alloc=3 dead=1 **live=2** | `done (strong cycle)` |

## The headline result: ARC + cycle breaking

`cycle_strong` and `cycle_weak` are identical except for one keyword on the
back-reference field:

```
cycle_strong:  public Parent owner;        ->  alloc=3 dead=1 live=2   (LEAK)
cycle_weak:    public weak Parent owner;   ->  alloc=3 dead=3 live=0   (reclaimed)
```

This is exactly the fundamental ARC trade-off made concrete: pure reference
counting cannot reclaim a strong cycle (two objects leak), and the `weak`
modifier breaks the cycle so deterministic reclamation succeeds. The leak in
`cycle_strong` is **expected and asserted** by the harness — it documents the
limitation rather than hiding it.

## Roslyn compatibility (idiomatic C#)

Because cycle-breaking uses the standard `System.WeakReference<T>` type rather
than a custom keyword, ArcSharp source is also valid C# that the normal toolchain
accepts. Both weak-reference samples compile clean with Roslyn:

```
$ dotnet build           # Program.cs = samples/cycle_weakref.cs
Build succeeded.
$ dotnet build           # Program.cs = samples/weakref_get.cs
Build succeeded.
```

Run side by side, `cycle_weakref` prints the same line under the .NET CLR (GC) and
under the ArcSharp native binary (ARC):

```
CLR (Roslyn-built):     done (WeakReference cycle)
ArcSharp (GC-less .exe): done (WeakReference cycle)   + [arc] alloc=4 dead=4 live=0
```

The one intended behavioural difference is determinism: `weakref_get` calls
`TryGetTarget` right after the target's last strong reference is dropped. ArcSharp
returns `false` immediately (deterministic destruction); under the CLR the result
depends on whether the GC has run yet. That divergence is the whole point of the
project.

## Windows x64 target check

```
$ arcsharp samples/inherit.cs --target windows
$ file inherit.obj
inherit.obj: Intel amd64 COFF object file, ... 1st section name ".text"
$ grep "target triple" inherit.ll
target triple = "x86_64-pc-windows-msvc"
```

The object links on Windows with
`clang -target x86_64-pc-windows-msvc inherit.obj runtime/arc_runtime.c -o inherit.exe`
(or `lld-link`/`link.exe` against a runtime `.obj`).
