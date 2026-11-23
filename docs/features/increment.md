# Numeric updates

Prefix and postfix `++` and `--` work on numeric locals, parameters, fields,
elements of arrays and non-virtual auto-properties. Prefix forms yield the
updated value and postfix forms yield the previous value.

The emitter computes the storage address once, pins any heap owner, and
loads and stores the value at that address. The underlying integer width
controls wrapping. A `char` uses only the low 16 bits of its storage slot.
Floating-point increments and decrements use floating-point arithmetic.

Arithmetic compound assignments on the same storage forms reuse that address
while evaluating the right operand. The operation promotes narrow integers
and converts its result to the storage type before writing. Integral division
and remainder run the same exception checks as the corresponding binary
operators. Auto-properties use their synthesised backing slot, so their
receiver is evaluated once. String `+=` retains the old string while the right
operand runs and transfers the concatenation result to that slot.

The verification harness checks prefix and postfix results, array index
evaluation, narrow integer wrapping, and floating-point arithmetic. It also
checks the diagnostic for an increment through a struct property with accessor
bodies. [Updates through accessors](accessor-updates.md) covers properties and
indexers that have bodies.
