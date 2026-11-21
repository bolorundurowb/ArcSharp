# Lifetime classification, borrows and moves

`--dump-ownership` lists the bound reference expressions for each method on
stderr. Allocations and reference-returning calls produce owned values. A
borrow uses an existing owner for a limited operation. Shared values use
the ordinary ARC path. A move transfers an existing owned value and clears
its source slot. The trace leaves generated IR unchanged.

The pass borrows stable local and by-value parameter reads in length reads,
equality comparisons and call receivers. Later operands in the same
evaluation must be side-effect-free. A call argument can also borrow when
the callee is directly known and every use of that parameter is a proved
non-escaping read. An address taken with `ref`, `out` or `in`, a weak-reference
out target, and a `using` resource all keep the ordinary ARC path.

Returning a local directly transfers its +1 to the caller. The final
declaration in a method can transfer a local into a fresh destination when
the destination is cleaned up after the source would have been. The final
call to a known static method can move a single non-escaping local argument
into a caller-owned slot. Its cleanup runs at the source's original
`func.exit` position, including when another path returns before the move.
Other stores and calls use ordinary ARC.

The Windows verification harness checks exact accounting, the M2
destruction-point sample, and a regression covering each move, an early
return and an argument whose owner is cleared through an alias during a call.
See [Verification](../verification.md) for the measured ARC traffic.
