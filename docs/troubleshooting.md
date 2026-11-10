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
from one unsupported construct, such as `++`, `foreach`, `=>`, or `$"..."`.

### `Unhandled exception. System.ComponentModel.Win32Exception ... 'llc'`

You did not pass `--clang`, so the compiler tried to run `llc`, and `llc` is not
on `PATH`. Pass `--clang "C:/Program Files/LLVM/bin/clang.exe"`, which is the
supported path on Windows. The same exception names `clang` when the path given
to `--clang` is wrong.

### `Unhandled exception. System.IndexOutOfRangeException` at `Program.cs`

An option that takes a value, such as `-o`, `--clang`, or `--runtime`, was the
last argument. Add the value.

### `--run` did nothing

On Windows, `--run` works only together with `--clang`. Without it, the
compiler writes an object file and prints how to link it.

### `clang` cannot find `arc_runtime.c`

`--runtime` defaults to `runtime/arc_runtime.c` relative to the **current
directory**. Run the compiler from the repository root, or pass the full path.

### `clang` reports `use of undefined value`, `invalid redefinition of function`, or `expected '=' in global variable`

The source compiled, but the generated IR is invalid. These come from an
abstract method, two overloads with the same number of parameters, and a static
auto-property respectively. See [Errors from clang](diagnostics.md#errors-from-clang).

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

### A `bool` prints the wrong value

Printing a `bool` directly can be wrong because of a known calling-convention
mismatch. Test it with `if` and print a string from each branch.

### A character prints as a number

`char` values print as their character code. Use a one-character string.

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
