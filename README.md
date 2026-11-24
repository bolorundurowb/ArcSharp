<p align="center">
  <img src="docs/assets/logo.svg" alt="ArcSharp" width="420">
</p>

<p align="center">
  <strong>Native C# without a garbage collector.</strong><br>
  A proof-of-concept compiler from C# 12 to LLVM IR, with automatic reference counting.
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-AGPL_v3-blue.svg" alt="Licence: AGPL v3"></a>
  <img src="https://img.shields.io/badge/status-proof_of_concept-orange.svg" alt="Status: proof of concept">
  <img src="https://img.shields.io/badge/target-Windows_x64-512BD4.svg" alt="Target: Windows x64">
</p>

---

ArcSharp compiles a subset of C# 12 to a native Windows x64 executable. It is
a separate runtime with its own memory contract, not a garbage collector
dropped into the CLR. In the compiler that exists today, every object carries
a reference count and is destroyed when its last strong reference goes away.
There are no collection pauses and no background threads.

The source you write is ordinary C#. It also builds with `dotnet build`, so you
can compare the two runtimes side by side.

## Why ARC instead of a garbage collector

Most C# applications thrive on a tracing garbage collector. ArcSharp explores
the niches where one is undesirable: latency-sensitive work such as audio,
games, and real-time control, and environments with tight memory budgets.

|                     | Tracing GC (.NET)            | ARC (ArcSharp)                            |
|---------------------|------------------------------|-------------------------------------------|
| **Reclamation**     | Deferred, at collection time | Immediate, at last release                |
| **Pauses**          | Stop-the-world phases        | None (work is spread inline)              |
| **Per-object cost** | Amortised                    | A counter field + retain/release traffic  |
| **Cycles**          | Collected automatically      | Warned (`ARC3100`), still permitted. An ignored warning leaks |
| **Finalisation**    | Later, on a finaliser thread, and may resurrect | At the last strong release, on that thread. No resurrection |

## Quick start

