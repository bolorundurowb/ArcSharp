# Diagnostics

The compiler reports problems as diagnostics. Each one has a stable code, a
severity, and a message.

```text
C:\src\app.cs(6,19): error ARC1001: unexpected token ByteKw 'byte' in expression
(12,0): error ARC2010: cannot convert 'int' to 'string'
(4,0): warning ARC2100: local 'total' may be used before assignment
compilation failed: 2 error(s), 1 warning(s)
```

- Lexer and parser diagnostics start with the file name, followed by the line
  and column.
- Binder diagnostics show only the line, with column `0`, because they are
  produced after the files have been merged into one program. When you compile
  several files, find the line in the file that declares the type involved.
- Definite-assignment warnings report the line of the **method** that contains
  the problem, not the line of the read.

Any error stops compilation, and the compiler exits with code `1`. Warnings do
not stop compilation unless you pass `-Werror`.

Codes are never renumbered or reused. A new kind of problem gets a new code.

## Code ranges

| Range | Stage |
|---|---|
| `ARC0xxx` | Lexer: reading characters into tokens |
| `ARC1xxx` | Parser: reading tokens into declarations, statements, and expressions |
| `ARC2xxx` | Binder: names, types, and conversions |
| `ARC3xxx` | Reserved for the planned unsupported-construct and ARC-hazard rules |

## Lexer

### ARC0001

**Error. Unexpected character.** The source contains a character that is not
part of the supported syntax, such as `$`, `@`, `#`, `~`, `^`, or a backtick.

