# ArcSharp — Verification Results

Reproduce with: `powershell -ExecutionPolicy Bypass -File tools\verify.ps1`.
It requires the .NET 11 SDK and LLVM `clang`, by default at
`C:/Program Files/LLVM/bin/clang.exe`; `-Clang <path>` overrides the location.
See [Getting started](getting-started.md) for the prerequisites.

The harness builds the compiler with the .NET 11 SDK selected by `global.json`,
compiles each sample in `samples/` to LLVM IR, uses `clang` to lower the IR and
link it with `runtime/arc_runtime.c` into a native Windows x64 `.exe`
(`x86_64-pc-windows-msvc`), runs it, and checks both program output **and** ARC
accounting. It runs every sample with [ARC checking](features/arc-checking-mode.md)
enabled. It also compiles the must-fail samples, checks the `ARC3100` cycle
warnings, runs the analyser unit tests, and checks the reference assembly
surface. The compiler can also emit the Linux
x64 triple (`x86_64-pc-linux-gnu`); the table below is a Windows x64 run. On
Linux only the triple, data layout, and the C runtime build differ.

Each run prints `[arc] alloc=A dead=D freed=F live=L` to stderr:
`alloc` = objects created, `dead` = objects whose strong count hit 0 (destructor
ran), `freed` = allocations whose weak count reached zero, `live` = `alloc − dead` (objects
still retained at exit). **`live=0` means no leaks.**

Peak memory and collector pauses are a separate measurement. The programs in
`bench/` are compiled with ArcSharp and with the ordinary .NET runtime by
`tools\compare-gc.ps1`. The original results and the M2, M3, M4, M5 and M6 medians are in
[Comparing ArcSharp with the .NET garbage collector](features/gc-comparison.md).
The script is not part of this harness, and GitHub Actions does not run it.
The numbers depend on the machine.

## Results (79 samples, Windows x64, 23 November 2026)

`tools\verify.ps1` reported **152 passed, 0 failed** with the .NET 11.0.100 SDK:
79 sample programs, 4 leak-report checks (`ARC_LEAKS=1`), 55 must-fail
diagnostics, 2 run-time abort checks, 3 command-line checks, 5 cycle checks,
2 `ARC3200` checks, one run of the analyser unit tests, and one
reference-surface check. `live=0` means every object was reclaimed.
`byref` allocates nothing because it uses only value types, and `numerics` allocates only the strings of one concatenation check.
`cycle_strong`, `cycle_struct` and `finaliser_cycle` are the leaks the harness
asserts on purpose.

