# Checked arithmetic

`checked` and `unchecked` apply to both expressions and statement blocks.
Checked integral addition, subtraction, multiplication, negation, increments
and decrements raise `OverflowException` when the result is outside the
operation's range. Checked conversions raise the same exception when the
converted integer cannot be represented. Floating-point arithmetic retains
its usual IEEE 754 behaviour.

Lowering carries Roslyn's checked flag on each affected operation. The
emitter widens integer arithmetic before comparing the result with its
narrow representation. Floating-point conversions check the truncated value
against the destination range before executing the LLVM conversion. Overflow
uses the existing pending-exception path, so handlers, cleanups and ARC
accounting behave as for other run-time checks.

The verification harness checks signed and unsigned overflow, narrow
increments, numeric conversions and an unchecked expression inside a checked
block. It also checks Roslyn's compile-time diagnostic for a constant overflow.
