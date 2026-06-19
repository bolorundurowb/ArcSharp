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
and open questions. See **[VERIFICATION.md](VERIFICATION.md)** for test results.

## What works today (verified end-to-end)

Classes, fields, instance + static methods, constructors with `: base(...)`
chaining, single inheritance, `virtual`/`override` (vtables), interfaces
(itable dispatch), single-dimension arrays of value or reference elements,
strings + `Console.Write`/`WriteLine`, `if`/`while`/`for`, `int`/`long`/`bool`,
recursion — and the memory model: deterministic ARC with `retain`/`release`,
recursive destruction, and a **`weak` field modifier that breaks reference
cycles**. Every sample reclaims all memory (`live=0`); the one strong-cycle
sample leaks on purpose to document the limitation.

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
                    [--run] [--no-bounds] [--runtime path] [--llc name] [--cc name]
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

## The `weak` extension

Standard C# has no `weak` storage class, so ArcSharp adds a non-standard `weak`
field modifier to break cycles (see ARCHITECTURE.md §5):

```csharp
class Parent { public Child child; }       // strong: keeps child alive
class Child  { public weak Parent owner; }  // weak: does NOT keep parent alive
```

## Layout

```
src/Lexing/    lexer + tokens
src/Syntax/    AST + recursive-descent parser
src/Binding/   symbols, type/vtable/itable layout, binder -> typed bound tree
src/CodeGen/   LLVM IR emitter (ARC ownership woven in)
src/Driver/    CLI + llc/linker invocation
runtime/       arc_runtime.c — retain/release, weak, arrays, strings, console
samples/       example programs used by the verifier
tools/         env.sh (toolchain), verify.sh (test harness)
```