| Sample | Exercises | ARC accounting | Output |
|---|---|---|---|
| `inherit` | single inheritance, `virtual`/`override`, `: base(...)`, arrays, `for`, string concat | alloc=5 dead=5 freed=5 **live=0** | `25`, `sum=42` |
| `interface` | interface declaration and dispatch through an interface-typed variable (itable) | alloc=8 dead=8 freed=8 **live=0** | `rect=12`, `circle=75` |
| `refarray` | `T[]` of reference elements; per-element release on array teardown | alloc=7 dead=7 freed=7 **live=0** | `refarray sum=5` |
| `statics` | static fields and static methods, `int Main()` exit code | alloc=3 dead=3 freed=3 **live=0** | `count=3` |
| `fib` | recursion, `long` return, `while`, `Console.Write` | alloc=22 dead=22 freed=22 **live=0** | `fib(10)=55` |
| `floats` | `float`/`double` literals, arithmetic, comparisons, string concat | alloc=7 dead=7 freed=7 **live=0** | `area=19.6349` |
| `weak_null` | a `WeakReference<T>` whose target is destroyed, so `TryGetTarget` returns false | alloc=4 dead=4 freed=4 **live=0** | `weak-after-death is null: PASS` |
| `cycle_weak` | parent/child graph broken with `WeakReference<T>` | alloc=4 dead=4 freed=4 **live=0** | `done (weak cycle)` |
| `cycle_weakref` | the same cycle, also broken with `WeakReference<T>` | alloc=4 dead=4 freed=4 **live=0** | `done (WeakReference cycle)` |
| `weakref_get` | `WeakReference<T>.TryGetTarget(out var)` after the target died | alloc=4 dead=4 freed=4 **live=0** | `false (PASS under ARC)` |
| `cycle_strong` | the same graph with a **strong** back-reference | alloc=3 dead=1 freed=1 **live=2** | `done (strong cycle)` |
| `cycle_struct` | a **strong** cycle that runs through an inline struct field | alloc=3 dead=1 freed=1 **live=2** | `done (struct cycle)` |
| `multifile` | `mf_main.cs` and `mf_lib.cs` compiled together; `Greeter` is declared in one file and used from the other | alloc=4 dead=4 freed=4 **live=0** | `twice=42` |
| `numerics` | `byte`/`sbyte`/`short`/`ushort`/`uint`/`ulong`, unsigned arithmetic, casts, and the largest `ulong` printed by concatenation and by `Console.WriteLine` | alloc=4 dead=4 freed=4 **live=0** | `18446744073709551615` |
| `byref` | `ref`, `out`, and `in` parameters | alloc=0 dead=0 freed=0 **live=0** | `3` |
| `properties` | auto-properties | alloc=2 dead=2 freed=2 **live=0** | `Alice` |
| `isas` | `is` and `as` runtime type checks | alloc=5 dead=5 freed=5 **live=0** | `as Cat: null` |
| `covariance` | covariant store of a compatible reference element | alloc=3 dead=3 freed=3 **live=0** | `stored Dog` |
| `longconcat` | `long` and `uint` values converted to strings by concatenation; regression test for the 32-bit `long` truncation on Windows | alloc=7 dead=7 freed=7 **live=0** | `concat=20000000000 uint=3000000000` |
| `using_stmt` | `using`/`IDisposable`: declaration and expression forms, virtual and itable dispatch of `Dispose`, a `null` resource (cleanup skipped), and cleanup on the `return` path | alloc=25 dead=25 freed=25 **live=0** | `dispose a`, `tmp delete`, `db closed`, `disposals=3` |
| `structs` | struct code generation: value semantics, reference fields, `ref`/`out`, structs inside classes, nested structs, and a temporary struct method call | alloc=66 dead=66 freed=66 **live=0** | `a.sum=5`, `card=card copy tags=1,2`, `temp=42` |
| `boolprint` | `Console.WriteLine` of a false comparison, and concatenation of `bool` values | alloc=9 dead=9 freed=9 **live=0** | `cmp=False/True direct=False` |
| `unicode` | `\u` and `\U` escapes in a string and a character literal | alloc=9 dead=9 freed=9 **live=0** | `A`, `letter=A match=True bytes=4` |
| `overloads` | static and instance methods that share a name and an arity, chosen by argument type | alloc=19 dead=19 freed=19 **live=0** | `int=7 str=ok 4 4` |
| `charprint` | `Console.WriteLine` of a `char`, and concatenation of a `char` | alloc=5 dead=5 freed=5 **live=0** | `concat=B direct=A` |
| `staticprop` | a static auto-property, read and written | alloc=3 dead=3 freed=3 **live=0** | `count=4` |
| `leaks` | a static root, an unreachable class cycle, and an unreachable cycle through a reference array. With `ARC_LEAKS=1` the report marks `Node` as `root=static` and the others as `root=unreachable`, and prints the `Parent`/`Child` and `<ref[]>`/`Cell` cycles | alloc=7 dead=1 freed=1 **live=6** | `leaks reported` |
| `generics` | two-file generic classes/interfaces, generic bases and virtual dispatch, methods and inference, recursion, independent statics, arrays and weak references as arguments, constraints, and primitive default construction | alloc=72 dead=72 freed=72 **live=0** | `generic=42,hello`, `dispatch=7,7`, `infer=11,hello,9`, `owners=after,hello,made`, `counts=2,1`, `arrays=11,array`, `weak=made`, `types=True,True`, `generics PASS` |
| `generic_dispatch` | interface, `virtual`, `abstract` and `override` generic methods, a generic owner, a constrained call, and an instantiation first reached inside an override | alloc=31 dead=31 freed=31 **live=0** | `generic dispatch PASS` |
| `static_abstract` | `static abstract` and `static virtual` members called through a type parameter: a method, operators, a property with a setter, and a default body, for a class and a struct | alloc=21 dead=21 freed=21 **live=0** | `static abstract PASS` |
| `generic_inference` | Roslyn inference for widening, base and derived arguments, array elements, nested generic types, `out` arguments, a constraint on another type parameter, and the non-generic overload | alloc=43 dead=43 freed=43 **live=0** | `widened=2.5`, `overload=int,generic,generic`, `generic inference PASS` |
| `generic_structs` | specialised struct copies, reference fields, nested wide values, managed-reference generic methods, and class field offsets | alloc=28 dead=28 freed=28 **live=0** | `copies=first,second,wide`, `layout=20,30,99,40` |
| `owners` | interior references stay valid when the callee drops the named owner: a field, an array element reached through a forwarded `ref`, a nested reference, a struct method's `this`, a static `ref`, an `in` read, and a constructor `ref` | alloc=33 dead=33 freed=33 **live=0** | `owners=4,7,5,6,5,2` |
| `destruction_points` | two strong owners cleared in separate statements | alloc=3 dead=3 freed=3 **live=0** | `destruction points PASS` |
| `m2_dispatch_logic` | abstract dispatch, string equality and short-circuit temporaries | alloc=6 dead=6 freed=6 **live=0** | `dispatch and logic PASS` |
| `m2_initialisers` | instance and static field initialisers, implicit base construction and constructor delegation | alloc=7 dead=7 freed=7 **live=0** | `initialisers PASS` |
| `m2_properties_narrowing` | get-only assignment, inherited auto-properties, narrow casts and arrays | alloc=5 dead=5 freed=5 **live=0** | `properties and narrowing PASS` |
| `lifetime_reads_and_moves` | stable reads, last-use moves, early return and a by-reference alias | alloc=10 dead=10 freed=10 **live=0** | `lifetime reads and moves PASS` |
| `lifetime_confined` | confined class and array reads, a stored local and publication from a constructor | alloc=6 dead=6 freed=6 **live=0** | `confined lifetimes PASS` |
| `finaliser_order` | a four-level chain finalises derived before base, and a parent before its children, checked against an exact log | alloc=40 dead=40 freed=40 **live=0** | `finaliser order PASS` |
| `finaliser_release_point` | a finaliser runs at the statement that drops the last reference, not at the end of the method | alloc=17 dead=17 freed=17 **live=0** | `finaliser release points PASS` |
| `finaliser_suppress` | `GC.SuppressFinalize` from `Dispose`, a finaliser that still runs for an object that was not disposed, and `GC.Collect` and `GC.WaitForPendingFinalizers` as no-ops | alloc=8 dead=8 freed=8 **live=0** | `suppress finalizer PASS finalised=1` |
| `finaliser_graph` | weak references read as dead inside a finaliser, a child released by a finaliser, array elements in index order, and a generic class | alloc=55 dead=55 freed=55 **live=0** | `finaliser graph PASS` |
| `finaliser_deep` | a list of 100,000 nodes, which overflowed the stack under the recursive release | alloc=100006 dead=100006 freed=100006 **live=0** | `deep graph finalised 100000` |
| `finaliser_cycle` | a strong cycle never reaches zero, so its finalisers never run, and the leak is deliberate | alloc=5 dead=3 freed=3 **live=2** | `cycle finalised 0` |
| `exceptions_basic` | `try`, `catch`, `finally`, a `throw` caught in the same method and one caught across a call, a runtime division by zero, and a catch clause that does not match | alloc=16 dead=16 freed=16 **live=0** | `outer caught inner` |
| `exceptions_unwind` | cleanup on every unwind path: argument temporaries, locals across frames, a throwing constructor, a store that throws, a concatenation that throws, `return` through `finally`, nested `finally`, rethrow, a `finally` that replaces an exception, and a thrown stored exception | alloc=97 dead=97 freed=97 **live=0** | `exceptions unwind PASS` |
| `exceptions_runtime` | each runtime check raises a catchable exception: index out of range, invalid cast, null field read and write, null call, null `.Length`, null element, divide and remainder by zero, `long` division, overflow, array type mismatch, `throw null`, and a base-class catch | alloc=53 dead=53 freed=53 **live=0** | `exceptions runtime PASS` |
| `variance` | Covariant and contravariant slots, generic defaults, and unmanaged, notnull, enum and nullable reference constraints | alloc=5 dead=5 freed=5 **live=0** | `variance PASS` |
| `nullable_values` | Lifted operations, nullable conversions and coalescing, presence tests, and a nullable struct's owned reference | alloc=4 dead=4 freed=4 **live=0** | `nullable values PASS` |
| `operator_updates` | Operator results for struct and reference updates, single index evaluation and failed covariant stores | alloc=9 dead=9 freed=9 **live=0** | `operator updates PASS` |
| `conditional_values` | Struct and reference results, overloaded short circuiting, switch expressions and exception cleanup | alloc=10 dead=10 freed=10 **live=0** | `conditional values PASS` |
| `patterns` | Single evaluation, numeric and string patterns, bindings, short circuiting, switch case guards, property and positional patterns, array list patterns with a slice, interface membership and dispatch, and decimal and nullable decimal comparisons | alloc=6 dead=6 freed=6 **live=0** | `patterns PASS` |
| `struct_interfaces` | Boxing with owned reference fields, constrained dispatch, checked unboxing and struct printing | alloc=9 dead=9 freed=9 **live=0** | `struct interfaces PASS` |
| `struct_storage` | Inline arrays, interior references, static struct fields, and property assignment copies | alloc=5 dead=5 freed=5 **live=0** | `struct storage PASS` |
| `struct_calls` | Argument snapshots, interface calls, result copies before finally, and ARC cleanup when a later argument throws | alloc=6 dead=6 freed=6 **live=0** | `struct calls PASS` |
| `base_interfaces` | Diamond inheritance, explicit implementations and inherited virtual overrides | alloc=2 dead=2 freed=2 **live=0** | `base interfaces PASS` |
| `enums` | Enum widths, struct copies, arrays, switches, compound updates and unsigned constants | alloc=2 dead=2 freed=2 **live=0** | `enums PASS` |
| `static_constructor` | First-use initialisation, argument order, recursive access, and cached failure with its cause | alloc=11 dead=11 freed=11 **live=0** | `static constructor PASS` |
| `accessors` | Property and indexer bodies, interface and virtual dispatch, a property initialiser and assignment results | alloc=4 dead=4 freed=4 **live=0** | `accessors PASS` |
| `accessor_updates` | `++`, `--` and compound updates through accessor bodies, one getter and setter call per update, string and operator updates, virtual, static and interface properties | alloc=13 dead=13 freed=13 **live=0** | `accessor updates PASS` |
| `extensions` (two files) | Extension methods on a class, `int`, `string` and a struct, chained calls, and a partial class split across files | alloc=15 dead=15 freed=15 **live=0** | `extensions PASS` |
| `nullable_updates` | Lifted compound and increment updates on locals, fields and array elements, user-defined lifted operators and conversions, and absent operands | alloc=3 dead=3 freed=3 **live=0** | `nullable updates PASS` |
| `nullable_struct_boxing` | Boxing a nullable struct to `object` and an interface, `as` and casts back, absent values and unboxing failures | alloc=6 dead=6 freed=6 **live=0** | `nullable struct boxing PASS` |
| `decimals` | `decimal` scale and rounding, remainder, limits and overflow, comparisons, conversions to and from integers, `double` and `float`, updates, class and struct members, arrays and `decimal?` | alloc=292 dead=292 freed=292 **live=0** | `decimals PASS` |
| `object_initialisers` | Member initialisers, nested initialisers and evaluation order | alloc=24 dead=24 freed=24 **live=0** | `object initialisers PASS` |
| `records` | Record classes: positional members, equality, hashing, formatting, `with`, deconstruction and inheritance | alloc=197 dead=197 freed=197 **live=0** | `records PASS` |
| `arguments` | Named evaluation order for methods and constructors, defaults, parameter arrays and cleanup when an array element throws | alloc=9 dead=9 freed=9 **live=0** | `arguments PASS` |
| `operators_members` | Direct operator overloads, conversion symbols, expression bodies, constants, readonly fields and nested names | alloc=4 dead=4 freed=4 **live=0** | `operators and members PASS` |
| `array_foreach` | Single evaluation, numeric conversion and reference iteration with early exits | alloc=5 dead=5 freed=5 **live=0** | `array foreach PASS` |
| `checked_arithmetic` | Checked integral overflow and conversions, catchable exceptions, and nested unchecked arithmetic | alloc=13 dead=13 freed=13 **live=0** | `checked PASS` |
| `bitwise` | Eager Boolean operators, masked shifts, signed and unsigned shifts, and compound updates | alloc=1 dead=1 freed=1 **live=0** | `bitwise PASS` |
| `numeric_compound` | One evaluation of an update target, numeric promotion, and auto-property updates including string concatenation | alloc=10 dead=10 freed=10 **live=0** | `compound PASS` |
| `increment` | Prefix and postfix results, single array-index evaluation, narrowing, and floating point | alloc=2 dead=2 freed=2 **live=0** | `increment PASS` |
| `switch_statement` | String and numeric labels, default dispatch, and continue through a switch inside a loop | alloc=4 dead=4 freed=4 **live=0** | `switch PASS` |
| `loop_branches` | Nested loop targets, for updates, and cleanups on break and continue | alloc=3 dead=3 freed=3 **live=0** | `loop branches PASS` |
| `do_while` | Body runs before the condition and repeats until it becomes false | alloc=1 dead=1 freed=1 **live=0** | `do while PASS` |
| `exceptions_using` | `Dispose()` while an exception unwinds, a `Dispose()` that throws after the body and during an unwind, `return` from `using`, and `using` inside `try` with `finally` | alloc=74 dead=74 freed=74 **live=0** | `exceptions using PASS` |
| `exceptions_finaliser` | a finaliser that throws is reported and ignored, the object's children are still finalised, an exception already unwinding survives it, and a finaliser that catches its own exception | alloc=32 dead=32 freed=32 **live=0** | `exceptions finaliser PASS` |

