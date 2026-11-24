# Decimal

`decimal` is a 128-bit base-ten number with a 96-bit whole mantissa, a sign, and
a scale from 0 to 28. It is meant for money and other values where `0.1 + 0.2`
must equal `0.3`.

## What works

- Literals such as `1.25m`, the constants `decimal.Zero`, `One`, `MinusOne`,
  `MaxValue` and `MinValue`, `default(decimal)` and `new decimal()`.
- `new decimal(x)` for an `int`, `uint`, `long` or `ulong`. The constructor that
  takes the low, middle and high parts, a sign and a scale is rejected with
  `ARC3001`.
- `+`, `-`, `*`, `/`, `%`, unary `-` and `+`, the six comparisons, `++`, `--`
  and compound assignment, on locals, fields, array elements and properties.
- Implicit conversions from the integer types and `char`, and explicit
  conversions to and from every integer type, `float` and `double`. A
  conversion to an integer type truncates towards zero and raises
  `OverflowException` when the value does not fit.
- `decimal?` with the same lifted rules as other nullable values.
- String concatenation, `Console.Write` and `Console.WriteLine`, and
  `ToString()` with no arguments.
- Decimal fields in classes and structs, arrays of decimal, parameters and
  return values.

## How it works

The compiler synthesises a `decimal` struct, so a decimal copies, passes and
returns like any other struct and needs no new storage rules. It has no
references, so it never takes part in ARC.

The value is two 8-byte slots. The first holds the low 64 bits of the mantissa.
The second holds the high 32 bits of the mantissa, the scale in bits 32 to 39,
and the sign in bit 63. Roslyn gives the built-in decimal operators no method
of their own, so the lowering maps each operator to a small synthesised method.
Each method is a `BoundDecimalOp` node, which the emitter turns into a call to
an `arc_dec_*` function in the runtime. The runtime does the arithmetic with
256-bit whole numbers, and rounds a result half to even until it fits 96 bits
and a scale of 28. Nothing is added to the object header.

Overflow raises `OverflowException`, and a zero divisor raises
`DivideByZeroException`. Both go through the pending-exception flag, so the
emitter checks for them after each operation that can fail and unwinds in the
usual way.

Roslyn folds an expression made only of constants, such as `256m`, before
ArcSharp sees it. The reference assembly therefore declares what the C#
compiler needs to read a decimal literal: the integer constructors and
`DecimalConstantAttribute`.

## Where the results match .NET

The `decimals` sample prints nothing but a pass line, and checks about a
hundred results against values taken from the ordinary .NET compiler running
the same source. That covers scale (`1.10m + 2.2m` is `3.30`), division to
28 digits, rounding of products, `decimal.MaxValue` and its overflow, and
conversions.

A `double` converts to the exact binary value, rounded to the largest scale up
to 28 that fits 96 bits, with trailing zeros removed. Current .NET behaves this
way, so `(decimal)0.1` is `0.1000000000000000055511151231`. Older .NET versions
rounded to 15 digits.

## Limits

- No `decimal.Parse`, `TryParse`, `Round`, `Truncate`, `Floor`, `Ceiling`,
  `Math` methods, format strings or culture-aware output. `ToString()` writes
  digits and a point.
- Calling the five-argument constructor, or any member the reference assembly
  does not declare, is rejected.
- Operations are not inlined. Each one is a call to a small method.
- The conversion to `double` follows the .NET formula, but the last bit may
  differ from .NET in rare cases.
- A `double` that a program prints uses the runtime's `%g` output, not the .NET
  shortest round-trip format. That is not specific to `decimal`.

## Verification

`samples/decimals.cs` checks the behaviours above and finishes with
`decimals PASS` and `live=0`. It runs unchanged with the ordinary .NET SDK and
prints the same line. `decimal_constructor` and `decimal_parse` are must-fail
samples. The analyser reports the five-argument constructor, and the reference
surface check confirms that arithmetic, conversions and `decimal?` compile and
that `decimal.Parse` does not.

## Defects fixed on the way

Writing the sample exposed three older defects. A struct with a nested struct field followed by another field addressed the second field at the wrong slot, because the emitter used the field index and not its byte offset. A `ulong` above `long.MaxValue` printed as a negative number, in string concatenation and in `Console.WriteLine`. A `double` literal such as `1e-5` produced invalid LLVM IR. The emitter now uses the byte offset, the runtime has `arc_str_from_uint` and `arc_console_write_uint`, and floating-point constants are written as exact hexadecimal bits.
