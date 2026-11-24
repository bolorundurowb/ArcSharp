# Enums

Enum values have their own type identity and use the width and signedness
of their declared integral representation. Named values come from Roslyn's
constant values. ARC treats enum fields and array elements as scalar values.

Enum arithmetic, bitwise operations, comparisons and conversions use the
declared representation. Switch sections compare that representation against
their constant labels. Enum values can also participate in compound updates,
including increments and decrements. Boxing requires separate support.

The verification harness checks a byte-backed enum in a struct copy and an
array, switches and compound updates, and unsigned 64-bit enum constants.
A negative sample checks the boxing diagnostic.