The two new generic samples were also built and run with the normal .NET 8
compiler, producing the same stdout. The generic identity example in the
language reference was compiled and run with ArcSharp: output `42`, with
`alloc=0 dead=0 freed=0 live=0`. Emitted IR was inspected for distinct closed
type descriptors, destructors, method specialisations, and inline struct sizes.
The generic linked-node type produces the expected conservative `ARC3100`
warning; its sample creates no cycle and exits with `live=0`.

With `ARC_LEAKS=1`, the harness checks the survivor report for `leaks`,
`cycle_strong`, `cycle_struct`, and `cycle_weak`. The two strong-cycle programs
name an unreachable cycle (`Parent -> Child -> Parent`, and
`Owner -> Owner -> Owner` through the struct field). `cycle_weak` prints no
survivor lines. The accounting totals above are unchanged when the report is
off.

## M2 retain and release baseline

`ARC_STATS=1` prints `[arc-stats] retain=R release=S` after the accounting
line. A retain is a strong-count increment after allocation, and a release is
a strong-count decrement. The harness does not assert these counts. They are
the baseline the lifetime pass has to come in under. See
[Retain and release statistics](features/arc-statistics.md).

The counts below were taken on 21 November 2026 with `ARC_CHECK=1` and
`ARC_STATS=1`, on the same Windows x64 path as the table above. For every
sample except `leaks`, `release = retain + dead`. In `leaks` the shared
`Cell[]` is stored in both cells, so that array still has a strong count of 2
and the release count is one below `retain + dead`. The other five survivors
in that run have a strong count of 1.

