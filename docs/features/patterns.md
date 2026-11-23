# Patterns and switch guards

The `is` operator and switch statements accept constant, relational,
declaration and type patterns in the supported numeric and reference
representations. Patterns compose with `and`, `or` and `not`. A `var`
binding can capture null, and a declaration pattern binds its local only
on a successful match.

Pattern lowering captures the input once. In a switch, each test and its
optional `when` guard run in order. A false guard lets the next case run.
The emitter keeps the captured input alive during dispatch and drops its
temporary ownership before entering a section.

The verification harness checks numeric and string patterns, short circuiting,
class and null bindings, and a failed guard followed by a matching case.
See [Conditional values](conditional-values.md) for switch expressions.
