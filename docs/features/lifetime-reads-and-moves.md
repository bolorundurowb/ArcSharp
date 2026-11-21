# Lifetime classification, borrows and moves

`--dump-ownership` lists the bound reference expressions for each method on
stderr. Allocations and reference-returning calls produce owned values. A
borrow uses an existing owner for a limited operation. Shared values use
ordinary ARC. The emitter still applies the same reference-counting rules
to all expressions, and the trace leaves generated IR unchanged.