| Sample | Retains | Releases |
|---|---:|---:|
| `inherit` | 17 | 22 |
| `interface` | 11 | 19 |
| `refarray` | 26 | 33 |
| `statics` | 0 | 3 |
| `fib` | 0 | 22 |
| `floats` | 0 | 7 |
| `weak_null` | 7 | 11 |
| `cycle_weak` | 7 | 11 |
| `cycle_weakref` | 7 | 11 |
| `weakref_get` | 7 | 11 |
| `cycle_strong` | 7 | 8 |
| `cycle_struct` | 6 | 7 |
| `multifile` | 1 | 5 |
| `numerics` | 0 | 0 |
| `byref` | 0 | 0 |
| `properties` | 11 | 13 |
| `isas` | 7 | 12 |
| `covariance` | 3 | 6 |
| `longconcat` | 0 | 7 |
| `using_stmt` | 22 | 47 |
| `structs` | 18 | 84 |
| `boolprint` | 0 | 9 |
| `unicode` | 0 | 9 |
| `overloads` | 4 | 23 |
| `charprint` | 0 | 5 |
| `staticprop` | 0 | 3 |
| `leaks` | 21 | 21 |
| `generics` | 141 | 213 |
| `generic_structs` | 16 | 44 |
| `owners` | 29 | 62 |
| `destruction_points` | 10 | 13 |
| `m2_dispatch_logic` | 7 | 13 |
| `m2_initialisers` | 21 | 28 |
| `m2_properties_narrowing` | 12 | 17 |

