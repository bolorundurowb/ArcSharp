# Nullable values

A nullable value stores a presence flag and its underlying value inline.
`HasValue`, `Value` and `GetValueOrDefault` read that representation.
Reading `Value` on an empty nullable raises `InvalidOperationException`.

## Conversions and operators

Conversions preserve absence or convert the contained value. Built-in lifted
arithmetic propagates absence. Equality accounts for two absent operands,
and relational operations return false when either operand is absent.
Nullable Boolean `&` and `|` implement their three-valued rules. The `??`
operator evaluates its fallback only when the first operand is absent or null.

A user-defined operator on a struct is lifted in the same way. When either
operand is absent, the result is absent, or false for a comparison, and the
operator is not called. A user-defined conversion between a struct and its
nullable form follows the same rule. A lifted user-defined conversion works
only when the operand type matches the parameter type of the conversion.

## Updates

Compound assignment, `++` and `--` work on a nullable local, field or array
element. The target is evaluated once. When the target is absent,
the update leaves it absent and does not evaluate the operator. The value of a
postfix update is the old value, which can be absent.

## Boxing a nullable struct

A nullable struct converts to `object` or to an interface it implements. An
absent value becomes `null`. A present value is boxed as the struct itself, so
`o as S?`, `(S?)o` and `(S)o` all read an ordinary boxed `S`. Casting `null`
to `S?` gives an absent value. Casting an object of another type raises
`InvalidCastException`.

Only a nullable struct declared in the program has a box. Boxing a nullable
primitive, such as `int?`, or a nullable enum stays rejected with `ARC3001`,
for the same reason that boxing a plain primitive is rejected. The
null-coalescing assignment `??=` is also still rejected.

## Storage

Copying a nullable struct uses ordinary struct ARC helpers for embedded
references. A boxed nullable struct is an ordinary ARC object, so it is
released at the last strong reference like any other box.

## Verification

The `nullable_values` sample checks conversions, lifted arithmetic and
comparisons, coalescing, pattern binding, and a reference field carried by a
nullable struct. `nullable_updates` checks lifted compound and increment
updates, absent targets, and user-defined lifted operators and conversions.
`nullable_struct_boxing` checks boxing to `object` and an interface, `as` and
cast back, and the failure cases. Both new samples were also run with the
ordinary .NET compiler and produced the same output. The `nullable_as` and
`nullable_boxing` must-fail samples check that nullable primitives are still
rejected. The analyser has matching tests.
