# Conditional values

A conditional expression evaluates its condition and the selected operand.
A switch expression evaluates its input once and tests its arms and guards
in source order. If an input matches no arm, it raises
`System.Runtime.CompilerServices.SwitchExpressionException`.

The emitter joins scalar and reference results through LLVM's `phi`
instruction. A struct result is copied into a shared result slot. A
temporary acquired on one branch is tracked only on that branch and
released with the enclosing statement.

An overloaded `&&` tests its left operand with `operator false`. An overloaded
`||` uses `operator true`. A short-circuit returns the left operand, while the
other path calls the corresponding overloaded `&` or `|`.

The verification harness checks short-circuiting, struct results, selected
switch-arm strings and ARC cleanup after a non-exhaustive match.