## M3 retain and release counts

The 34 existing samples retained their exact M2 accounting and output
checks. Retains across those samples fell from 418 to 369
and releases fell from 799 to 750. Samples with changed ARC traffic
are listed below. The reads-and-moves regression recorded 18 retains and 28
releases. The confined-allocation regression recorded 8 retains and 14
releases. Its generated `ReadConfined` method contains only the two
function-exit releases for its owners, and `Escape` retains and releases
its read temporaries through ordinary ARC. The M2 destruction-point sample
still observed destruction at the same statements.

| Sample | M2 retains | Current retains | M2 releases | Current releases |
|---|---:|---:|---:|---:|
| `inherit` | 17 | 12 | 22 | 17 |
| `interface` | 11 | 9 | 19 | 17 |
| `refarray` | 26 | 18 | 33 | 25 |
| `weak_null` | 7 | 6 | 11 | 10 |
| `weakref_get` | 7 | 6 | 11 | 10 |
| `multifile` | 1 | 0 | 5 | 4 |
| `properties` | 11 | 7 | 13 | 9 |
| `isas` | 7 | 6 | 12 | 11 |
| `overloads` | 4 | 1 | 23 | 20 |
| `generics` | 141 | 123 | 213 | 195 |
| `m2_dispatch_logic` | 7 | 4 | 13 | 10 |
| `m2_properties_narrowing` | 12 | 10 | 17 | 15 |

