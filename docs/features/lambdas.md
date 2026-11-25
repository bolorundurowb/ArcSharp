# Lambdas and captures

ArcSharp supports synchronous expression and block lambdas. A lambda converts to
any delegate type in the program, generic or not, and to the framework's
`Action`, `Func` and `Predicate<T>`. Parameters and results are passed by value.

A lambda creates a delegate, so it works with everything that
[delegates](delegates.md) support: `+=`, `-=`, `==`, `?.Invoke` and calls. A
lambda and a method group can sit in the same multicast list.

This program compiles and runs with the current compiler. It prints `21` and
then `10`:

```csharp
delegate int Compute(int value);

class Program
{
    static void Main()
    {
        var offset = 10;
        Compute plus = value => value + offset;
        offset = 20;                    // the lambda sees the new value
        System.Console.WriteLine(plus(1));     // 21

        Compute chain = plus;
        chain += value => value * 2;    // a lambda in a multicast list
        System.Console.WriteLine(chain(5));    // 10, the value of the last handler
    }
}
```

## Captures

Roslyn identifies each captured variable before the enclosing body is lowered.
An ordinary local, an array `foreach` iteration variable, or a by-value parameter gets an ARC-managed cell.
The enclosing method and the lambda both read and write that cell, so a
mutation is visible to both, including after the method returns. A class
receiver that the lambda uses is held directly by the closure.

Nested lambdas and local functions can share the same cells across multiple
frames. See [Nested closures and local functions](nested-closures.md).

## Representation

Each lambda site generates a closure class. The closure holds the capture cells
and the receiver as fields. Its body is an ordinary instance method.

The lambda expression creates the closure and a delegate. The delegate's target
is the closure, and its function is a thunk that calls the body. A lambda
therefore runs through the same path as a method-group delegate, so the null
check, multicast, equality and exception rules are the same. A lambda that
captures nothing still gets a closure, so every delegate has a target.

Each lambda creation allocates two objects: the closure and the delegate. The
allocation counts in [Verification](../verification.md) include both.

## Cycles

The cycle graph follows a closure's fields. A delegate type has an edge to each
closure that it creates, so a delegate stored in a field of the object it
captures forms a cycle that `ARC3100` reports. The program still compiles, and
the cycle leaks as the warning says. Break it with a `WeakReference<T>`.

Delegates created from local functions have the same capture edges.

## Limits

- Anonymous methods (`delegate { ... }`) are rejected with `ARC3001`.
- An `async` lambda is rejected with `ARC3001`.
- A lambda whose delegate has a `ref` or `out` parameter, or a `ref` result, is
  rejected with `ARC3001`.
- A lambda in a struct method cannot capture `this`, and a lambda in a finaliser
  cannot capture the object being finalised. Both are rejected with `ARC3001`.
- Captures keep their own ARC ownership when the lifetime pass cannot prove
  they stay within the frame.

## Verification

- [`samples/lambdas.cs`](../../samples/lambdas.cs) covers shared mutation, an
  escaping capture, a static lambda and a null invocation.
- [`samples/lambda_cycle.cs`](../../samples/lambda_cycle.cs) checks the
  `ARC3100` warning and the objects that the cycle leaks.
- [`samples/delegates_lambdas.cs`](../../samples/delegates_lambdas.cs) checks
  that a lambda shares a local with its method, that a lambda and a method
  group combine in one multicast delegate, that a lambda is removed by equality,
  that two lambdas written apart are unequal, that `?.Invoke` skips a null
  delegate, and that a call through a null delegate raises
  `NullReferenceException`.
- [`samples/negative/lambda.cs`](../../samples/negative/lambda.cs) and
  [`samples/negative/lambda_ref_parameter.cs`](../../samples/negative/lambda_ref_parameter.cs)
  must fail with `ARC3001`.
