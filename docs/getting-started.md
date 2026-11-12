# Getting started

This guide takes you from a clean Windows machine to a native executable built
from C# source. It takes about ten minutes, most of which is installing tools.

## Before you begin

ArcSharp builds on Windows x64 only. It needs three things.

| Tool | Why | How to get it |
|---|---|---|
| .NET 8 SDK | The compiler itself is a .NET 8 program | [Download the .NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| LLVM `clang` | Turns the generated LLVM IR and the C runtime into an executable | [Download LLVM](https://github.com/llvm/llvm-project/releases), or run `winget install LLVM.LLVM` |
| A Windows linker and C libraries | `clang` hands linking to the Microsoft toolchain | Install [Visual Studio Build Tools](https://visualstudio.microsoft.com/visual-cpp-build-tools/) with the "Desktop development with C++" workload |

The repository's `global.json` selects the newest .NET 8 SDK that you have
installed. It does not roll forward to .NET 9 or later, so a machine with only
a newer SDK cannot build the compiler until you add an 8.0 SDK.

The scripts and the examples below assume that `clang` is installed at
`C:/Program Files/LLVM/bin/clang.exe`, which is the LLVM installer's default.
If yours is elsewhere, pass its path wherever these pages use `--clang` or
`-Clang`.

Check the tools from a PowerShell prompt:

```powershell
dotnet --list-sdks          # expect at least one 8.0.x line
& "C:/Program Files/LLVM/bin/clang.exe" --version
```

## Build the compiler

From the repository root:

```powershell
dotnet build -c Release
```

The compiler is written to `bin/Release/net8.0/arcsharp.dll`. You run it with
`dotnet`; there is no installed `arcsharp` command yet.

## Write a program

Save this as `hello.cs`:

```csharp
using System;

class Greeter
{
    public string name;

    public Greeter(string name)
    {
        this.name = name;
    }

    public string Greet()
    {
        return "Hello, " + name + "!";
    }
}

class Program
{
    static void Main()
    {
        Greeter greeter = new Greeter("ArcSharp");
        Console.WriteLine(greeter.Greet());
    }
}
```

This is ordinary C#. It also builds and runs with `dotnet run`, which is a
useful way to compare behaviour.

## Compile and run it

```powershell
dotnet bin/Release/net8.0/arcsharp.dll hello.cs -o hello `
    --clang "C:/Program Files/LLVM/bin/clang.exe" `
    --runtime runtime/arc_runtime.c --run
```

You should see:

```text
[arcsharp] wrote hello.ll
warning: overriding the module target triple with x86_64-pc-windows-msvc... [-Woverride-module]
1 warning generated.
[arcsharp] wrote hello.exe
[arcsharp] running hello.exe
Hello, ArcSharp!
[arc] alloc=6 dead=6 freed=6 live=0
```

`hello.exe` is a self-contained native program. It does not need .NET to run.

The `clang` warning about the target triple is expected and harmless. Lines
that start with `[arcsharp]` are progress messages from the compiler.

## Read the accounting line

When `Main` returns, the runtime prints one line to standard error:

```text
[arc] alloc=6 dead=6 freed=6 live=0
```

| Field | Meaning |
|---|---|
| `alloc` | Heap objects created, including strings and arrays |
| `dead` | Objects whose last strong reference was released, so their destructor ran |
| `freed` | Allocations whose memory was returned |
| `live` | `alloc` minus `dead`: objects still alive when the program ended |

`live=0` means that every object was reclaimed. A higher number usually means a
reference cycle; see [Memory model](memory-model.md).

The `hello` program allocates six objects: the `Greeter`, the strings, and the
intermediate strings that concatenation builds.

## Shortcuts

`tools/build-windows.ps1` builds the compiler and compiles one file in a single
step:

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-windows.ps1 hello.cs -Run
```

Use `--emit-llvm` to stop after writing the LLVM IR, which is useful when you
want to see what the compiler generated:

```powershell
dotnet bin/Release/net8.0/arcsharp.dll hello.cs -o hello --emit-llvm
```

## Run the test suite

```powershell
powershell -ExecutionPolicy Bypass -File tools\verify.ps1
```

The harness builds the compiler, compiles every program in `samples/`, runs
each one, and checks both its output and its `live=` count. It also compiles
the programs under `samples/negative/`, checks that a bad command line is
reported instead of crashing the compiler, checks the `ARC3100` cycle
warnings, and runs the analyser unit tests. It ends with a line such as
`49 passed, 0 failed`. See [Verification](verification.md) for what each sample
tests.

## Next steps

- Read the [Language reference](language-reference.md) to see which C# features
  you can use. Many common ones, such as generics, `try`/`catch`, `foreach`,
  `++`, and lambdas, are not supported yet.
- Read the [Memory model](memory-model.md) before you write classes that refer
  to each other.
- Browse `samples/` for small, working programs.
