# Overloaded storage updates

User-defined operators can update locals, supported parameters, fields,
array elements and non-virtual auto-properties. A compound update evaluates
its target once, reads its value, calls the operator and stores the result.
A postfix increment or decrement yields the original value.

The emitter pins an interior target while evaluating the operator. If a
reference array is covariant, it checks the replacement against the array's
actual element type before transferring ownership. Struct reads and writes
use value copies. Updates through user-written accessors remain unsupported.

The verification harness checks reference and struct updates, prefix and
postfix result ownership, and a covariant array that rejects an operator
result. The failing store releases the result and leaves the array unchanged.
