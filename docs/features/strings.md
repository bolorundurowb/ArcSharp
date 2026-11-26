# Strings and formatting

Strings hold UTF-16 code units. `Length` counts units and indexing returns a
`char`. A supplementary character occupies two units, and a string can hold
an isolated surrogate or an embedded NUL. Console output converts surrogate
pairs to UTF-8.

| API | Supported forms |
|---|---|
| String search and comparison | `Equals`, `Contains`, `IndexOf`, `StartsWith` and `EndsWith`. Matching is ordinal. `Contains` and `IndexOf` accept a string or a character. |
| String operations | `Substring(start)`, `Substring(start, length)`, `Trim()`, character and string `Replace`, `Concat` with two to four string arguments, `IsNullOrEmpty` and `IsNullOrWhiteSpace`. |
| Primitive text conversion | Parameterless `ToString()` for strings, characters, booleans, integers, floating-point values and decimals. |
| Numeric formats | Integer `D`, `X` and unqualified `G`. Floating-point `F`, `E` and `G`. The specifiers support either case and accept precision up to 99 where applicable. Formats use invariant text. |
| Interpolation and composite formatting | Ordinary interpolated strings and `string.Format` support escaped braces, field alignment and the formats above. Composite arguments can be strings, integers, floating-point values, characters, booleans or null. Pass arguments individually or in an inline array initializer. |
| `StringBuilder` | Construct empty or from a string. Append strings or primitive values, append the target platform's newline with `AppendLine`, inspect `Length`, call `Clear`, or take an immutable snapshot with `ToString()`. |

The runtime keeps the ordinary ARC header and stores the code-unit count at
byte 24, followed by the units at byte 32. UTF-16 literals are emitted as
16-bit values so an isolated surrogate survives compilation. The emitter
tracks string arguments as owned temporaries before runtime calls that can
raise an exception.

`StringBuilder` is an ArcSharp class with one strong field. Each append
replaces the field with a concatenated string. A previously returned snapshot
keeps its own reference and remains immutable when the builder changes.

The verification sample checks surrogate indexing and concatenation,
substring bounds, ordinal search and replacement, primitive and composite
formatting, builder snapshots, and exception cleanup. The harness also checks
that prebuilt composite argument arrays report `ARC3001`.
