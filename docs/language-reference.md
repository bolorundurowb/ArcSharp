# Language reference

ArcSharp accepts a subset of C# 12. A program that ArcSharp accepts is meant to
be valid C# that the normal .NET toolchain also accepts, so you can build the
same source with `dotnet build` and compare the results.

This page lists what the subset contains, where it behaves differently from
.NET, and the known issues in the current compiler. Each statement on this page
was checked against the compiler; the [Known issues](#known-issues) section
records the behaviour as it is today, not as it is planned.

- [Program structure](#program-structure)
- [Types](#types)
- [Classes, interfaces, and members](#classes-interfaces-and-members)
- [Statements](#statements)
- [Expressions and operators](#expressions-and-operators)
- [Literals](#literals)
- [Built-in library](#built-in-library)
- [Not supported](#not-supported)
- [Differences from .NET](#differences-from-net)
- [Known issues](#known-issues)

## Program structure

A program is one or more `.cs` files passed to the compiler together. Their
type declarations are merged into one program, so a class declared in one file
can be used from another.

- `using` directives are accepted and ignored. You can keep `using System;` so
  the file still builds with Roslyn.
- Block-scoped and file-scoped `namespace` declarations are accepted, but the
  namespace name is discarded. Two types with the same simple name collide even
  when they are in different namespaces.
- Only type declarations may appear at the top level. Top-level statements are
  not supported.
- The entry point must be a static method named `Main` with **no parameters**,
  returning `void` or `int`. `static int Main()` sets the process exit code.
  `Main(string[] args)` is not recognised, and compilation fails with
  [`ARC2050`](diagnostics.md#arc2050).

## Types

### Value types

| C# type | Size | Notes |
|---|---|---|
| `bool` | 1 bit in registers, 8 bytes in fields | Prints as `True` or `False` |
| `char` | 32 bits | Prints as a number; see [Known issues](#known-issues) |
| `byte`, `sbyte` | 8 bits | Declaration and arithmetic only; casts and arrays are not supported |
| `short`, `ushort` | 16 bits | Declaration and arithmetic only; casts and arrays are not supported |
| `int`, `uint` | 32 bits | |
| `long`, `ulong` | 64 bits | |
| `float` | 32 bits | |
| `double` | 64 bits | |

Integer arithmetic wraps on overflow, as in an `unchecked` context. There are
no `checked` contexts.

Value types never live on the heap on their own and never take part in
reference counting. `decimal`, `enum`, and nullable value types (`int?`) are not
supported.

### Reference types

- **Classes** you declare.
- **`string`**, an immutable sequence of UTF-8 bytes.
- **Single-dimensional arrays** `T[]` of any supported element type except the
  small integer types. Multi-dimensional and jagged array syntax
  (`int[,]`, `int[][]`) is not supported.
- **`System.WeakReference<T>`**, where `T` is a class. It is the only generic
  type the compiler recognises, and it can only be the type of a field. See
  [Memory model](memory-model.md).

Every reference-type variable, field, and array element starts as `null`.

### Structs

`struct` declarations are parsed and type-checked, but no code is generated for
them. Do not use structs yet.

## Classes, interfaces, and members

### Classes

```csharp
abstract class Shape : IShape
{
    public int width;
    public static int count;

    public Shape(int width) { this.width = width; }
    public virtual int Area() { return 0; }
}

class Square : Shape
{
    public Square(int side) : base(side) { }
    public override int Area() { return width * width; }
}
```

Supported:

- Single inheritance from one base class, and any number of interfaces.
- Instance and static fields.
- Constructors, including `: base(...)` and `: this(...)` initialisers.
- Instance and static methods.
- `virtual` and `override` methods, dispatched through a virtual table.
- The modifiers `public`, `private`, `protected`, `internal`, `static`, and
  `abstract` on a class, and `new` on a member.
- Instance auto-properties with both accessors:
  `public string Name { get; set; }`. Static auto-properties do not build yet;
  see [Known issues](#known-issues).

Not supported: `sealed`, `readonly`, `const`, `partial`, static constructors,
nested types, finalisers (`~T()`), expression-bodied members (`=> expr`),
properties with accessor bodies, get-only auto-properties, indexers, operator
overloading, extension methods, `params`, and optional or named arguments.

Access modifiers are accepted but **not enforced**. Code in one class can read
another class's `private` field.

### Interfaces

Interfaces declare methods without bodies. A class implements an interface by
listing it after the colon and declaring a public method with the same name and
parameter count. Calls through an interface-typed variable are dispatched at
run time.

The binder reports an error if a class does not implement every interface
method. Interface inheritance (`interface IB : IA`) is parsed, but the base
interface list is ignored.

### Methods and overloading

Methods are identified by their name and their number of parameters. Two
methods with the same name and a different number of parameters work. Two
methods with the same name and the same number of parameters, such as
`Print(int)` and `Print(string)`, do not: every call binds to the first one
declared, and `clang` then rejects the duplicate symbol. See
[Known issues](#known-issues).

### Parameters

- By-value parameters of any supported type.
- `ref`, `out`, and `in` parameters, for value and reference types. The caller
  passes a variable, field, or array element with the matching keyword.
- `out var name` and `out Type name` declare a local at the call site.
- A reference-typed parameter cannot be reassigned inside the method. Copy it
  into a local first.

## Statements

| Statement | Notes |
|---|---|
| Block `{ ... }` | |
| Local declaration | `int x = 1;` or `var x = 1;`. `var` requires an initialiser. |
| Expression statement | Assignment, a method call, or `new`. |
| `if` and `else`, including `else if` | The condition must be `bool`. |
| `while` | |
| `for` | Each of the three clauses is optional. |
| `return` | With or without a value. |

Not supported: `do`/`while`, `foreach`, `switch`, `break`, `continue`, `goto`,
`try`/`catch`/`finally`, `throw`, `using` statements, `lock`, `yield`, `checked`,
`unchecked`, `unsafe`, `fixed`, and local functions. Because `break` and
`continue` are missing, end a loop by making its condition false.

## Expressions and operators

| Category | Operators and forms |
|---|---|
| Arithmetic | `+`, `-`, `*`, `/`, `%`, unary `-` |
| Comparison | `==`, `!=`, `<`, `<=`, `>`, `>=` |
| Logical | `&&`, `\|\|`, `!` (short-circuiting) |
| Assignment | `=`, `+=`, `-=`. Assignment can be used as an expression. |
| String | `+` concatenates a string with a string, number, or `bool` |
| Type tests | `x is T`, `x as T`, and casts `(T)x` |
| Object creation | `new T(args)`, `new T[n]`, `new WeakReference<T>(x)` |
| Access | Fields, properties, static members, `this`, `base` constructor calls, array indexing `a[i]` |
| Grouping | Parentheses |

Not supported: `++` and `--` (write `i = i + 1`), the conditional operator
`?:`, `*=`, `/=` and the other compound assignments, the bitwise and shift
operators (`&`, `|`, `^`, `~`, `<<`, `>>`), `??` and `?.`, `typeof`, `sizeof`,
`nameof`, `default`, lambdas, anonymous types, tuples, pattern matching beyond
a plain `is T`, `switch` expressions, object and collection initialisers, and
interpolated strings (`$"..."`).

A single `&` or `|` is rejected by the lexer with
[`ARC0003`](diagnostics.md#arc0003).

### Conversions

Implicit conversions:

- From `int` to `long`, and from `char` to `int` or `long`.
- Between numeric types.
- From a class to its base class or to an interface it implements.
- From `S[]` to `T[]` when `S` converts to `T` as a reference (array
  covariance). A store into such an array is checked at run time.
- From `null` to any reference type.

Explicit casts `(T)x` are accepted when `T` is `int`, `long`, `bool`, `string`,
`char`, `float`, `double`, or a class or interface name. A cast to a class is
**not checked** at run time; see [Known issues](#known-issues). Use `as` or
`is` when the object might be of a different type.

## Literals

| Kind | Examples |
|---|---|
| Integer | `42`, `42L`, `42U`, `42UL` |
| Floating point | `3.5` (a `double`), `3.5d`, `3.5f` |
| Character | `'a'`, `'\n'` |
| String | `"hello"`, `"tab\there"` |
| Boolean and null | `true`, `false`, `null` |

The escape sequences `\n`, `\t`, `\r`, `\0`, `\"`, `\'`, and `\\` work in
character and string literals. Hexadecimal and binary literals, digit
separators (`1_000`), exponents (`1e6`), Unicode escapes (`\u0041`), verbatim
strings (`@"..."`), raw strings, and interpolated strings are not supported.

Comments use `//` and `/* ... */`.

## Built-in library

ArcSharp has no base class library. The compiler recognises these members
directly:

| Member | Notes |
|---|---|
| `Console.Write(x)`, `Console.WriteLine(x)` | `x` is a `string`, `bool`, integer, `float`, or `double`. `Console.WriteLine()` with no argument prints an empty line. |
| `array.Length` | Number of elements |
| `str.Length` | Number of **bytes** in the UTF-8 encoding |
| `new WeakReference<T>(target)` | |
| `weak.TryGetTarget(out T target)` | Returns `true` and a strong reference if the target is alive |
| `weak.SetTarget(target)` | |

Write `Console`, not `System.Console`; the fully qualified name is reported as
`unknown name 'System'`. Nothing else from .NET exists: no collections, no
`Math`, no string methods, no `ToString()`, and no file or network access.

## Not supported

These features are not implemented. The [Roadmap](roadmap.md) records the plan
and priority for each one.

- Generics, except `WeakReference<T>`.
- Exceptions: `try`, `catch`, `finally`, and `throw`.
- `using` statements and `IDisposable`.
- Struct code generation.
- Enums, delegates, events, indexers, and records.
- Lambdas, closures, local functions, `async`, `await`, and iterators.
- LINQ.
- Threads and anything else that shares objects between threads. Reference
  counts are not atomic.
- Reflection, `dynamic`, attributes, `unsafe` code, and pointers.

The compiler does not always report an unsupported feature clearly. Some
constructs produce a confusing parse error, such as `unknown name 'break'` or
`expected type declaration, found Identifier` for `sealed class`. Dedicated
diagnostics for unsupported constructs are planned as `ARC3001`
([Roadmap section 7](roadmap.md#7-roslyn-analyser--warnings-inside-an-ordinary-c-project)).

## Differences from .NET

These behaviours differ from the same source built with .NET. Most of them are
deliberate consequences of ARC.

| Behaviour | .NET | ArcSharp |
|---|---|---|
| When an object is reclaimed | Some time after it becomes unreachable, when the garbage collector runs | Immediately, when its last strong reference is released |
| Reference cycles | Collected | Leak, unless one edge is a `WeakReference<T>` |
| `WeakReference<T>.TryGetTarget` after the last strong reference is gone | Usually still `true`, until a collection runs | Always `false` |
| String `==` | Compares contents | Compares references; see [Known issues](#known-issues) |
| `string.Length` | UTF-16 code units | UTF-8 bytes. `"héllo".Length` is 6, not 5. |
| `char` in `WriteLine` and concatenation | Prints the character | Prints the character code |
| Formatting a `double` or `float` | Shortest round-trippable form, such as `0.3333333333333333` | Six significant digits, such as `0.333333` |
| Null dereference | `NullReferenceException` | The process crashes with an access violation |
| Integer division by zero | `DivideByZeroException` | The process crashes |
| Array index out of range | `IndexOutOfRangeException`, which can be caught | A message, then the process exits with code 70 |
| Invalid cast to a class | `InvalidCastException` | No check; the program continues with a wrongly typed reference |

## Known issues

These are defects in the current compiler, found by testing. Each one has a
workaround.

| Issue | Workaround |
|---|---|
| **Field initialisers are ignored.** `public int count = 5;` compiles, but the field starts as `0`. | Assign the value in the constructor. |
| **String `==` compares references.** Two equal strings built separately are not `==`. | Avoid comparing strings for equality. There is no content comparison yet. |
| **Printing a `bool` can be wrong.** `Console.WriteLine(a == b)` has been seen to print `True` when the comparison was false. An `if` on the same expression behaves correctly. The cause is a mismatch between the IR and the C runtime in how a `bool` argument is passed. | Branch on the value with `if`, and print a string from each branch. |
| **Abstract methods fail to link.** Calling an `abstract` method through the base type compiles to IR that `clang` rejects with `use of undefined value`. | Declare the method `virtual` with a default body, and `override` it. |
| **Overloads with the same number of parameters do not work.** With `Print(int)` and `Print(string)`, a call to the second reports `ARC2010`, and even when every call matches the first, `clang` reports `invalid redefinition of function`. | Give the methods different names. |
| **Abstract classes can be instantiated.** `new Shape()` compiles when `Shape` is `abstract`. | Do not rely on the compiler to catch it. |
| **Get-only auto-properties cannot be assigned.** `public int Id { get; }` cannot be set, even in a constructor. | Declare `{ get; set; }`. |
| **Static auto-properties fail to build.** `public static int Count { get; set; }` produces a backing-field symbol that `clang` rejects with `expected '=' in global variable`. | Use a `public static` field. |
| **`WeakReference<T>` works only as a field type.** A local such as `WeakReference<Node> w = ...;` is parsed as a comparison and reported as `unknown name 'WeakReference'`. | Keep the weak reference in a field of an object. |
| **Class casts are unchecked.** `(Dog)animal` succeeds even when `animal` is a `Cat`. | Use `animal as Dog` and test for `null`. |
| **`char` prints as a number.** `Console.WriteLine('A')` prints `65`. | Use a one-character string. |
| **Unicode escapes are not decoded.** `"\u0041"` does not produce `A`. | Write the character directly in the UTF-8 source file. |
| **Small integer casts are rejected.** `(byte)x` and `new byte[n]` do not compile. | Use `int` or `long`. |

If you find another difference, check it against `dotnet run` first, then
record it here with a reproduction.
