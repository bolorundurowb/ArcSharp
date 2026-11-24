# Lambdas and captures

ArcSharp supports synchronous expression and block lambdas with non-generic
delegates declared in the same program. Delegate signatures use by-value
parameters and results. Static lambdas use the same single-cast invocation as
capturing lambdas.

Roslyn identifies each captured variable before lowering the enclosing body.
An initialised ordinary local or a by-value parameter gets an ARC-managed cell.
The enclosing method and each lambda read and write that cell, so mutations
remain visible even after the method returns. A class receiver is held
directly by the generated lambda object. Captures that use other local
declaration forms are rejected.

A source delegate has an abstract `Invoke` slot. Each lambda site generates a
concrete ARC class that implements that slot and owns its capture cells and
receiver. Invocation uses the normal virtual call path, including null
checks and exception unwinding. The existing layout, field ownership and
teardown rules also apply to generated objects. A delegate field can form a
cycle through a capture. The compiler follows those edges for `ARC3100`.

Nested lambdas, anonymous methods, method-group conversions, local functions,
multicast operations, delegate equality other than comparison with null, and
events are outside the current implementation. Framework delegate types such
as `Func<T>` and `Action` are unavailable. The compiler also rejects capturing
a finaliser's receiver.

The Windows verification harness runs [lambdas.cs](../../samples/lambdas.cs)
for shared mutation, an escaping capture, a static lambda and a null
invocation. [lambda_cycle.cs](../../samples/lambda_cycle.cs) checks the warning
and the two deliberately leaked objects.
