# Updates through accessors

`++`, `--` and compound assignments such as `+=` now work on any property or
indexer that has both a getter and a setter. This includes accessors with
bodies, `virtual` and `override` members, static properties, and members called
through an interface. Auto-properties without a body still use their backing
field directly, as described in [Numeric updates](increment.md).

## How an update runs

An update has no address to modify, so the compiler rewrites it into plain
steps. For `ledger[i] += v` the steps are:

1. Evaluate the receiver (`ledger`) and each index (`i`) once, and keep them.
2. Call the getter and store the result in a hidden local.
3. Apply the ordinary update to that local. This reuses the existing
   numeric, string, and overloaded-operator code, so checked arithmetic,
   narrow integer wrapping, and `string +=` behave as they do elsewhere.
4. Call the setter with the hidden local.
5. Yield the result: the updated value for a prefix form or a compound
   assignment, and the previous value for a postfix form.

The getter and the setter each run exactly once, and the receiver and the
index arguments are never evaluated twice. This is the C# rule, and the
verification sample checks it with call counters.

## Decisions

- **One new bound node.** `BoundSequence` evaluates a list of effects in order
  and then yields a value. It is small, and the emitter treats each effect as
  an expression statement: a reference result becomes a temporary that is
  released at the end of the full statement. The hidden locals follow the
  ordinary rules, so they are released when the method exits.
- **Struct receivers are rejected.** Holding a struct receiver in a temporary
  would copy it, so the setter would change the copy and the update would be
  lost silently. An update through a property of a struct local or field
  still reports `ARC3001` when the property has accessor bodies.
- **Named and `ref` indexer arguments are rejected.** They are rare for
  indexers, and capturing them once needs more machinery than the benefit
  justifies.

## Verification

The sample `accessor_updates` checks prefix and postfix results, one getter and
one setter call per update, a receiver and an index that are evaluated once, string
and overloaded-operator updates, a `virtual` property, a static property, an
interface property, and updates inside the declaring class. It ends with
`live=0`. The must-fail sample `accessor_update_struct` checks the struct
receiver rejection.
