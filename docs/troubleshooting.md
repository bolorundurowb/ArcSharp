# Troubleshooting

Find the symptom you see, then follow the steps for it. If a program compiles
but behaves strangely, also check [Known issues](language-reference.md#known-issues).

## Building the compiler

### `dotnet build` reports that a compatible SDK was not found

`global.json` requires a .NET 8 SDK and does not roll forward to .NET 9 or
later. Run `dotnet --list-sdks`. If no `8.0.x` line appears, install the
[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Newer SDKs can
stay installed alongside it.

## Building a program

### `compilation failed: N error(s)`

Read the first error; later ones are often caused by it. Each code is explained
in [Diagnostics](diagnostics.md). A long run of `ARC1001` errors usually comes
from one unsupported construct. Constructs the compiler recognises are reported
as `ARC3001` instead, so look for that code first when the feature is listed in
the [Language reference](language-reference.md#not-supported).

### `error: could not run 'llc'`

The compiler could not start `llc`, `clang`, the `--cc` compiler, or the
program named by `--run`. The rest of the message is the operating-system
reason, usually that the file was not found. That line is followed by
`error: llc failed` or `error: clang build failed`. The compiler exits with
code `2`.

Pass `--clang "C:/Program Files/LLVM/bin/clang.exe"`, which is the supported
path on Windows. Without `--clang`, `llc` must be on `PATH`. `. tools\env.ps1`
adds the default LLVM folder. A wrong `--clang`, `--llc`, or `--cc` path
produces the same error.

### `error: option -o requires a value`

An option that takes a value was not followed by one. This covers `-o`,
`--target`, `--runtime`, `--llc`, `--cc`, and `--clang`. Add the value. The
compiler exits with code `2`.

### `--run` did nothing

On Windows, `--run` works only together with `--clang`. Without it, the
compiler writes an object file and prints how to link it.

### `clang` cannot find `arc_runtime.c`

`--runtime` defaults to `runtime/arc_runtime.c` relative to the **current
directory**. Run the compiler from the repository root, or pass the full path.

### `clang` reports `use of undefined value`

The source compiled, but the generated IR calls an `abstract` method, which has
no body. Declare the method `virtual` with a default body, and `override` it.
See [Errors from clang](diagnostics.md#errors-from-clang).

### `clang` reports that it cannot find `link.exe` or the C runtime libraries

`clang` uses the Microsoft linker and libraries on Windows. Install
[Visual Studio Build Tools](https://visualstudio.microsoft.com/visual-cpp-build-tools/)
with the "Desktop development with C++" workload, then open a new terminal.

### `warning: overriding the module target triple`

Expected and harmless. `clang` adds the exact MSVC version to the target that
the compiler wrote.

## Running a program

### `IndexOutOfRangeException: index 5, length 2`, exit code 70

An array index was outside the array. The runtime stops the program with exit
code `70`. There is no exception handling, so this cannot be caught. The `[arc]`
line printed afterwards shows the objects that were still alive at that moment;
those are not leaks in your program.

### `ArrayTypeMismatchException`, exit code 70

An object was stored into an array whose element type it does not match. This
happens through array covariance: a `Dog[]` used as an `Animal[]` cannot hold a
`Cat`.

### The program exits with `-1073741819` and prints no `[arc]` line

That code is `0xC0000005`, an access violation. The usual cause is reading a
field or calling a method through a `null` reference. ArcSharp does not check
for `null`, so add an `if (x != null)` test where the value can be missing. An
invalid class cast can cause the same crash later; use `as` instead of `(T)x`.

### The program exits with `-1073741676`

That code is `0xC0000094`, integer division by zero. Check the divisor before
dividing.

### The `[arc]` line appears before the program's output

Standard output is buffered when it is redirected to a file or a pipe, and the
accounting line goes to standard error, which is not. The order of the two
streams is not meaningful. Run the program directly in a console to see them in
order.

## Wrong results

### `live` is greater than zero

Some objects were never destroyed. Usually this is a reference cycle; see
[Finding leaks](memory-model.md#finding-leaks). Objects held by static fields
also count, because static fields are not cleared when `Main` returns.

### A field has the wrong starting value

Field initialisers such as `public int count = 5;` are ignored. Assign the
value in the constructor.

### Two strings that look equal are not `==`

String `==` compares references, not contents.

### A number prints with fewer digits than under .NET

`float` and `double` print with six significant digits.

## Comparing with .NET

ArcSharp source is meant to be valid C#. When behaviour surprises you, build
the same file with .NET and compare:

```powershell
dotnet new console -o compare
Copy-Item app.cs compare\Program.cs -Force
dotnet run --project compare
```

Results that should differ, such as when `WeakReference<T>.TryGetTarget`
returns `false`, are listed in
[Differences from .NET](language-reference.md#differences-from-net).

## Reporting a problem

Include:

1. The smallest source file that shows the problem.
2. The exact command you ran.
3. The full output, including standard error and the `[arc]` line.
4. The output of `dotnet --version` and `clang --version`.
5. If the program built, the generated `.ll` file. Create it with `--emit-llvm`.
