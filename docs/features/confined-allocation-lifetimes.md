# Confined allocation lifetimes

The lifetime pass can borrow every read of a method-level local initialised
with a fresh array or a fresh class instance whose constructor has an empty
lowered body or needs no call. The pass checks every use of the local. The
proof covers type tests, length reads, array element reads and reads of fields
whose types are references or scalars. The original local owns the allocation
until the same function-exit release as before. The object keeps its ARC
header, and the read of a reference element or field still yields an owned
value.

Any use outside the proof prevents this optimisation for the allocation. This
includes reassignment, address-taking, stores of the reference, and calls
that might publish it. An executable constructor also rules out the proof
because it can publish `this` before the allocation reaches the local. The
other lifetime optimisations still apply where their own proofs hold.

The `lifetime_confined` sample checks class and array reads and checks that
references stored in static slots survive the allocating call. The
verification harness checks the output and exact ARC accounting under
`ARC_CHECK=1`. See [Verification](../verification.md) for the measured traffic.
