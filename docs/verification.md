# ArcSharp — Verification Results

Reproduce with: `powershell -ExecutionPolicy Bypass -File tools\verify.ps1`.
It requires the .NET 8 SDK and LLVM `clang`, by default at
`C:/Program Files/LLVM/bin/clang.exe`; `-Clang <path>` overrides the location.
See [Getting started](getting-started.md) for the prerequisites.

The harness builds the compiler with the .NET 8 SDK selected by `global.json`,
compiles each sample in `samples/` to LLVM IR, uses `clang` to lower the IR and
link it with `runtime/arc_runtime.c` into a native Windows x64 `.exe`
(`x86_64-pc-windows-msvc`), runs it, and checks both program output **and** ARC
accounting. It also compiles the must-fail samples, checks the `ARC3100` cycle
warnings, and runs the analyser unit tests. The compiler can also emit the Linux
x64 triple (`x86_64-pc-linux-gnu`); the table below is a Windows x64 run. On
Linux only the triple, data layout, and the C runtime build differ.

Each run prints `[arc] alloc=A dead=D freed=F live=L` to stderr:
`alloc` = objects created, `dead` = objects whose strong count hit 0 (destructor
ran), `freed` = allocations returned to the OS, `live` = `alloc − dead` (objects
still retained at exit). **`live=0` means no leaks.**

## Results (23 samples, Windows x64, 12 November 2026)

`tools\verify.ps1` reported **45 passed, 0 failed** with the .NET 8.0.428 SDK:
23 sample programs, 14 must-fail diagnostics, 3 command-line checks, 4 cycle
checks, and one run of the analyser unit tests (62 cases). `live=0` means every
object was reclaimed.
`numerics` and `byref` allocate nothing because they use only value types.
`cycle_strong` and `cycle_struct` are the leaks the harness asserts on purpose.

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
| `cycle_struct` | a **strong** cycle that runs through an inline struct field | alloc=3 dead=1 **live=2** | `done (struct cycle)` |
| `multifile` | `mf_main.cs` and `mf_lib.cs` compiled together; `Greeter` is declared in one file and used from the other | alloc=4 dead=4 **live=0** | `twice=42` |
| `numerics` | `byte`/`sbyte`/`short`/`ushort`/`uint`/`ulong`, unsigned arithmetic, casts | alloc=0 dead=0 **live=0** | `150` |
| `byref` | `ref`, `out`, and `in` parameters | alloc=0 dead=0 **live=0** | `3` |
| `properties` | auto-properties | alloc=2 dead=2 **live=0** | `Alice` |
| `isas` | `is` and `as` runtime type checks | alloc=5 dead=5 **live=0** | `as Cat: null` |
| `covariance` | covariant store of a compatible reference element | alloc=3 dead=3 **live=0** | `stored Dog` |
| `longconcat` | `long` and `uint` values converted to strings by concatenation; regression test for the 32-bit `long` truncation on Windows | alloc=7 dead=7 **live=0** | `concat=20000000000 uint=3000000000` |
| `using_stmt` | `using`/`IDisposable`: declaration and expression forms, virtual and itable dispatch of `Dispose`, a `null` resource (cleanup skipped), and cleanup on the `return` path | alloc=25 dead=25 **live=0** | `dispose a`, `tmp delete`, `db closed`, `disposals=3` |
| `structs` | struct code generation: value semantics, reference fields, `ref`/`out`, structs inside classes, nested structs, and a temporary struct method call | alloc=66 dead=66 **live=0** | `a.sum=5`, `card=card copy tags=1,2`, `temp=42` |
| `boolprint` | `Console.WriteLine` of a false comparison, and concatenation of `bool` values | alloc=9 dead=9 **live=0** | `cmp=False/True direct=False` |
| `unicode` | `\u` and `\U` escapes in a string and a character literal | alloc=9 dead=9 **live=0** | `A`, `letter=A match=True bytes=4` |

## Diagnostics

