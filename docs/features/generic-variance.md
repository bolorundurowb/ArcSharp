# Generic interface variance and constraints

Generic interfaces can declare covariant and contravariant reference type
arguments. ArcSharp keeps each closed interface's selectors distinct and
fills compatible target selectors with the corresponding implementation.
An exact implementation keeps its slot when a class also exposes a
variance-compatible interface.

Roslyn validates `unmanaged`, `notnull` and `class?` constraints. The reference
assembly provides the metadata type Roslyn needs to emit the unmanaged
constraint. Closed generic bodies still use their specialised layouts.
`default(T)` produces the zero or null value of the closed type.

The verification harness calls an implementation through both covariant and
contravariant views and exercises the additional constraint forms. The
negative samples check invalid variance and a managed field in a type
argument to an unmanaged constraint.
