# ArcSharp — Verification Results

Reproduce with: `powershell -ExecutionPolicy Bypass -File tools\verify.ps1`
(requires the .NET 8 SDK + LLVM `clang` on `PATH`; `tools\env.ps1` sets the build
environment, and `-Clang <path>` overrides the clang location).

The harness builds the compiler with the .NET 8 SDK selected by `global.json`,
compiles each sample in `samples/` to LLVM IR, uses `clang` to lower the IR and
link it with `runtime/arc_runtime.c` into a native Windows x64 `.exe`
(`x86_64-pc-windows-msvc`), runs it, and checks both program output **and** ARC
accounting. The compiler can also emit the Linux x64 triple
(`x86_64-pc-linux-gnu`); the table below is a Windows x64 run. On Linux only the
triple, data layout, and the C runtime build differ.

Each run prints `[arc] alloc=A dead=D freed=F live=L` to stderr:
`alloc` = objects created, `dead` = objects whose strong count hit 0 (destructor
ran), `freed` = allocations returned to the OS, `live` = `alloc − dead` (objects
still retained at exit). **`live=0` means no leaks.**

## Results (17 samples, Windows x64, 9 November 2026)

`tools\verify.ps1` reported **17 passed, 0 failed** with the .NET 8.0.427 SDK.
`live=0` means every object was reclaimed. `numerics` and `byref` allocate
nothing because they use only value types. `cycle_strong` is the leak the
harness asserts on purpose.

| Sample | Exercises | ARC accounting | Output |
|---|---|---|---|
| `inherit` | single inheritance, `virtual`/`override`, `: base(...)`, arrays, `for`, string concat | alloc=5 dead=5 **live=0** | `25`, `sum=42` |
| `interface` | interface declaration and dispatch through an interface-typed variable (itable) | alloc=8 dead=8 **live=0** | `rect=12`, `circle=75` |
| `refarray` | `T[]` of reference elements; per-element release on array teardown | alloc=7 dead=7 **live=0** | `refarray sum=5` |
| `statics` | static fields and static methods, `int Main()` exit code | alloc=3 dead=3 **live=0** | `count=3` |
| `fib` | recursion, `long` return, `while`, `Console.Write` | alloc=22 dead=22 **live=0** | `fib(10)=55` |
| `floats` | `float`/`double` literals, arithmetic, comparisons, string concat | alloc=7 dead=7 **live=0** | `area=19.6349` |
| `weak_null` | a `weak` field whose target is destroyed, so a load yields `null` | alloc=3 dead=3 **live=0** | `weak-after-death is null: PASS` |
| `cycle_weak` | parent/child graph with a **weak** back-reference | alloc=3 dead=3 **live=0** | `done (weak cycle)` |
| `cycle_weakref` | the same cycle, broken with idiomatic **`WeakReference<T>`** | alloc=4 dead=4 **live=0** | `done (WeakReference cycle)` |
| `weakref_get` | `WeakReference<T>.TryGetTarget(out var)` after the target died | alloc=4 dead=4 **live=0** | `false (PASS under ARC)` |
| `cycle_strong` | the same graph with a **strong** back-reference | alloc=3 dead=1 **live=2** | `done (strong cycle)` |
| `multifile` | `mf_main.cs` and `mf_lib.cs` compiled together; `Greeter` is declared in one file and used from the other | alloc=4 dead=4 **live=0** | `twice=42` |
| `numerics` | `byte`/`sbyte`/`short`/`ushort`/`uint`/`ulong`, unsigned arithmetic, casts | alloc=0 dead=0 **live=0** | `150` |
| `byref` | `ref`, `out`, and `in` parameters | alloc=0 dead=0 **live=0** | `3` |
| `properties` | auto-properties | alloc=2 dead=2 **live=0** | `Alice` |
| `isas` | `is` and `as` runtime type checks | alloc=5 dead=5 **live=0** | `as Cat: null` |
| `covariance` | covariant store of a compatible reference element | alloc=3 dead=3 **live=0** | `stored Dog` |

## Diagnostics

Diagnostics print with a stable code and a severity, for example
`(12,0): error ARC2010: cannot convert 'int' to 'string'`. Compilation fails
when the error count is non-zero. Warnings, including definite-assignment
`ARC2100`, do not fail the build unless `-Werror` is passed, so the samples
above still pass.

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
