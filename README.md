# ArcSharp

A proof-of-concept compiler for a subset of **C# 12** that uses **ARC (Automatic
Reference Counting)** instead of a garbage collector, emits **LLVM IR**, and
produces a native, GC-less executable. Target: **Windows x64**
(`x86_64-pc-windows-msvc`). The compiler is written in C# (.NET 8).

> **AI-led project** — this is a personal exploration of alternative C# runtime
> models beyond Native AOT, driven by curiosity about what compilers and memory
> management strategies the ecosystem could support.

See **[ARCHITECTURE.md](ARCHITECTURE.md)** for the design, the ARC ownership
model, the supported language subset, the features ARC cannot reasonably support,
and open questions. See **[VERIFICATION.md](VERIFICATION.md)** for test results,
and **[ROADMAP.md](ROADMAP.md)** for the production-readiness plan.

## Why ArcSharp — the rationale

Most C# runs on a tracing garbage collector, and for the vast majority of
applications that is the right default. ArcSharp targets the cases where it
isn't: environments where developers would gladly trade away some of C#'s
features — and some of the raw throughput of JIT-compiled IL — for two
properties a tracing GC fundamentally cannot give them.

- **Deterministic finalization.** Under ARC an object's destructor runs at the
  exact, predictable point its last reference disappears — not "sometime later"
  on a finalizer thread. That matters for scarce non-memory resources (file
  handles, sockets, GPU buffers, locks, database connections) and for
  `IDisposable`-style cleanup you want to be both automatic *and* timely, rather
  than depending on `using` blocks everywhere and hoping finalizers eventually run.
- **No garbage collector at all.** No background collection threads, no
  stop-the-world pauses, no managed heap to scan, and a small, auditable runtime.
  This suits latency-sensitive and soft-real-time workloads (audio, control loops,
  trading, games), hard memory budgets, and constrained or embedded targets where
  shipping — and pausing for — a collector is undesirable.

Native AOT already compiles C# to native binaries, but it keeps the tracing GC;
ArcSharp goes a step further and removes the collector itself. The cost is real
and deliberate: pure reference counting leaks cycles (you manage them with
`WeakReference<T>`), refcount traffic has its own overhead, and some language
features lean hard on the GC (finalizer-queue semantics, `async` state-machine
lifetimes, unrestricted shared-memory threading). ArcSharp's bet is that for the
niches above, predictable, GC-free native execution is worth that price — and this
proof of concept exists to explore how far a faithful C# subset can be pushed
under that model.

## What works today (verified end-to-end)

Classes, fields, instance + static methods, constructors with `: base(...)`
chaining, single inheritance, `virtual`/`override` (vtables), interfaces
(itable dispatch), single-dimension arrays of value or reference elements,
strings + `Console.Write`/`WriteLine`, `if`/`while`/`for`, `int`/`long`/`bool`,
`float`/`double`, recursion — and the memory model: deterministic ARC with `retain`/`release`,
recursive destruction, and **`System.WeakReference<T>` to break reference cycles**
(idiomatic C# that also compiles unchanged with Roslyn). Every sample reclaims all
memory (`live=0`); the one strong-cycle sample leaks on purpose to document the
limitation.

## Build & run

Requires the .NET 8 SDK, LLVM `llc`, and a C compiler (`gcc`/`clang`).

```bash
# build the compiler
dotnet build -c Release

# compile a program for Windows x64 (emits .ll + .obj; link on Windows)
dotnet bin/Release/net8.0/arcsharp.dll samples/inherit.cs --target windows

# compile, link and run for the local host (Linux/macOS) in one shot
dotnet bin/Release/net8.0/arcsharp.dll samples/inherit.cs \
    -o /tmp/inherit --target host --run --runtime runtime/arc_runtime.c

# just see the generated LLVM IR
dotnet bin/Release/net8.0/arcsharp.dll samples/inherit.cs --emit-llvm

# run the full verification suite
bash tools/verify.sh
```

### CLI

```
arcsharp <input.cs> [-o name] [--target windows|host] [--emit-llvm]
                    [--run] [--no-bounds] [--runtime path] [--llc name] [--cc name] [--clang path]
```

## Build on Windows x64 (native .exe)

`clang` consumes the generated LLVM IR directly, so a full build is one step.
With your clang at `C:/Program Files/LLVM/bin/clang.exe`:

```powershell
# build the compiler once
dotnet build -c Release

# compile + link + run a program to a native .exe
dotnet bin/Release/net8.0/arcsharp.dll samples/inherit.cs `
    --target windows --clang "C:/Program Files/LLVM/bin/clang.exe" `
    --runtime runtime/arc_runtime.c --run

# or use the helper script
powershell -ExecutionPolicy Bypass -File tools/build-windows.ps1 samples/inherit.cs -Run
```

This emits `inherit.ll`, then runs
`clang inherit.ll runtime/arc_runtime.c -o inherit.exe` and runs it. clang needs
a Windows linker it can find (Visual Studio Build Tools / Windows SDK; or add
`-fuse-ld=lld` if you only have LLVM's `lld-link`). Without `--clang`, the driver
instead emits a `.obj` via `llc` and prints the link command.

## Breaking cycles with `WeakReference<T>`

Pure ARC leaks reference cycles, so ArcSharp recognises the standard
`System.WeakReference<T>` BCL type and lowers it to a non-owning (weak) handle.
Because it is ordinary C#, the same source also compiles unchanged with Roslyn:

```csharp
using System;
class Parent { public Child child; }                 // strong: keeps child alive
class Child  { public WeakReference<Parent> owner; }  // weak: breaks the cycle

// ...
child.owner = new WeakReference<Parent>(parent);
if (child.owner.TryGetTarget(out var p)) { /* p is alive */ }
```

`new WeakReference<T>(x)`, `TryGetTarget(out var p)` / `TryGetTarget(out Parent p)`,
and `SetTarget(x)` are supported. Under ARC, `TryGetTarget` returns `false` the
moment the target's last strong reference is gone — deterministically, unlike the
CLR where it depends on GC timing. (A legacy non-standard `weak` field modifier
also still works, but `WeakReference<T>` is the recommended, Roslyn-compatible form.)

## Layout

```
src/Lexing/    lexer + tokens
src/Syntax/    AST + recursive-descent parser
src/Binding/   symbols, type/vtable/itable layout, binder -> typed bound tree
src/CodeGen/   LLVM IR emitter (ARC ownership woven in)
src/Driver/    CLI + llc