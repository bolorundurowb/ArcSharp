# Struct storage

Struct arrays store inline values, with a stride based on the struct layout.
The array still uses the ordinary ARC header and the length, element type
and data offsets. Destroying an array drops each element's fields. The
leak reporter walks those fields through the element type's slot metadata.

Static struct fields use the same storage layout and copy operations as
struct locals and instance fields. The leak reporter finds strong references
nested inside a static struct. Struct-valued properties and indexers use
the struct calling convention. An assignment expression preserves the
original assigned value even if the setter changes its parameter copy.

The verification harness checks array stride, copies, interior references,
iteration, static storage, and a struct property setter that changes its
parameter. The negative sample checks rejection of multidimensional arrays.