## M4 retain and release counts

These counts were taken on 23 November 2026 with `ARC_CHECK=1` and
`ARC_STATS=1`, for the samples added by the finalisers milestone. The existing
samples kept the accounting in the results table above. The harness does not
assert these counts.

| Sample | Retains | Releases |
|---|---:|---:|
| `finaliser_order` | 36 | 76 |
| `finaliser_release_point` | 13 | 30 |
| `finaliser_suppress` | 13 | 21 |
| `finaliser_graph` | 62 | 117 |
| `finaliser_deep` | 499,998 | 600,004 |
| `finaliser_cycle` | 6 | 9 |

## M5 retain and release counts

These counts were taken on 23 November 2026 with `ARC_CHECK=1` and
`ARC_STATS=1`, for the samples added by the exceptions milestone. For each
sample, `release = retain + dead`. The harness does not assert these counts.

| Sample | Retains | Releases |
|---|---:|---:|
| `exceptions_basic` | 30 | 46 |
| `exceptions_unwind` | 169 | 266 |
| `exceptions_runtime` | 22 | 75 |
| `exceptions_using` | 123 | 197 |
| `exceptions_finaliser` | 38 | 70 |

## Diagnostics

Diagnostics print with a stable code and a severity, for example
`(0,0): error ARC2050: no static 'Main()' entry point found`. Compilation fails
when the error count is non-zero. Warnings, including definite-assignment
`ARC2100` and cycle `ARC3100`, do not fail the build unless `-Werror` is
passed, so the samples above still pass.

The harness also compiles 55 programs that must fail (`samples/negative/`) and
checks the expected code: `ARC3001` for unsupported constructs, including ref
locals, ref returns, shift operators, exception filters, throw expressions and
`GC.ReRegisterForFinalize`, `CS0029` for throwing a value that is not an
exception, `CS1009`
for an invalid Unicode escape, `ARC2011` for an invalid `using` resource,
`ARC2012` for `new` on an abstract class, `ARC2013` for expanding
specialisations, `ARC2015` for a write through an `in` parameter, and
`ARC2016` for four forms of a finaliser that stores `this`. Missing generic definitions, wrong arity, and
unsatisfied constraints report Roslyn `CS` codes. The harness also
checks that `-o` with no value, and a missing `clang` or `llc`, are reported
as errors and exit with code 2. It checks
`ARC3100` in both directions: `cycle_strong`, `cycle_struct` and
`finaliser_cycle` warn, while `cycle_weak` and `cycle_weakref` stay clean.

