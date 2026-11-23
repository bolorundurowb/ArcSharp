# Language reference

ArcSharp accepts a subset of C# 12. A program that ArcSharp accepts is meant to
be valid C# that the normal .NET toolchain also accepts, so you can build the
same source with `dotnet build` and compare the results.

This page lists what the subset contains, where it behaves differently from
.NET, and the known issues in the current compiler. Each statement on this page
was checked against the compiler; the [Known issues](#known-issues) section
records the behaviour as it is today, not as it is planned. The
[Roadmap](roadmap.md) is the direction: Roslyn, then a lifetime pass, then
deterministic finalisation, inside the
[compatibility target](roadmap.md#compatibility-target). None of that changes
what this page describes.

- [Program structure](#program-structure)
- [Types](#types)
- [Generics](#generics)
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

- `using` directives are honoured. The compiler also adds a global
  `using System`, so `Console` resolves without one.
- Block-scoped and file-scoped `namespace` declarations are part of a type's
  identity. Two types with the same simple name in different namespaces are
  different types.
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
| `char` | 32 bits | Prints as the character |
| `byte`, `sbyte` | 8 bits | Explicit integer casts and single-dimensional arrays are supported |
| `short`, `ushort` | 16 bits | Explicit integer casts and single-dimensional arrays are supported |
| `int`, `uint` | 32 bits | |
| `long`, `ulong` | 64 bits | |
| `float` | 32 bits | |
| `double` | 64 bits | |

Integer arithmetic wraps on overflow, as in an `unchecked` context. There are
no `checked` contexts.

Value types never live on the heap on their own and never take part in
reference counting. Struct values are stored inline in the local, field, or
parameter that holds them. `decimal`, `enum`, and nullable value types
(`int?`) are not supported.

### Reference types

- **Classes** you declare.
- **`string`**, an immutable sequence of UTF-8 bytes.
- **Single-dimensional arrays** `T[]` of supported non-struct element types.
  Multi-dimensional and jagged array syntax
  (`int[,]`, `int[][]`) is not supported.
- **`System.WeakReference<T>`**, where `T` is a reference type. It can be used
  in fields, locals, and generic arguments. See
  [Memory model](memory-model.md).

Reference storage defaults to `null` before any explicit initialiser runs.

### Structs

A struct is a value type you declare. Struct values are stored inline: in a
local, in a field of a class or another struct, or behind a `ref`, `out`, or
`in` parameter. Assigning a struct copies it, and the copy owns its own
reference fields.

```csharp
struct Point
{
    public int x;
    public int y;

    public Point(int x, int y) { this.x = x; this.y = y; }
    public int Sum() { return x + y; }
}

class Program
{
    static void Main()
    {
        Point a = new Point(2, 3);
        Point b = a;              // b is a copy
        b.x = 10;                 // a.x is still 2
        Console.WriteLine(a.Sum());
    }
}
```

Supported:

- Instance fields of value, reference, struct, and `WeakReference<T>` type.
- Constructors, instance methods, and `ref`/`out`/`in` parameters.
- Struct locals, struct fields inside classes and other structs, and struct
  assignment.
- Copying a struct retains its reference fields; dropping a struct value
  releases them.

Not supported yet, each reported as `ARC3001`:

- Arrays of structs, struct return values, static struct fields, and by-value
  struct parameters.
- Properties whose type is a struct, and printing a struct value with
  `Console.Write` or `Console.WriteLine`.
- `virtual` or `override` members on a struct, and interfaces on a struct.
- A struct that contains itself, directly or through another struct.
- `using` a struct resource.

## Generics

Generic classes, structs, interfaces, and non-virtual methods are specialised
for each closed set of type arguments. For example, `Box<int>` and
`Box<string>` have separate layouts, type descriptors, methods, destructors,
and static fields. Nested arguments such as `Box<Box<string>>` are supported,
as are arrays and `WeakReference<T>` as type arguments.

Methods accept explicit arguments, such as `Identity<int>(42)`. Basic inference
also recognises exact parameter types, array elements, and matching closed
generic types, so `Identity(42)` infers `int`. Conflicting inferred arguments
require explicit types; full C# overload inference and variance are not
implemented. A non-generic overload wins when its conversions are equally good.

```csharp
using System;
class Program
{
    static T Identity<T>(T value)
    {
        return value;
    }

    static void Main()
    {
        Console.WriteLine(Identity<int>(42));
    }
}
```

Output: `42`.

Constraints support `class`, `struct`, base classes, interfaces, other type
parameters, and `new()`. They are checked when a closed type or method is used;
Roslyn reports constraint failures with `CS` diagnostics. A `new()` reference argument must be concrete
and have a public parameterless constructor, or an implicit default constructor.
Parameterless construction of primitive value types produces zero.

The existing struct restrictions still apply after substitution: struct values
can be stored inline in generic fields and copied through managed references,
but cannot be returned, passed by value, or placed in arrays. Virtual methods
on generic classes and methods of generic interfaces work. Methods that are
themselves generic cannot be virtual, overrides, abstract, or interface members
(`ARC3001`). Method type parameters must not shadow enclosing type parameters.
Templates are checked when instantiated. Nested type declarations, variance,
`default(T)`, and additional constraint forms such as `unmanaged`, `notnull`,
and `class?` remain unsupported. The compiler caps specialisation at 64 nested
instantiations and 1024 total type/method instantiations (`ARC2013`).

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
- Instance and static field initialisers. A declaring constructor can assign
  its get-only auto-properties. Inherited auto-properties use their declaring
  type's accessors.
- Instance and static methods.
- `virtual` and `override` methods, dispatched through a virtual table.
- The modifiers `public`, `private`, `protected`, `internal`, `static`, and
  `abstract` on a class, and `new` on a member.
- Instance and static auto-properties with both accessors:
  `public string Name { get; set; }` and `public static int Count { get; set; }`.
- A finaliser, `~T()`, with no parameters and no modifiers. See
  [Finalisers](#finalisers).

Not supported yet: `sealed`, `readonly`, `const`, `partial`, static constructors,
nested types, expression-bodied members (`=> expr`),
properties with accessor bodies, get-only auto-properties, indexers, operator
overloading, extension methods, `params`, and optional or named arguments.

Access modifiers are accepted but **not enforced**. Code in one class can read
another class's `private` field.

### Finalisers

A class can declare `~T()`. It runs when the last strong reference to the
object is released, on the thread that released it, before the object's fields
are released. A derived class's finaliser runs first, then its base class's.

```csharp
using System;

class Base
{
    ~Base() { Console.WriteLine("Base finaliser"); }
}

class Derived : Base
{
    Base child;

    public Derived() { child = new Base(); }

    ~Derived() { Console.WriteLine("Derived finaliser"); }
}

class Program
{
    static void Main()
    {
        Derived d = new Derived();
        d = null;
        Console.WriteLine("after release");
    }
}
```

```text
Derived finaliser
Base finaliser
Base finaliser
after release
[arc] alloc=6 dead=6 freed=6 live=0
```

The first two lines are the `Derived` object: its own finaliser, then the one
it inherits. The third line is the `Base` object that `Derived` held, which is
finalised after its owner. The `Derived` finaliser could still have used
`child`.

- `GC.SuppressFinalize(obj)` skips the finaliser of one object.
- A finaliser must not store `this` where it outlives the finaliser. The
  compiler rejects the stores it can see (`ARC2016`), and the runtime aborts
  the program with exit code 70 for the rest.
- `GC.Collect()` and `GC.WaitForPendingFinalizers()` compile and do nothing.
  They report info `ARC3200`.
- `GC.ReRegisterForFinalize` is rejected (`ARC3001`).
- Objects in a strong cycle, and objects held by a static field, are never
  released, so their finalisers do not run.
- A finaliser that throws is reported on stderr and then ignored. Teardown of
  the object continues, and the code that released the last reference does not
  see the exception.

The contract and the design are in the [memory model](memory-model.md#finalisation)
and [Deterministic finalisers](features/finalisers.md).

### Interfaces

Interfaces declare methods without bodies. A class implements an interface by
listing it after the colon and declaring a public method with the same name and
parameter count. Calls through an interface-typed variable are dispatched at
run time.

The binder reports an error if a class does not implement every interface
method. Interface inheritance (`interface IB : IA`) is parsed, but the base
interface list is ignored.

### Methods and overloading

A type may declare several methods with the same name. The call binds to the
method whose parameters accept the arguments. When more than one method is
applicable, the one with more parameters of exactly the argument's type is
chosen. If two methods still match equally, the compiler reports that the call
is ambiguous.

Constructors follow the same rule, so `new Box(1)` and `new Box("a")` can both
exist. Two declarations with the same name and the same parameter types are an
error.

### Parameters

- By-value parameters of any supported type.
- `ref`, `out`, and `in` parameters, for value and reference types. The caller
  passes a variable, field, or array element with the matching keyword.
- `out var name` and `out Type name` declare a local at the call site.
- A reference-typed parameter cannot be reassigned inside the method. Copy it
  into a local first.
- A `ref`, `out`, or `in` argument lasts for that call. When the slot is a
  field or an array element, the compiler keeps the containing object alive
  until the call returns, even if the method clears every variable that named
  it. A local, a static field, and a `ref` parameter passed on to another call
  need no extra retain.
- An `in` parameter is read-only, including the fields of an inline struct.
  Assigning that storage, passing it as `ref` or `out`, or calling an instance
  method on an `in` struct is [`ARC2015`](diagnostics.md#arc2015). A reference
  loaded from the struct is a different object and can still be written.
- Ref locals and ref returns are rejected ([`ARC3001`](diagnostics.md#arc3001)).
  A managed reference must not outlive the call that created it.

## Statements

| Statement | Notes |
|---|---|
| Block `{ ... }` | |
| Local declaration | `int x = 1;` or `var x = 1;`. `var` requires an initialiser. |
| Expression statement | Assignment, a method call, or `new`. |
| `if` and `else`, including `else if` | The condition must be `bool`. |
| `while` | |
| `for` | Each of the three clauses is optional. |
| `using` | `using (var x = expr) { ... }` or `using (expr) { ... }`. See below. |
| `return` | With or without a value. |
| `try`, `catch`, `finally` | See [Exceptions](#exceptions). |
| `throw` | `throw expr;` and the rethrow `throw;`. See [Exceptions](#exceptions). |

Not supported: `do`/`while`, `foreach`, `switch`, `break`, `continue`, `goto`,
`lock`, `yield`, `checked`,
`unchecked`, `unsafe`, `fixed`, and local functions. Because `break` and
`continue` are missing, end a loop by making its condition false.

### Exceptions

`try` accepts any number of `catch` clauses, an optional `finally` block, or
both. A `catch` names an exception type, optionally with a variable, or it has
no type and catches everything. Clauses are tried in order, and a clause
matches when the exception is an instance of its type.

```csharp
try
{
    Process(order);
}
catch (ArgumentException e)
{
    Console.WriteLine("bad argument: " + e.Message);
}
catch
{
    Console.WriteLine("something else");
    throw;
}
finally
{
    Console.WriteLine("done");
}
```

ArcSharp ships a small exception library in the `System` namespace. The
compiler adds it to every program, so no reference is needed:

| Type | Base |
|---|---|
| `Exception` | `object` |
| `SystemException` | `Exception` |
| `ArithmeticException` | `SystemException` |
| `DivideByZeroException`, `OverflowException` | `ArithmeticException` |
| `IndexOutOfRangeException`, `InvalidCastException`, `NullReferenceException`, `ArrayTypeMismatchException`, `InvalidOperationException`, `NotSupportedException`, `NotImplementedException`, `ArgumentException` | `SystemException` |
| `ArgumentNullException` | `ArgumentException` |

Each type has a parameterless constructor, which sets a default message, and a
constructor that takes a `string` message. `Message` is a read-only property.
You can derive your own exception classes from any of these, and a derived
class may add fields.

The runtime raises these exceptions itself: `NullReferenceException` for a
member access, call, `.Length`, element access, or `throw null` on a null
reference, `IndexOutOfRangeException` for an array index outside the array,
`InvalidCastException` for a failed class cast, `DivideByZeroException` for `/`
and `%` by zero, `OverflowException` for the minimum value divided by -1, and
`ArrayTypeMismatchException` for a store into a covariant array that the
element type does not accept.

Every reference the code owns is released on the way out, whether the exit is a
normal return or an exception. A `finally` block, and the `Dispose()` call of a
`using` statement, run on both paths. A release that reaches zero during
unwinding runs the object's finaliser at that point. An exception thrown while
another is already unwinding replaces it. See
[Exceptions](features/exceptions.md) for how this is built.

An exception that nothing catches ends the program. The runtime prints
`Unhandled exception. Type: message` on stderr, then the `[arc]` line, and the
process exits with code 70. The counts on that line are balanced, because the
unwind released everything.

Not supported: exception filters (`catch ... when`) and throw expressions are
rejected with `ARC3001`. There is no `InnerException`, `StackTrace`, or
`ToString()`.

### `using` and `IDisposable`

`using` maps a resource to an explicit `Dispose()` call. When the statement's
block is left, the compiler calls `Dispose()` and then releases the resource.
`Dispose()` is not a finaliser. If that release was the last strong reference,
teardown follows at that moment. A finaliser still runs only at the last
strong release, and it does not call `Dispose()`. Both forms are supported, one resource per
statement:

```csharp
using (Logger log = new Logger("a")) { ... }   // declaration form
using (var log = new Logger("a")) { ... }      // with 'var'
using (log) { ... }                            // expression form
```

The resource type must implement `System.IDisposable`. When the resource's
static type is an interface, `Dispose` is dispatched through the itable;
otherwise a `virtual` `Dispose` dispatches through the vtable. The resource
type must be a reference type, and the resource must have an initialiser.
The compiler reports [`ARC2011`](diagnostics.md#arc2011) when the resource is
not disposable.

Differences from .NET:

- The cleanup runs on every path out of the body: block completion, `return`,
  and an exception. If the body throws and `Dispose()` then throws too, the
  exception from `Dispose()` replaces the first one, as in .NET.
- A `null` resource does nothing at scope exit, where .NET throws
  `NullReferenceException` from the `Dispose` call.
- `using var x = ...;` declarations without a block, and multiple resources in
  one statement (`using (a = ..., b = ...)`), are not supported.

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

A single `&` or `|`, and the shift operators, are rejected with
[`ARC3001`](diagnostics.md#arc3001).

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
checked at run time and throws `InvalidCastException` when the object is not an
instance of that class. A `null` passes the cast. Use `as` or `is` when the
object might be of a different type.

## Literals

| Kind | Examples |
|---|---|
| Integer | `42`, `42L`, `42U`, `42UL` |
| Floating point | `3.5` (a `double`), `3.5d`, `3.5f` |
| Character | `'a'`, `'\n'`, `'\u0041'` |
| String | `"hello"`, `"tab\there"`, `"\u0041"` |
| Boolean and null | `true`, `false`, `null` |

The escape sequences `\n`, `\t`, `\r`, `\0`, `\"`, `\'`, `\\`, `\u` followed by
four hex digits, and `\U` followed by eight hex digits work in character and
string literals. `"\u0041"` is `A`. A `\u` or `\U` sequence that is not valid
hex, or a code point above U+10FFFF, is Roslyn error `CS1009`. A character literal
cannot hold a code point above U+FFFF. Hexadecimal and binary literals, digit
separators (`1_000`), exponents (`1e6`), verbatim strings (`@"..."`), raw
strings, and interpolated strings are not supported.

Comments use `//` and `/* ... */`.

## Built-in library

ArcSharp has no base class library. The compiler recognises these members
directly:

| Member | Notes |
|---|---|
| `Console.Write(x)`, `Console.WriteLine(x)` | `x` is a `string`, `bool`, `char`, integer, `float`, or `double`. A `char` prints as the character. `Console.WriteLine()` with no argument prints an empty line. |
| `array.Length` | Number of elements |
| `str.Length` | Number of **bytes** in the UTF-8 encoding |
| `new WeakReference<T>(target)` | |
| `weak.TryGetTarget(out T target)` | Returns `true` and a strong reference if the target is alive |
| `weak.SetTarget(target)` | |

The compiler that runs today recognises the simple names and reports `System`
as an unknown name, so `System.Console` does not compile yet. The reference
assembly declares these types in `System`, so `System.Console.WriteLine`,
`System.WeakReference<T>`, and the same calls after `using System` bind when
Roslyn uses that assembly. Nothing else from .NET exists: no collections, no
`Math`, no string methods, no `ToString()`, and no file or network access.

## Not supported

These features are not implemented. The
[compatibility target](roadmap.md#compatibility-target) is the surface this
runtime is aiming at. It is smaller than the whole of C# and the whole of the
.NET libraries. The list below is what the compiler rejects today, including
items that target marks as full and items it marks as limited.

- Generic features outside the subset described under [Generics](#generics).
- Exception filters (`catch ... when`), throw expressions, and the exception
  types that are not listed under [Exceptions](#exceptions).
- Enums, delegates, events, indexers, and records.
- Lambdas, closures, local functions, `async`, `await`, and iterators.
- LINQ.
- Threads and anything else that shares objects between threads. Reference
  counts are not atomic.
- Reflection, `dynamic`, attributes, `unsafe` code, and pointers.
- The struct restrictions listed under [Structs](#structs).
- Ref locals and ref returns. A managed reference must not outlive the call.

Unsupported constructs are reported as `ARC3001` by the compiler, with a
message that names the construct. The same rule is available in an ordinary
`dotnet build` through the Roslyn analyser in `analyzers/ArcSharp.Analyzers`.
A construct that is still reported as an ordinary parse or name error is a
bug; see [Diagnostics](diagnostics.md).

## Differences from .NET

These behaviours differ from the same source built with .NET. Most of them are
deliberate consequences of ARC. A row that the compiler does not implement yet
says so. The finalisation contract is the runtime's compatibility profile.

| Behaviour | .NET | ArcSharp |
|---|---|---|
| When an object is reclaimed | Some time after it becomes unreachable, when the garbage collector runs | Immediately, when its last strong reference is released. Leaving a lexical scope finalises the object only when that is also the last strong release |
| C# finaliser (`~T()`) | Runs later, on the finaliser thread, and may resurrect the object | Runs synchronously on the releasing thread, derived class before base, before children are released. Resurrection is prohibited: a visible store of `this` is `ARC2016`, and a hidden one aborts the program with exit code 70. The contract is in the [memory model](memory-model.md#finalisation) |
| `GC.Collect`, `GC.WaitForPendingFinalizers` | Collect, or wait for the finaliser queue | Accepted no-ops, with info `ARC3200` |
| `GC.SuppressFinalize` | Skips the finaliser. The object is still collected later | Skips the finaliser and still tears the object down at the last strong release |
| `GC.ReRegisterForFinalize`, `GC.KeepAlive` | Turn finalisation back on, or keep an object alive to a point | `ReRegisterForFinalize` is `ARC3001`. `KeepAlive` is not in the library |
| `Dispose` and finalisation | Often paired by convention. The finaliser is a backstop | Separate. A finaliser does not call `Dispose`. `using` is an explicit call |
| Reference cycles | Collected. Finalisers of cyclic objects may still run | Permitted. `ARC3100` warns when the type graph can form one. An ignored warning, including in a library, leaks, and a finaliser in the cycle does not run. `WeakReference<T>` breaks the cycle |
| `WeakReference<T>.TryGetTarget` after the last strong reference is gone | Usually still `true`, until a collection runs | Always `false`. The weak reference does not keep the object alive |
| `string.Length` | UTF-16 code units | UTF-8 bytes. `"héllo".Length` is 6, not 5. |
| Formatting a `double` or `float` | Shortest round-trippable form, such as `0.3333333333333333` | Six significant digits, such as `0.333333` |
| Null dereference, index out of range, invalid class cast, integer division by zero, array type mismatch | `NullReferenceException`, `IndexOutOfRangeException`, `InvalidCastException`, `DivideByZeroException`, `ArrayTypeMismatchException`, all catchable | The same exceptions, all catchable. See [Exceptions](#exceptions) |
| Unhandled exception | Prints the type, message, and stack trace, and exits with a non-zero code | Prints `Unhandled exception. Type: message`, then the `[arc]` line, and exits with code 70. There is no stack trace |
| Exception members | `Message`, `InnerException`, `StackTrace`, `ToString()`, and more | `Message` only. There is no `InnerException`, because a field of its own type would trigger `ARC3100` |
| Exception filters and throw expressions | Supported | `ARC3001` |
| Exception while another is in flight | A `finally` block that throws replaces the exception in flight | The same. The replaced exception is released |
| `catch` of a type that is not an exception | Compile error | Compile error from Roslyn |
| Exception thrown by a finaliser | Terminates the process on current .NET | Reported on stderr as `[arc] exception in finaliser of T ignored: ...`, then ignored. Teardown continues, and the code that released the last reference does not see it |

## Known issues

No defects are recorded at the moment.

If you find a difference that is not listed above, check it against
`dotnet run` first, then record it here with a reproduction.
