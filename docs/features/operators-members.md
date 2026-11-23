# Operator overloads and member declarations

ArcSharp supports direct calls to user-defined unary, binary and conversion
operators with supported by-value signatures. Roslyn picks the method and
the compiler emits a static call. Conversion symbols include the return type
to distinguish conversions with the same source type. Overloaded updates,
short-circuiting operators and struct signatures are still planned.

Methods, constructors and supported operators can use expression bodies.
Roslyn represents the expression body as a block with the appropriate
expression statement or return, so the existing ARC statement logic applies.

Roslyn validates `const` and `readonly` fields. Constant reads use the
constant value directly, with no field storage. Readonly fields keep
ordinary storage. A nested type in a non-generic containing type includes
the containing type's name in its native symbol.

The verification harness exercises overloaded arithmetic, conversion to two
different types, expression bodies, constants and readonly fields in nested
types. It checks the diagnostic for assigning a readonly field outside a
constructor and for types nested inside a generic type.