A finaliser that hides a resurrection behind a method call cannot be seen by
the compiler. `finaliser_resurrect_call` compiles, and the harness checks that
running it exits with code 70, prints `ResurrectionException` on stderr, and
never reaches the line that follows the release. The harness also checks the
info diagnostic `ARC3200`: `finaliser_suppress` calls `GC.Collect` and reports
it, and `finaliser_order` makes no `GC.Collect` call and stays clean.

An exception that nothing catches ends the program. `negative/exception_unhandled`
compiles, and the harness checks that running it exits with code 70, prints
`Unhandled exception. System.InvalidOperationException: nobody catches this`
on stderr, never reaches the line that follows the call, and prints the
complete line `[arc] alloc=4 dead=4 freed=4 live=0`.

## Analyser unit tests

`analyzers/ArcSharp.Analyzers.Tests` is an xUnit project that compiles snippets
in memory with Microsoft.CodeAnalysis and runs the analyser. It has 74 cases:
44 unsupported constructs that must report `ARC3001`, 18 supported programs
that must stay clean, 9 cycle checks for `ARC3100` (mutual and self
references, arrays, statics, struct bridges, and weak references that break the
cycle), and 3 checks for `ARC3200` (two calls that must report it, and a
program whose `GC` calls the runtime honours that must not). `tools/verify.ps1`
runs `dotnet test` and counts the whole run as one check.

## The headline result: ARC + cycle breaking

`cycle_strong` and `cycle_weak` are the same parent and child graph. The
back-reference is the only difference:

```
cycle_strong:  public Parent owner;                         ->  alloc=3 dead=1 live=2   (LEAK)
cycle_weak:    public WeakReference<Parent> owner;         ->  alloc=4 dead=4 live=0   (reclaimed)
```

This is exactly the fundamental ARC trade-off made concrete: pure reference
counting cannot reclaim a strong cycle (two objects leak), and
`WeakReference<T>` breaks the cycle so deterministic reclamation succeeds.
The extra allocation is the `WeakReference<T>` object. `cycle_weakref` is the
same shape (alloc=4 dead=4 live=0).
`cycle_struct` shows that a cycle through an inline struct field leaks in the
same way, and that `ARC3100` sees the class reference inside the struct. The
leaks in `cycle_strong` and `cycle_struct` are **expected and asserted** by the
harness — they document the limitation rather than hiding it.

## Roslyn compatibility (idiomatic C#)

Because cycle-breaking uses the standard `System.WeakReference<T>` type rather
than a custom keyword, ArcSharp source is also valid C# that the normal toolchain
accepts. This was checked on 10 November 2026 with the .NET 8.0.427 SDK: each
sample was copied into a new `net8.0` console project as `Program.cs` and run
with `dotnet run`.

| Sample | Roslyn build | Output under .NET | Output under ArcSharp |
|---|---|---|---|
| `cycle_weakref` | Succeeded | `done (WeakReference cycle)` | `done (WeakReference cycle)`, `live=0` |
| `weakref_get` | Succeeded | `target v=99` | `TryGetTarget after death: false (PASS under ARC)` |
| `interface` | Succeeded | `rect=12`, `circle=75` | `rect=12`, `circle=75` |

`weakref_get` shows the intended behavioural difference. It calls
`TryGetTarget` right after the target's last strong reference is dropped.
ArcSharp returns `false` immediately, because destruction is deterministic.
Under .NET the target is still in memory because no collection has run, so the
call returns `true`. That divergence is the point of the project.
`cycle_weak` and `weak_null` use `WeakReference<T>` as well.

## Windows x64 target check

Every module the compiler writes with `--target windows` (the default) starts
with:

```
target triple = "x86_64-pc-windows-msvc"
```

`clang` replaces it with the exact MSVC version it finds, which is the source
of the expected `-Woverride-module` warning. The harness builds every sample
through that path, so each passing row in the results table is also a
successful Windows x64 link and run.
