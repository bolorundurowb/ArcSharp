# ArcSharp

[![Licence: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](LICENSE)

A proof-of-concept compiler for a subset of **C# 12** that uses **ARC (Automatic Reference Counting)** instead of a garbage collector, emits **LLVM IR**, and produces native, GC-less executables.

> **AI-led project** — this is a personal exploration of alternative C# runtime models beyond Native AOT, driven by curiosity about what compilers and memory management strategies the ecosystem could support.

## Table of Contents
- [Rationale](#why-arcsharp--the-rationale)
- [Key Features](#key-features)
- [Project Status](#project-status)
- [Getting Started](#getting-started)
- [Project Structure](#project-structure)
- [Documentation](#documentation)
- [Portability](#portability)

## Why ArcSharp — the rationale
Most C# applications thrive on a tracing garbage collector. ArcSharp targets the niches where a GC is undesirable: latency-sensitive workloads (audio, games, real-time control) and environments with tight memory budgets.

|                     | Tracing GC (.NET)            | ARC (ArcSharp)                            |
|---------------------|------------------------------|-------------------------------------------|
| **Reclamation**     | Deferred, at collection time | Immediate, at last release                |
| **Pauses**          | Stop-the-world phases        | None (work is spread inline)              |
| **Per-object cost** | Amortised                    | A counter field + retain/release traffic  |
| **Cycles**          | Collected automatically      | Leaks unless broken by `WeakReference<T>` |
| **Determinism**     | Nondeterministic finalisers  | Deterministic destructors                 |

## Key Features
- **Deterministic Finalisation**: Object destructors run the moment the last reference is dropped.
- **No Runtime GC**: No background collection threads or stop-the-world pauses.
- **Native Execution**: Compiles directly to LLVM IR and then to native machine code via `clang` or `llc`.
- **C# 12 Subset**: Supports classes, inheritance, interfaces, virtual methods, arrays, strings, and more.
- **Cycle Breaking**: Recognises standard `System.WeakReference<T>` to break reference cycles, maintaining Roslyn compatibility.

## Project Status
ArcSharp is currently a **Proof of Concept**.
- **Run-verified** on Windows x64 and Linux x64.
- **Full pipeline** (Compile → LLVM → Link → Run → ARC Accounting) is operational.
- **Diagnostics**: Stable codes (`ARC0001`…) and severities.
- **Analysis**: Conservative definite-assignment analysis.

## Getting Started

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [LLVM / Clang](https://llvm.org/) (Ensure `clang` or `llc` is in your PATH)

### 1. Build the Compiler
```powershell
dotnet build -c Release
```

### 2. Compile and Run a Program
You can use the compiler DLL directly or the helper script (Windows).

**Using the DLL (One-shot compile & run):**
```powershell
dotnet bin/Release/net8.0/arcsharp.dll samples/inherit.cs --target windows --run --runtime runtime/arc_runtime.c
```

**Using the Helper Script:**
```powershell
powershell -ExecutionPolicy Bypass -File tools/build-windows.ps1 samples/inherit.cs -Run
```

### 3. Run Verification Suite
```powershell
powershell -ExecutionPolicy Bypass -File tools/verify.ps1
```

## Project Structure
- `src/` — Compiler source code (Lexing, Syntax, Binding, CodeGen, Driver).
- `runtime/` — The C runtime (`arc_runtime.c`) providing the object model and ARC primitives.
- `samples/` — Example C# programs and test cases.
- `tools/` — Build and verification scripts.

## Documentation
- **[ARCHITECTURE.md](ARCHITECTURE.md)**: Deep dive into the ARC model, object layout, and compiler pipeline.
- **[ROADMAP.md](ROADMAP.md)**: The plan for production-readiness (Generics, Exceptions, Async, etc.).
- **[VERIFICATION.md](VERIFICATION.md)**: Test results and ARC accounting verification.

## Portability
While ArcSharp uses LLVM IR for portability, the current implementation is scoped to **x86-64** (Windows/Linux) due to a fixed 8-byte slot assumption in the object model. Expanding to ARM and 32-bit targets is a P1 roadmap item.