Diagnostics print with a stable code and a severity, for example
`(12,0): error ARC2010: cannot convert 'int' to 'string'`. Compilation fails
when the error count is non-zero. Warnings, including definite-assignment
`ARC2100` and cycle `ARC3100`, do not fail the build unless `-Werror` is
passed, so the samples above still pass.

The harness also compiles 14 programs that must fail (`samples/negative/`) and
checks the expected code: `ARC3001` for unsupported constructs, `ARC0003` for
shift operators, `ARC0004` for an invalid Unicode escape, and `ARC2011` for an
invalid `using` resource. It checks that `-o` with no value, and a missing
`clang` or `llc`, are reported as errors and exit with code 2. It checks
`ARC3100` in both directions: `cycle_strong` and `cycle_struct` warn, while
`cycle_weak` and `cycle_weakref` stay clean.

## Analyser unit tests

`analyzers/ArcSharp.Analyzers.Tests` is an xUnit project that compiles snippets
in memory with Microsoft.CodeAnalysis and runs the analyser. It has 62 cases:
41 unsupported constructs that must report `ARC3001`, 12 supported programs
that must stay clean, and 9 cycle checks for `ARC3100` (mutual and self
references, arrays, statics, struct bridges, and weak references that break the
cycle). `tools/verify.ps1` runs `dotnet test` and counts the whole run as one
check.

## The headline result: ARC + cycle breaking

`cycle_strong` and `cycle_weak` are identical except for one keyword on the
back-reference field:

```
cycle_strong:  public Parent owner;        ->  alloc=3 dead=1 live=2   (LEAK)
cycle_weak:    public weak Parent owner;   ->  alloc=3 dead=3 live=0   (reclaimed)
```

This is exactly the fundamental ARC trade-off made concrete: pure reference
counting cannot reclaim a strong cycle (two objects leak), and the `weak`
modifier breaks the cycle so deterministic reclamation succeeds.
`cycle_weakref` shows the same result with standard `WeakReference<T>`
(alloc=4 dead=4 live=0); its extra allocation is the `WeakReference<T>` object.
`cycle_struct` shows that a cycle through an inline struct field leaks in the
same way, and that `ARC3100` sees the class reference inside the struct. The
leaks in `cycle_strong` and `cycle_struct` are **expected and asserted** by the
harness — they document the limitation rather than hiding it.

## Roslyn compatibility (idiomatic C#)

Because cycle-breaking uses the standard `System.WeakReference<T>` type rather
than a custom keyword, ArcSharp source is also valid C# that the normal toolchain
accepts. This was checked on 10 November 2026 with the .NET 8.0.427 SDK: each
sample was copied into a new `net8.0` console project as `Program.cs` and run
with `dotnet run`.

| Sample | Roslyn build | Output under .NET | Output under ArcSharp |
|---|---|---|---|
| `cycle_weakref` | Succeeded | `done (WeakReference cycle)` | `done (WeakReference cycle)`, `live=0` |
| `weakref_get` | Succeeded | `target v=99` | `TryGetTarget after death: false (PASS under ARC)` |
| `interface` | Succeeded | `rect=12`, `circle=75` | `rect=12`, `circle=75` |
| `inherit` | Failed (`CS1002`) | | |

`weakref_get` shows the intended behavioural difference. It calls
`TryGetTarget` right after the target's last strong reference is dropped.
ArcSharp returns `false` immediately, because destruction is deterministic.
Under .NET the target is still in memory because no collection has run, so the
call returns `true`. That divergence is the point of the project.

`inherit` fails under Roslyn because it uses the legacy `weak` field modifier,
which is not C#. `cycle_weak` and `weak_null` use it too. Those three samples
are kept to test the legacy alias; new samples use `WeakReference<T>`.

## Windows x64 target check

Every module the compiler writes with `--target windows` (the default) starts
with:

```
target triple = "x86_64-pc-windows-msvc"
```

`clang` replaces it with the exact MSVC version it finds, which is the source
of the expected `-Woverride-module` warning. The harness builds every sample
through that path, so each passing row in the results table is also a
successful Windows x64 link and run.