These characters usually come from an unsupported feature: an interpolated or
verbatim string, a preprocessor directive, or a bitwise operator. See
[Language reference](language-reference.md#not-supported).

### ARC0002

**Error. Unterminated string or character literal.** A `"` or `'` has no
closing quote. The literal runs to the end of the file, so the parser usually
adds several `ARC1001` errors at the last line. Fix the `ARC0002` first.

### ARC0003

**Error. Single `&` or `|` not supported.** Bitwise and non-short-circuiting
logical operators are not implemented. Use `&&` or `||` for logic. There is no
replacement for bitwise operations yet.

## Parser

### ARC1001

**Error. Syntax error.** The parser met a token it did not expect. The message
names what it expected and what it found:

| Message | Common cause |
|---|---|
| `expected type declaration, found Identifier` | A top-level statement, or a type modifier such as `sealed`, `partial`, or `record` |
| `expected type, found Assign` | An expression-bodied member (`=>`) |
| `expected Identifier but found IntKw` (or another type keyword) | A member modifier such as `readonly` or `const` |
| `unexpected token ... in expression` | An unsupported operator such as `++` or `?:`, a cast to `byte` or `short`, or a `foreach` loop |
| `expected Semicolon but found ...` | Often a follow-on error from one of the above |

One mistake often produces several `ARC1001` errors in a row. Fix the first
one and compile again.

## Binder

### ARC2001

**Error. Semantic error.** The general binder error. The message describes the
problem. The most common messages are:

| Message | What to do |
|---|---|
| `unknown type 'X'` | Declare the type, or check the spelling. Types from the .NET library other than `WeakReference<T>` do not exist. |
| `duplicate type 'X'` | Two files declare the same type name. Namespaces do not separate them. Rename one. |
| `unknown base type 'X'`, `multiple base classes for 'X'`, `'X' cannot be a base of a class` | A class may extend one class and implement interfaces. |
| `'X' does not implement 'I.M'` | Add a public method with that name and parameter count. |
| `'X' has no member 'Y'`, `'X' has no method 'Y'`, `'X' has no static member 'Y'` | Check the name and whether the member is static. |
| `unknown method 'Y'` | Check the spelling, or call the method through its type name. |
| `method 'M' expects N args, got K` | Pass the declared number of arguments. |
| `no constructor of 'X' takes N arguments`, `no matching constructor on 'X'` | Declare a matching constructor. |
| `argument N must be passed with 'ref'` (or `out`, `in`) | Add the keyword at the call site. |
| `'ref' argument must be a variable, field, or element` | Pass something that can be assigned to. |
| `condition must be 'bool', found 'T'` | Write a comparison, such as `x != 0`. |
| `operator 'op' cannot be applied to 'A' and 'B'` | The operand types do not support that operator. |
| `logical operator requires 'bool' operands`, `'!' requires a 'bool' operand` | Use `bool` operands. |
| `unary '-' requires a numeric operand` | |
| `the left-hand side of an assignment must be a variable, field, or element` | Often a follow-on error from an unknown name. |
| `reassigning a reference-typed parameter is not supported in this first pass` | Copy the parameter into a local and assign the local. |
| `'var' requires an initializer` | Give the local a value, or write its type. |
| `'this' is not available in a static method` | |
| `cannot index 'T'` | Only arrays can be indexed. |
| `cannot instantiate 'X'` | Interfaces cannot be created with `new`. |
| `'is' requires a reference-type operand`, `'as' requires a reference-type test type` | `is` and `as` work only with classes, interfaces, strings, and arrays. |
| `Console has no method 'X'`, `Console.Write/WriteLine takes 0 or 1 argument` | Only `Write` and `WriteLine` with at most one argument exist. Build the text with `+`. |
| `WeakReference<T> requires a reference type argument` | `T` must be a class. |
| `TryGetTarget expects a single 'out' argument`, `SetTarget(target) takes one argument` | |
| `'weak' may only be applied to reference-typed fields` | The legacy `weak` modifier needs a class-typed field. Prefer `WeakReference<T>`. |

### ARC2002

**Error. Unknown name.** A local, parameter, field, or type name could not be
found.

This code also appears for unsupported statements that look like names. For
example, `break;` is reported as `unknown name 'break'`, and a local of type
`WeakReference<T>` is reported as `unknown name 'WeakReference'`. See
[Known issues](language-reference.md#known-issues).

### ARC2010

**Error. Cannot convert.** A value is used where a different type is needed,
and no implicit conversion exists. For example,
`cannot convert 'int' to 'string'`.

To turn a number into a string, concatenate it: `"" + n`. To narrow a number,
use an explicit cast such as `(int)x`.

This error also appears when you call one of two overloads that have the same
number of parameters. The binder chooses the first one declared, so
`Show("a")` with `Show(int)` declared first reports
`cannot convert 'string' to 'int'`.

The message `cannot convert '<null>' to 'T'` or `'<error>'` is usually a
follow-on error. Fix the errors reported before it.

### ARC2011

**Error. Invalid `using` resource.** The resource of a `using` statement does
not meet the cleanup contract: the compiler needs a parameterless instance
`void Dispose()` method to call at scope exit. The messages are:

| Message | Cause |
|---|---|
| `type 'T' used in 'using' has no parameterless 'void Dispose()' method` | The resource type has no instance `Dispose()` method, or its `Dispose` is `static`. `IDisposable` is structural: implementing the interface is not required, but the method must exist. |
| `interface 'I' has no parameterless 'Dispose' method` | The interface-typed resource does not declare `Dispose`. |
| `type 'T' used in 'using' must be a reference type` | A value type cannot be a resource yet, because struct code generation is not implemented. |
| `'using' resource must have an initializer` | `using (Foo x)` without `= ...`. |

### ARC2050

**Error. No entry point.** No class declares `static void Main()` or
`static int Main()`. `Main` must have no parameters; `Main(string[] args)` is
not recognised.

### ARC2100

**Warning. Local may be used before assignment.** The message is
`local 'x' may be used before assignment`. The analysis is conservative: it
warns whenever it cannot prove that every path assigns the local before
reading it. Give the local an initial value where you declare it.

The line number is the line of the method that contains the read.

## Reserved: ARC3xxx

These codes are reserved for rules that are planned but not yet reported. They
are described in [Roadmap section 7](roadmap.md#7-roslyn-analyser--warnings-inside-an-ordinary-c-project).

| Code | Planned rule |
|---|---|
| `ARC3001` | Unsupported construct, such as generics, exceptions, or lambdas |
| `ARC3100` | Classes whose strong fields can form a reference cycle |
| `ARC3200` | Code whose behaviour differs between ARC and the .NET garbage collector |
| `ARC3300` | Use of threads |

## Errors from clang

A few problems pass the compiler's checks and fail later, when `clang` builds
the executable. They appear as `clang` errors followed by
`error: clang build failed`:

| `clang` message | Cause |
|---|---|
| `use of undefined value` | Calling an `abstract` method |
| `invalid redefinition of function` | Two overloads with the same number of parameters, when every call happens to match the first one |
| `expected '=' in global variable` | A static auto-property |

Each one is listed with a workaround in
[Known issues](language-reference.md#known-issues).
