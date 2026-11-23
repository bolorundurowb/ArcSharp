# Record classes

A `record` is a class whose compiler-written members give it value semantics.
ArcSharp supports record classes, including positional parameters, a body,
inheritance from another record, `with` expressions, and deconstruction.

```csharp
record Point(int X, int Y);
record Student(string Name, int Age, int Grade) : Person(Name, Age);

Point a = new Point(1, 2);
Point moved = a with { X = 10 };
bool same = a == new Point(1, 2);   // true
Console.WriteLine(a);               // Point { X = 1, Y = 2 }
var (x, y) = a;
```

## What the compiler writes

Roslyn declares the members that a record gets, but gives them no bodies. The
compiler writes the bodies as ordinary bound trees, so the emitter and the ARC
rules handle them like user code.

| Member | Behaviour |
|---|---|
| Primary constructor | Stores each parameter in its property, runs field initialisers, then calls the base record's constructor with the arguments given in the base list. |
| `Equals(R)` and `Equals(Base)` | Compare the exact runtime type, then each member. |
| `==` and `!=` | A null-safe call to `Equals`. |
| `GetHashCode` | Starts at 17, or at the base record's hash, then does `hash * 31 + part` for each member. |
| `ToString` | `R { A = 1, B = x }`, with base members first. |
| `Deconstruct` | One `out` parameter for each positional member. |
| `with` | Copies every field of the original, then runs the initialiser on the copy. |

A few members that Roslyn also synthesises are never lowered, because nothing
in the supported subset can reach them: `EqualityContract`, `PrintMembers`,
the copy constructor, `<Clone>$`, and `Equals(object)`. `BoundSameType` stands
in for `EqualityContract`. It compares the type descriptors of two objects, so
a `Student` never equals a `Person` that has the same name and age.

### Hashing

Integers, enums and characters hash as their integer value, a `bool` as 1 or
0, and a string by its length. A record-typed member uses its own
`GetHashCode`. Floating-point members add nothing. The hash is consistent with
`Equals`, which is the only guarantee a program should rely on, but it is
weaker than the .NET one, so do not depend on specific values.

### Text conversion

A class operand in string concatenation or `Console.WriteLine` is now
converted through its `ToString`, or to its type name when it has none, or to
an empty string when it is `null`. Earlier the compiler treated such an
operand as a string pointer, which was a silent miscompile. Records use the
same path to print class-typed members.

## Deconstruction

`var (a, b) = value;` and `(a, b) = value;` work as statements when the type
has an instance `Deconstruct` method, including the one a record writes. The
targets can be new variables, existing locals or fields, or discards. A
deconstruction used as an expression, a target that is a property, and a
deconstruction that needs a second `Deconstruct` call for a nested part are
reported as `ARC3001`.

## Limits

- **Record structs are rejected** with `ARC3001`. Declare a record class.
- **Generic records are rejected** with `ARC3001`.
- **A struct or array member cannot be printed.** The generated `ToString`
  reports `ARC3001` unless you declare `ToString` yourself.
- **`NaN` equality differs from .NET.** Equality on floating-point members uses
  `==`, so two `NaN` members are never equal.
- **Only a `ToString` found through the static type's base chain is used** when
  a class is converted to text.

## Verification

`samples/records.cs` covers equality, hashing, `ToString`, `with`, inheritance,
deconstruction and positional patterns, and members that are enums, strings,
records and classes. It ends with `live=0`. The same program was also run on
stock .NET and printed the same results. The must-fail samples `record_struct`,
`record_generic`, `record_struct_member` and `record_array_member` expect
`ARC3001`.
