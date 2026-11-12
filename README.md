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

ArcSharp compiles a subset of C# 12 to a native Windows x64 executable. Instead
of a tracing garbage collector, every object carries a reference count, and it
is destroyed at the moment its last strong reference goes away. There are no
collection pauses and no background threads, and memory is returned as soon as
it is no longer used.

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
| **Cycles**          | Collected automatically      | Leak unless broken by `WeakReference<T>`  |
| **Determinism**     | Nondeterministic finalisers  | Deterministic destruction                 |

## Quick start

You need Windows x64, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
[LLVM](https://github.com/llvm/llvm-project/releases) `clang`, and the
Microsoft C++ build tools. [Getting started](docs/getting-started.md) has the
details.

```powershell
dotnet build -c Release

dotnet bin/Release/net8.0/arcsharp.dll samples/inherit.cs -o $env:TEMP\inherit `
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

- Classes with single inheritance, interfaces, and `virtual`/`override` dispatch.
- Constructors, instance and static methods and fields, and instance
  auto-properties.
- Structs with value semantics, including reference fields, nested structs,
  and structs inside classes.
- `int`, `long`, `uint`, `ulong`, `float`, `double`, `bool`, `char`, and
  `string`, plus single-dimensional arrays.
- `ref`, `out`, and `in` parameters, and `is` and `as`.
- `if`, `while`, `for`, and `return`.
- `using` statements with deterministic `Dispose()` at scope exit
  (`IDisposable` is structural: any type with a `Dispose()` method qualifies).
- Cycle breaking with the standard `System.WeakReference<T>`.
- Several source files compiled into one program.
- Diagnostics with stable codes, a definite-assignment warning, `ARC3001` for
  unsupported constructs, and `ARC3100` for possible strong cycles.
- A Roslyn analyser in `analyzers/ArcSharp.Analyzers` that reports the same
  `ARC3001` and `ARC3100` rules inside an ordinary `dotnet build`.

Generics, exceptions, `foreach`, `switch`, `++`, lambdas, and the .NET class
library are not supported yet. The [Language reference](docs/language-reference.md)
has the full list, the differences from .NET, and the known issues.

## Status

ArcSharp is a proof of concept. The full pipeline (C# to LLVM IR to a native
executable, with ARC accounting) works, and 23 sample programs pass the test
harness on Windows x64. The harness also runs 14 must-fail diagnostics, three
command-line checks, four cycle checks, and 62 analyser unit tests. The
compiler can also emit IR for Linux x64, but that path is not tested. See
[Verification](docs/verification.md) for the latest results and
[Roadmap](docs/roadmap.md) for the plan.

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
| [Roadmap](docs/roadmap.md) | The plan towards a usable toolchain |

Contributors should read [`AGENTS.md`](AGENTS.md), which lists the invariants
the compiler and runtime depend on.

## Repository layout

| Path | Contents |
|---|---|
| `src/` | The compiler: lexing, syntax, binding, code generation, and the driver |
| `runtime/` | `arc_runtime.c`, the object model and ARC primitives |
| `samples/` | Test programs, each run by the harness |
| `analyzers/` | The Roslyn analyser, the shared `ARC3xxx` catalogue, and their xUnit tests |
| `tools/` | Build and verification scripts |
| `docs/` | Documentation and the logo |

## About this project

ArcSharp is an AI-led personal exploration of alternative C# runtime models
beyond Native AOT, driven by curiosity about which compilers and memory
management strategies the ecosystem could support.

## Licence

ArcSharp is licensed under the [GNU Affero General Public License v3.0](LICENSE).
