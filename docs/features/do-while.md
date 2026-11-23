# Post-test loops

A `do`/`while` loop enters its body before evaluating its Boolean condition.
If the condition is true, control returns to the body.

Roslyn supplies the test position on its loop operation. The bound loop
carries that position into the emitter, which reuses the existing condition
block and its ARC temporary cleanup. Definite-assignment analysis visits the
body first for a post-test loop, so assignments on every path through the body
are available in the condition and after the loop.

The verification harness runs `samples/do_while.cs` to check the first
iteration and repeated execution. It also checks that Roslyn rejects a
non-Boolean condition in `samples/negative/do_while_condition.cs`.
