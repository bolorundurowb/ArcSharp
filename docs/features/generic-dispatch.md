# Generic methods in virtual dispatch, and static abstract members

This step finishes the generics work in milestone M6. A method can now be both
generic and `virtual`, `abstract`, an `override`, or an interface member. An
interface can also declare `static abstract` and `static virtual` members that
code calls through a type parameter.

## Virtual and interface generic methods

Generic classes and methods are specialised for each closed set of type
arguments, and a virtual call needs a fixed slot to dispatch through. A generic
method has no single slot, because `Echo<int>` and `Echo<string>` are different
methods. The compiler therefore gives each closed instantiation its own
selector, which works like a slot.

1. At a call site such as `shape.Echo(5)`, the compiler finds the first
   declaration of the method (the root of the `override` chain, or the
   interface method). It then creates the instantiation `Echo<int>` of that
   root and registers a selector for it.
2. After all bodies are lowered, a resolution pass visits every class that
   inherits the root or implements the interface. For each registered
   instantiation, it finds the matching `override` or implementation, creates
   its `Echo<int>` specialisation, and records it in the class's dispatch
   table for that selector.
3. The call is emitted as an ordinary interface-style call through that
   selector.

The resolution pass repeats until nothing new appears. This matters because
specialising an override can reach a new instantiation. In the sample,
`Doubler.Make<T>` calls `Helper.Echo(2.5)`, so `Echo<double>` is only found
while a body is being lowered, after the earlier override resolution ran.

### Decisions and limits

- **Reuse the existing dispatch table.** Interface dispatch already indexes a
  dense table by a global selector. A generic instantiation is another selector
  in the same table, so the emitter and the runtime did not change.
- **Abstract members only need a selector.** An abstract generic method has no
  body, so its specialisation is created without queuing a body.
- **Structs cannot implement generic interface methods.** A struct has no
  object header or table, and generic interface calls on a struct are not
  boxed here. This is reported as `ARC3001`, and the analyser reports it too.
- **Types nested in generic types are still unsupported**, as before.

## Static abstract and static virtual members

`static abstract` and `static virtual` interface members, including operators,
work when they are called through a constrained type parameter:

```csharp
interface IMonoid<T> where T : IMonoid<T>
{
    static abstract T Identity();
    static abstract T operator +(T a, T b);
    static virtual int Weight() { return 1; }
}

static T Fold<T>(T first, T second) where T : IMonoid<T>
{
    return T.Identity() + first + second;
}
```

Because the compiler specialises `Fold<Total>` and `Fold<Count>` separately, the
type argument is known at compile time. Roslyn reports it as the constrained
type of the call, so the compiler resolves `T.Identity()` straight to the
concrete type's member and emits a normal static call. There is no dispatch
table for static members.

The same rule covers static properties, binary and unary operators. A type
that does not override a `static virtual` member uses the interface's default
body, which is compiled as an ordinary static method.

### Decisions and limits

- **A static member is never marked virtual.** A `static virtual` default must
  not be called through a table, because there is no receiver. An earlier draft
  marked it virtual and produced IR with an empty receiver.
- **Generic static abstract methods are rejected** (`ARC3001`), for example
  `static abstract T Make<T>()`. They need a selector and a closed type
  argument together, and nothing in the compatibility target needs them.
- **The reference assembly needed two additions.** Roslyn only accepts static
  abstract members when `RuntimeFeature.VirtualStaticsInInterfaces` exists, and
  list patterns and extension methods needed `UIntPtr` and
  `ExtensionAttribute`. These live in `ref/ArcSharp.References`.

## Inference and constraints

Method type arguments are inferred by Roslyn, so the full C# rules apply:
widening between arguments (`Pick(1, 2.5)` is `double`), base and derived
arguments, array elements, nested generic types, `out` arguments, constraints
that name another type parameter, and the preference for a non-generic
overload. The `generic_inference` sample covers these. Constraint forms were
already enforced by Roslyn, and the compiler specialises only the closed
results.

## Verification

- `generic_dispatch` covers interface and `virtual`, `abstract` and `override`
  generic methods, a generic owner, a constrained call through a type
  parameter, and the late instantiation described above. It ends with
  `live=0`.
- `static_abstract` covers a static method, operators, a property with a getter
  and a setter, and a `static virtual` default and its override. It ends with
  `live=0`.
- `generic_inference` covers the inference cases above and ends with `live=0`.
- The must-fail samples `generic_interface_struct` and
  `static_abstract_generic` check the two `ARC3001` rejections.
