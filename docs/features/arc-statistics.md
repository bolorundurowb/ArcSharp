# Retain and release statistics

Set `ARC_STATS=1` in the environment when running a native ArcSharp program.
After the accounting line, the runtime prints one extra line to stderr:

```text
[arc-stats] retain=R release=S
```

The `[arc] alloc=... live=...` line is unchanged. The harness matches that line
and does not read `[arc-stats]`.

## Implementation decisions

- The line is opt-in. The counters advance on every run, and only the print
  is gated, so a report from an early runtime failure still includes the
  operations performed before the failure.
- `ARC_STATS` must be the string `1`, the same rule as `ARC_CHECK`. Any other
  value leaves the line off.
- A retain is a strong-count increment after the object has been created.
  `arc_retain` counts when its argument is not null. A successful
  `arc_load_weak` counts as well, because it increments the strong count
  without calling `arc_retain`. The initial strong count of 1 on allocation
  is an allocation, already reported as `alloc`, and is not a retain.
- A release is a strong-count decrement in `arc_release` when its argument is
  not null. A null argument does not change a count, so it is not included.
- Weak retain and weak release are not included. They do not keep an object
  alive, and the lifetime pass does not remove them. The baseline is the
  strong traffic that pass is allowed to reduce.
- The line is printed after the accounting line and before the `ARC_LEAKS`
  survivor report, so the survivor report still follows the accounting line.

For a run in which every surviving object has a strong count of 1,
`release = retain + dead`. A live object whose strong count is above 1 makes
`release` smaller than that sum by the extra outstanding retains.

## Verification

The counts for every sample, taken with `ARC_CHECK=1` as well as
`ARC_STATS=1`, are the baseline in
[Verification](../verification.md#retain-and-release-baseline). The lifetime
pass has to come in below those numbers without moving a destruction point.
The harness checks `alloc`, `dead`, `freed`, and `live` on the `[arc]` line.
It does not assert the retain and release counts.
