# Bitwise operators and shifts

Integral values support `&`, `|`, `^`, `~`, `<<`, `>>`, and `>>>`. Boolean
operands also support eager `&`, `|`, and `^`. Compound forms work on
supported locals, parameters, fields, array elements and non-virtual
auto-properties.

The emitter masks a shift count to five bits for a 32-bit value and six
bits for a 64-bit value before shifting. Signed `>>` preserves the sign bit.
Unsigned `>>` and `>>>` shift zeroes into the high bits. Compound operations
use the same rule and write through the evaluated storage address.

The verification harness checks eager Boolean evaluation, narrow and wide
shift counts, signed and unsigned right shifts, and compound updates. It
checks that an invalid operand and a user-defined shift operator produce
diagnostics.
