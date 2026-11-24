# Patterns and switch guards

The `is` operator and switch statements accept constant, relational,
declaration and type patterns in the supported numeric and reference
representations. Patterns compose with `and`, `or` and `not`. A `var`
binding can capture null, and a declaration pattern binds its local only
on a successful match.

Interface type and declaration patterns on reference inputs test the
runtime type, including inherited interfaces and marker interfaces. The
compiler reserves interface table entries for membership so a match does
not depend on the interface having any callable methods. A successful
declaration pattern can use the bound value for interface dispatch.

Decimal constant and relational patterns use the same exact comparison as
decimal operators. A nullable decimal must contain a value to match either
kind of pattern.

## Recursive patterns

A recursive pattern tests a type and then examines members of the matched
value. Property subpatterns read a field or a getter-only or auto-property
without arguments, such as `{ Count: 4, Limit: > 7 }`. A positional
subpattern calls an instance `Deconstruct` method, such as `Tag(4, > 7)`. Each
`out` value is held in a hidden local, and its subpattern is tested in order.
A designation after the pattern (`Tag(4, _) shape`) binds the matched value.
Tuple deconstruction, extension `Deconstruct` methods and indexer subpatterns are
not supported and report `ARC3001`.

## List patterns

A list pattern matches a single-dimensional array. The array must not be null,
and its length must equal the number of element patterns. A slice pattern
(`..`) lets the length be larger, and `.. var middle` binds a new array that
holds the elements the slice covers. The slice is a copy, so the new array
owns its own references: for a reference-element array each element is
retained, and for a struct-element array the structs are copied. The runtime
function `arc_array_slice_new` allocates it with the same element type as
the source. A list pattern on any other type, such as a user class with an
indexer, is reported as `ARC3001`.

## How tests run

Pattern lowering captures the input once. In a switch, each test and its
optional `when` guard run in order. A false guard lets the next case run.
The emitter keeps the captured input alive during dispatch and drops its
temporary ownership before entering a section.

The verification harness checks numeric and string patterns, short circuiting,
class and null bindings, and a failed guard followed by a matching case. It
also checks a property subpattern, a positional `Deconstruct` pattern with a
binding, and a list pattern with a slice, all with `live=0`. The must-fail
samples `list_pattern` and `switch_pattern` check that a list pattern on a
user class is rejected. The surface checker confirms that Roslyn accepts list
patterns against the reference assembly.
See [Conditional values](conditional-values.md) for switch expressions.
