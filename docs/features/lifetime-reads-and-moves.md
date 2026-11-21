# Lifetime classification, borrows and moves

`--dump-ownership` lists the bound reference expressions for each method on
stderr. Allocations and reference-returning calls produce owned values. A
borrow uses an existing owner for a limited operation. Shared values use
ordinary ARC. The trace leaves generated IR unchanged.

The pass borrows stable local and by-value parameter reads in length reads,
equality comparisons and call receivers. Later operands in the same
evaluation must be side-effect-free. A call argument can also borrow when
the callee is directly known and every use of that parameter is a proved
non-escaping read. An address taken with `ref`, `out` or `in`, a weak-reference
out target and a `using` resource all keep the ordinary ARC path.