You need Windows x64, the [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0),
[LLVM](https://github.com/llvm/llvm-project/releases) `clang`, and the
Microsoft C++ build tools. [Getting started](docs/getting-started.md) has the
details.

```powershell
dotnet build -c Release

dotnet bin/Release/net11.0/arcsharp.dll samples/inherit.cs -o $env:TEMP\inherit `
    --clang "C:/Program Files/LLVM/bin/clang.exe" `
    --runtime runtime/arc_runtime.c --run
```

```text
25
sum=42
[arc] alloc=5 dead=5 freed=5 live=0
```

The last line is the runtime's accounting: `live=0` means every object was
reclaimed.

## What works

- Classes with single inheritance, transitive base interfaces, and
  `virtual`/`override` dispatch.
- Generic classes, structs, interfaces, and methods through monomorphisation,
  with Roslyn type inference, generic constraints, interface variance,
  virtual and interface generic methods, and static abstract members.
- Constructors, instance and static methods and fields, and instance
  properties, indexers, constants, readonly fields, and expression-bodied methods
  and constructors. Calls support named arguments, optional defaults, and
  parameter arrays. Static constructors run lazily on first use.
- Structs with value semantics, including reference fields, nested structs,
  structs inside classes, struct arrays, static fields, properties, parameters,
  and return values. Structs can implement interfaces and override their
  printed representation.
- `int`, `long`, `uint`, `ulong`, `float`, `double`, `decimal`, `bool`, `char`, and
  `string`, enums, nullable values (with updates, user-defined operators and
  struct boxing), and single-dimensional arrays.
- Record classes with value equality, `with` and deconstruction, and object
  initialisers on classes.
- `ref`, `out`, and `in` parameters. A reference into a heap object is kept
  alive for the call. Also `is` and `as`.
- `if`, `while`, `do`/`while`, `for`, `break`, `continue`, `return`, constant-label
  and pattern `switch`, and array `foreach`.
- Bitwise operators, shifts, prefix and postfix updates, compound assignments,
  and checked numeric arithmetic.
- `using` statements with deterministic `Dispose()` at scope exit, including
  when an exception unwinds
  (`IDisposable` is structural: any type with a `Dispose()` method qualifies).
- `try`, `catch`, `finally`, and `throw`, with a small `System` exception
  library. Every unwind path releases the references it owns, and the runtime
  checks (null reference, index, cast, division) raise catchable exceptions.
- Synchronous lambdas with captured variables, and delegates over method groups and lambdas, single and multicast, with `+`, `-`, `==`, `?.Invoke`, and `System.Action`, `Func` and `Predicate<T>`.
- Cycle breaking with the standard `System.WeakReference<T>`.
- Finalisers (`~T()`) that run at the last strong release, derived class
  before base, with `GC.SuppressFinalize`. Resurrection is rejected.
- Several source files compiled into one program.
- Diagnostics with stable codes, a definite-assignment warning, `ARC3001` for
  unsupported constructs, `ARC3100` for possible strong cycles, and `ARC3200`
  for `GC` calls that do nothing.
- A Roslyn analyser in `analyzers/ArcSharp.Analyzers` that reports the same
  `ARC3001`, `ARC3100` and `ARC3200` rules inside an ordinary `dotnet build`.

Exception filters, events, and the .NET class
library are not supported yet. The [Language reference](docs/language-reference.md)
has the full list, the differences from .NET, and the known issues. The
[compatibility target](docs/roadmap.md#compatibility-target) is the language
and library surface the project is aiming at: classes through `Span<T>` as
full, and reflection, `dynamic`, and `unsafe` as limited. It is not a plan to
implement the whole framework.

## GC comparison (23 November 2026)

The five-run comparison completed on Windows x64 with .NET SDK 11.0.100
and clang 22.1.8. All checksums matched and every ArcSharp run reported
`live=0`.

| Workload | Runtime | Wall time | Peak working set | Peak private commit |
|---|---|---:|---:|---:|
| baseline | ArcSharp | 19.1 ms | 3.9 MiB | 0.6 MiB |
| baseline | .NET | 59.0 ms | 23.1 MiB | 6.3 MiB |
| retained | ArcSharp | 62.9 ms | 25.1 MiB | 23.4 MiB |
| retained | .NET | 74.3 ms | 35.4 MiB | 18.7 MiB |
| churn | ArcSharp | 149.0 ms | 35.8 MiB | 34.8 MiB |
| churn | .NET | 107.4 ms | 61.1 MiB | 44.2 MiB |

The churn sampler observed both .NET pauses in three of the five runs, and the
median of the longest pause in each run was 16.153 ms. The ArcSharp churn
workload recorded 9,200,856 retains and 10,601,216 releases per run.

[Benchmark method and previous results](docs/features/gc-comparison.md).

## Status

ArcSharp is a proof of concept. The full pipeline (C# to LLVM IR to a native
executable, with ARC accounting) works, and 86 sample programs pass the test
harness on Windows x64. The harness also runs four leak-report checks, 58
must-fail diagnostics, two run-time abort checks, three command-line checks,
cycle and `ARC3200` checks, and 84 analyser unit tests. The
compiler can also emit IR for Linux x64, but that path is not tested. See
[Verification](docs/verification.md) for the latest results.

The M2 verification baseline checks exact ARC counts and destruction timing.
Every heap object has a reference count. Roslyn parses and binds the source,
and the lifetime pass proves selected borrows, moves and confined allocation
lifetimes. [Deterministic finalisation](docs/features/finalisers.md) has
landed. Its contract is in the
[memory model](docs/memory-model.md#finalisation): `~T()` runs when the
last strong reference is released, not when a variable leaves scope, and
`Dispose()` stays a separate call. ArcSharp is a separate runtime with that
profile. It is not a collector dropped into the CLR.
[Exceptions](docs/features/exceptions.md) have landed too: an unwind releases
what it owns, runs `finally` and `Dispose()`, and finalises objects that die on
the way.

## Documentation

| Guide | |
|---|---|
| [Getting started](docs/getting-started.md) | Install, build, and run your first program |
| [Language reference](docs/language-reference.md) | The supported subset, differences from .NET, and known issues |
| [Memory model](docs/memory-model.md) | When objects die, why cycles leak, and `WeakReference<T>` |
| [Command-line reference](docs/cli-reference.md) | Options, outputs, and exit codes |
| [Diagnostics](docs/diagnostics.md) | Every error and warning code |
| [Troubleshooting](docs/troubleshooting.md) | Build failures, crashes, and leaks |
| [Architecture](docs/architecture.md) | Compiler internals, object layout, and the ARC rules |
| [Roadmap](docs/roadmap.md) | Thirteen milestones, in order: the Roslyn frontend, a verification baseline, the lifetime pass, finalisers and exceptions (both landed), and then the rest of the compatibility target |

Contributors should read [`AGENTS.md`](AGENTS.md), which lists the invariants
the compiler and runtime depend on.

## Repository layout

| Path | Contents |
|---|---|
| `src/` | The compiler: lexing, syntax, binding, code generation, and the driver |
| `runtime/` | `arc_runtime.c`, the object model and ARC primitives |
| `samples/` | Test programs, each run by the harness |
| `analyzers/` | The Roslyn analyser, the shared `ARC3xxx` catalogue, and their xUnit tests |
| `ref/` | The reference assembly the Roslyn frontend will bind against, and the check that guards its surface |
| `tools/` | Build and verification scripts |
| `docs/` | Documentation and the logo |

## About this project

ArcSharp is an AI-led personal exploration of alternative C# runtime models
beyond Native AOT, driven by curiosity about which compilers and memory
management strategies the ecosystem could support.

## Licence

ArcSharp is licensed under the [GNU Affero General Public License v3.0](LICENSE).
