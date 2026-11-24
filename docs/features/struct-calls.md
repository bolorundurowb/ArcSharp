# Struct parameters and return values

A by-value struct argument is copied when that argument is evaluated. The
callee receives the copy, so changes to it do not affect the caller's
original value. A later argument can change the original without changing
the already evaluated copy.

A method returning a struct receives a hidden result address. It copies the
return value before running any `finally` cleanups. Direct, virtual and
interface calls use the same convention. The copy retains reference fields
and is registered for cleanup if argument evaluation, the callee or a
cleanup throws.

The verification harness checks evaluation order across an aliased argument,
a `finally` that changes a returned local, interface dispatch, and cleanup
after a later argument throws. The must-fail sample checks Roslyn's
diagnostic for returning an unassigned local.
