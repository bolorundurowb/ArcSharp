# Nested closures and local functions

A synchronous lambda can return another lambda or call a local function. Each
enclosing invocation creates its own captured parameters and locals, and array
`foreach` creates a fresh capture cell each iteration. A variable captured at
multiple levels keeps one cell for that activation. Each closure keeps the
cell alive so its reads and writes observe the latest value.

Synchronous local functions support expression or block bodies, direct and
mutually recursive calls, delegate conversion, generic specialisation and
by-reference parameters. Capture storage supports ordinary locals, array
`foreach` iteration variables, by-value parameters and class receivers.
Capturing `using` resources, catch variables, and pattern variables is outside
the current lowering. Roslyn checks definite assignment at the call or
conversion sites. Ref returns, asynchronous or extern local functions and anonymous
methods report `ARC3001`.

The lowering follows references to local functions when discovering captures.
This lets a caller pass on a cell it never reads itself. A capturing local
function receives an ARC environment, and direct self-recursion can reuse it.
Capture-free local functions lower to static methods.

Repeated delegate conversions can allocate separate environments for one local
function. Equality and multicast removal compare the function and the
identities of its capture cells and receiver. A flag in the existing strong-slot
metadata identifies these environments.

The lifetime pass treats values stored in a closure or capture cell as escaping.
The store keeps its own strong reference so a delegate can safely outlive the
creating frame. `ARC3100` can warn when a delegate field or event and its
captures form a possible strong cycle.

[The nested closures sample](../../samples/nested_closures.cs) exercises
escaping nested lambdas, generic specialisations, mutual recursion, delegate
equality and removal, `ref` arguments and independent iteration captures.
The Windows harness checks the observed output and that all allocations are
freed. The analyser tests also cover transitive capture cycles.
