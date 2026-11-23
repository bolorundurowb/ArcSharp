# Post-test loops, loop branches, and array iteration

A `do`/`while` loop enters its body before evaluating its Boolean condition.
If the condition is true, control returns to the body.

`break` exits the innermost loop or switch. `continue` runs the update in
a `for` loop and tests the condition in a `while` or `do`/`while` loop.
Leaving a `using` or `try` with a `finally` runs its cleanup before branching.

Roslyn supplies the test position on its loop operation. The bound loop
carries that position into the emitter, which reuses the existing condition
block and its ARC temporary cleanup. The emitter records the exception
region depth at loop entry, so a branch runs the cleanups for regions it
exits. Definite-assignment analysis joins the paths that reach the test or
exit and includes assignments made by a `finally` on those paths.

An array `foreach` evaluates its source once. Lowering saves the array in an
owned local and emits a counted loop over its elements. Reference iteration
variables release at the end of each iteration, and the array releases when
control leaves the loop, including on exceptions. The compiler accepts
single-dimensional arrays with supported numeric or reference conversions.

The verification harness checks single evaluation, element conversion and
early exits. It rejects iteration over a string and exercises repetition, nested loops, `for` updates
and cleanups. It checks that Roslyn rejects a non-Boolean post-test condition
and a `break` outside a loop or switch.
