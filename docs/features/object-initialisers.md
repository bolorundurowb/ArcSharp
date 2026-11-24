# Object initialisers

An object initialiser sets members of a new class instance right after its
constructor runs:

```csharp
Car car = new Car("named") { Speed = 5, Wheels = 6 };
Car nested = new Car { Motor = { Power = 300 }, Spare = new Engine { Power = 10 } };
```

## How it works

The compiler creates the object and keeps one reference to it while the member
assignments run. Each assignment is then lowered like any other assignment to
a field or property, so a property setter runs, and the right side is
evaluated in source order. The finished object is the value of the whole
expression.

A nested initialiser such as `Motor = { Power = 300 }` does not create a new
object. It assigns into the object that `Motor` already holds, as C# does.

Roslyn marks the assigned object with an implicit receiver. The compiler keeps
a stack of the objects being initialised, so the receiver refers to the right
one when initialisers are nested.

## Decisions and limits

- **The constructor runs first.** The initialiser runs after any constructor
  body, so a member set by both ends with the initialiser's value.
- **Reuse ordinary assignment.** This keeps ARC handling in one place. The
  new object is held through a capture, so it is released once even when an
  assignment throws.
- **Struct initialisers are rejected** with `ARC3001`, because the struct
  initialiser needs an address that the current struct lowering does not
  give out.
- **Collection initialisers are rejected** with `ARC3001`. They need
  `IEnumerable` and `Add`, which the compiler does not model.
- **A type parameter with an initialiser is rejected.**

## Verification

`samples/object_initialisers.cs` checks fields and properties, a setter's
side effects, an initialiser after a non-default constructor, nested
initialisers, source-order evaluation, and use inside a `return` and in
arithmetic. It ends with `live=0`. The must-fail samples
`initialiser_struct` and `initialiser_collection` expect `ARC3001`.
