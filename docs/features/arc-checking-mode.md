# ARC checking mode

Set `ARC_CHECK=1` in the environment when running a native ArcSharp program.
An ARC violation prints an `[arc-check]` diagnostic with the operation and
reason to stderr and exits with code 70.

## Implementation decisions

- The runtime checks registry membership before reading an object header.
  The registry already records allocations for `ARC_LEAKS=1`, so checking
  can share its registration and removal paths. Allocation of the registry
  record must succeed when checking is enabled.
- Retain and release require a positive strong count. Checking before the
  decrement catches a release at zero before it underflows. A weak load of
  a tracked object with a strong count of zero returns null because an
  outstanding weak reference keeps the allocation valid.
- When the last weak reference goes away, the runtime removes the allocation
  from the registry and holds it in quarantine until process exit. This
  prevents a reused address from making a stale pointer appear valid. The
  trade-off is higher peak memory use and a linear registry lookup on
  checked operations.
- Destruction still runs at the last strong release. The `freed` counter
  advances when the weak count reaches zero, which is when the unchecked
  runtime frees the allocation. This keeps the accounting comparable and
  excludes quarantined allocations from the survivor report.

## Verification

`tools/verify.ps1` enables checking for every executed sample, including
the runs with `ARC_LEAKS=1`. It checks observable output and the expected
`alloc`, `dead`, `freed`, and `live` counts, and rejects any `[arc-check]`
diagnostic.

The checked `isas` sample exposed a double release on a successful `as`
cast. The emitter now retains the selected result so the receiving slot
owns a reference after statement cleanup releases the operand.

A native probe also verified the traps for retaining and releasing a dead
object, a negative strong count, and a weak load after retirement. A valid
weak load after destruction returned null and ended with `live=0`.
