# Command-line reference

The compiler is a .NET 8 program. After `dotnet build -c Release`, run it with:

```text
dotnet bin/Release/net8.0/arcsharp.dll <input.cs> [more.cs ...] [options]
```

## Options

| Option | Default | Meaning |
|---|---|---|
| `<input.cs> ...` | Required | One or more source files. Their types are merged into one program. |
| `-o <base>` | The first input's name, in the current directory | Output path **without** an extension. The compiler adds `.ll`, `.exe`, `.obj`, or `.o`. |
| `--clang <path>` | Not set | Build the executable in one step: `clang` compiles the IR and the C runtime and links them. This is the recommended path on Windows. |
| `--run` | Off | Run the executable after building it. Has no effect on Windows unless `--clang` is also given. |
| `--runtime <path>` | `runtime/arc_runtime.c` | The C runtime source to compile into the program. The default is relative to the current directory, so pass it explicitly when you run the compiler from elsewhere. |
| `--target windows` | Yes | Emit IR for `x86_64-pc-windows-msvc`. |
| `--target host` | | Emit IR for `x86_64-pc-linux-gnu`, whatever the machine you run on. This path is not part of the current verification. |
| `--emit-llvm` | Off | Stop after writing the `.ll` file. |
| `--no-bounds` | Off | Leave out array bounds checks. An out-of-range index then reads or writes arbitrary memory. |
| `-Werror`, `--warn-as-error` | Off | Treat warnings as errors. |
| `--llc <name>` | `llc` | The LLVM static compiler to use when `--clang` is not given. |
| `--cc <name>` | `gcc` | The C compiler and linker for `--target host` when `--clang` is not given. |
| `-h`, `--help` | | Print a one-line usage summary. |

Every option that takes a value needs that value. If it is missing, the
compiler reports `error: option <name> requires a value` and exits with code
`2`. This covers `-o`, `--target`, `--runtime`, `--llc`, `--cc`, and `--clang`.

If `clang`, `llc`, the `--cc` compiler, or the program started by `--run`
cannot be started, the compiler reports `error: could not run '<tool>': ...`
and exits with code `2`. Without `--clang`, it needs `llc` on `PATH`;
`. tools\env.ps1` adds the default LLVM folder.

## What the compiler writes

| Command | Files written |
|---|---|
| `--emit-llvm` | `<base>.ll` |
| `--clang <path>` | `<base>.ll`, then `<base>.exe` (Windows) or `<base>` (host) |
| Neither, with `--target windows` | `<base>.ll` and `<base>.obj`, then a message that shows how to link the object |
| Neither, with `--target host` | `<base>.ll`, `<base>.o`, and the executable `<base>` |

## Output streams

The compiler writes diagnostics and progress lines, such as
`[arcsharp] wrote hello.ll`, to **standard error**. A program run with `--run`
writes its own output to standard output, and the runtime writes its
accounting line, `[arc] alloc=... live=...`, to standard error.

If you call the compiler from a script, do not treat output on standard error
as a failure. Check the exit code instead. `tools/verify.ps1` shows one way to
capture both streams from PowerShell.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success. With `--run`, the program also exited with `0`. |
| `1` | Compilation failed because of errors, or because of warnings with `-Werror`. |
| `2` | A usage error: no input file, an input file that does not exist, an unknown option, or an option with no value. Also a tool or program that could not be started (`clang`, `llc`, the `--cc` compiler, or `--run`). |
| `-532462766` (`0xE0434352`) | The compiler crashed with an unhandled .NET exception. |
| Other | With `--clang`, `clang`'s exit code if the build failed. With `--run`, the program's own exit code. |

When the program is run, its exit codes include:

| Code | Meaning |
|---|---|
| `0` | `Main` returned normally, or `static int Main()` returned `0`. |
| Any value | The value returned by `static int Main()`. |
| `70` | A runtime check failed, such as an array index out of range or an array type mismatch. |
| `-1073741819` (`0xC0000005`) | Access violation, usually a null reference. |
| `-1073741676` (`0xC0000094`) | Integer division by zero. |

## Examples

Compile and run one file:

```powershell
dotnet bin/Release/net8.0/arcsharp.dll samples/inherit.cs -o $env:TEMP\inherit `
    --clang "C:/Program Files/LLVM/bin/clang.exe" `
    --runtime runtime/arc_runtime.c --run
```

Compile several files into one program:

```powershell
dotnet bin/Release/net8.0/arcsharp.dll samples/mf_main.cs samples/mf_lib.cs `
    -o $env:TEMP\multifile --clang "C:/Program Files/LLVM/bin/clang.exe" `
    --runtime runtime/arc_runtime.c --run
```

Write the LLVM IR only:

```powershell
dotnet bin/Release/net8.0/arcsharp.dll samples/inherit.cs -o $env:TEMP\inherit --emit-llvm
```

Fail on warnings:

```powershell
dotnet bin/Release/net8.0/arcsharp.dll app.cs -o app -Werror --emit-llvm
```

## Helper scripts

| Script | Purpose |
|---|---|
| `tools/build-windows.ps1 <file.cs> [-Out <base>] [-Run] [-Clang <path>]` | Build the compiler, then compile one file with `clang` |
| `tools/verify.ps1 [-Clang <path>]` | Build the compiler and run every sample through the test harness |
| `tools/env.ps1` | Dot-source it (`. tools\env.ps1`) to put the default LLVM folder on `PATH` and turn off .NET telemetry |

## Reading the generated IR

The `.ll` file is readable text. Symbols follow a fixed naming scheme:

| Symbol | What it is |
|---|---|
| `<Type>__<Method>__<n>` | A method with `n` parameters |
| `<Type>__ctor__<n>` | A constructor with `n` parameters |
| `@<Type>__sf__<Field>` | A static field |
| `@<Type>__ti` | The type descriptor |
| `@<Type>__deinit` | The generated destructor |

Calls to `arc_retain`, `arc_release`, and `arc_assign_take` are the reference
counting operations. [Architecture](architecture.md#4-the-arc-model-ownership-rules)
explains where each one comes from.
