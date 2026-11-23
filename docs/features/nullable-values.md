# Nullable values

A nullable value stores a presence flag and its underlying value inline.
`HasValue`, `Value` and `GetValueOrDefault` read that representation.
Reading `Value` on an empty nullable raises `InvalidOperationException`.

Conversions preserve absence or convert the contained value. Built-in lifted
arithmetic propagates absence. Equality accounts for two absent operands,
and relational operations return false when either operand is absent.
Nullable Boolean `&` and `|` implement their three-valued rules. The `??`
operator evaluates its fallback only when the first operand is absent or null.

Copying a nullable struct uses ordinary struct ARC helpers for embedded
references. Nullable boxing, lifted updates and user-defined lifted
operators remain unsupported.

The verification harness checks conversions, lifted arithmetic and
comparisons, coalescing, pattern binding, and a reference field carried by a
nullable struct. The negative sample checks the nullable boxing diagnostic.
