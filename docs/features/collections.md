# Collections

ArcSharp provides `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`,
`Queue<T>` and `Stack<T>` in the `System.Collections.Generic` namespace. They
are written in C# under `corelib/Collections/`, and the compiler adds them to
every program in the same way as the exception classes.

```csharp
var scores = new Dictionary<string, int>();
scores["ada"] = 3;
scores["ada"] = 4;                      // replaces the value

var seen = new HashSet<Point>();
seen.Add(new Point(1, 2));              // record keys compare by value

var pending = new Queue<Job>();
pending.Enqueue(job);
Job next = pending.Dequeue();
```

## Surface

| Type | Members |
|---|---|
| `List<T>` | `Count`, indexer, `Add`, `Insert`, `Remove`, `RemoveAt`, `Clear`, `Contains`, `IndexOf`, `ToArray`, and the constructor with a capacity |
| `Dictionary<TKey, TValue>` | `Count`, indexer, `Add`, `ContainsKey`, `TryGetValue`, `Remove`, `Clear`, and the constructor with a capacity |
| `HashSet<T>` | `Count`, `Add`, `Contains`, `Remove`, `Clear` |
| `Queue<T>` | `Count`, `Enqueue`, `Dequeue`, `Peek`, `Clear` |
| `Stack<T>` | `Count`, `Push`, `Pop`, `Peek`, `Clear` |

All five types support generic `foreach` and expose `GetEnumerator()`.
See [Collection enumeration](collection-enumeration.md) for ordering, mutation
and ARC lifetime behaviour.

## How elements are compared and hashed

The hash tables need two operations on the element type: equality and a hash
code. The compiler supplies both through `ElementEquality.AreEqual<T>` and
`ElementEquality.GetHash<T>`. These are not ordinary methods. The lowering
replaces each call with code for the closed element type, so the same corelib
source works for `int`, `string`, a class, and a struct.

| Element type | Equality | Hash code |
|---|---|---|
| `int`, `short`, `byte`, `char`, `bool`, enums | Value | The value |
| `long`, `ulong` | Value | The high and low 32-bit halves, XORed |
| `string` | Ordinal UTF-16 comparison | FNV-1a over the code units. A null string hashes to 0 |
| Class without `Equals` or `GetHashCode` overrides | Reference identity | Identity hash |
| Class that overrides `Equals(object)` | The override | The `GetHashCode` override |
| Record class | Record `Equals` | Record `GetHashCode` |
| Struct | Its own `Equals(T)` method | Its own `GetHashCode()` method |
| Array | Reference identity | Identity hash |

A struct has no reference identity to fall back on, so it must declare both
`Equals(T)` and `GetHashCode()`. Without them the use is rejected with
`ARC3001`, for example:

```text
error ARC3001: struct element type 'Plain' must declare Equals(Plain) to be used in a collection
```

A class that overrides `Equals(object)` must also override `GetHashCode`, as
in .NET. Without the override, two equal objects can hash differently and land
in different buckets, so a lookup can miss them. The compiler does not check
this yet.

Floating-point elements (`float`, `double`), `decimal`, delegates and `object`
are rejected with `ARC3001`. Floating-point equality needs a decision first,
because `==` and the .NET default comparer disagree on `NaN`. An `object` key
would need a dispatch on the runtime type of each key, which the intrinsic does
not do.

A record's string member contributes its length to the record hash, as
described in [Record classes](records.md#hashing). Two records that differ
only in the text of a same-length string share a bucket. Equality still
compares the full text, so the result is correct, only slower.

### Why the compiler does the work

`IEquatable<T>` would be the usual constraint, but ArcSharp rejects that
library interface, and the surface has no `IEquatable<int>` for the primitive
types. Calling `Object.GetHashCode` or `Equals(object)` directly is also
rejected. The intrinsic lets the collection code stay ordinary C# while the
compiler picks the comparison that each element type needs.

## Null elements and keys

`null` is a valid element and a valid key. It is equal only to `null`, and it
hashes to 0 (for a class or a string). This differs from .NET, where
`Dictionary<TKey, TValue>` throws `ArgumentNullException` for a null key.

## Removal releases references at once

Each collection drops its reference at the statement that removes it. That
statement is the last strong release, so a finaliser runs there and a
`WeakReference<T>` to the element reports `false` straight after.

| Operation | Element released |
|---|---|
| `List<T>.RemoveAt`, `List<T>.Remove`, `List<T>.Clear` | The removed element or each element |
| `Dictionary<TKey, TValue>` indexer set on an existing key | The old value |
| `Dictionary<TKey, TValue>.Remove`, `Clear` | The removed key and value, or each key and value |
| `Queue<T>.Dequeue`, `Stack<T>.Pop` | The returned element is transferred to the caller, so it is released when the caller's last reference goes |
| `Queue<T>.Clear`, `Stack<T>.Clear`, `HashSet<T>.Remove`, `HashSet<T>.Clear` | The removed or cleared elements |

A removed slot is set to its default value, so a reused backing array holds no
stale reference.

## Exceptions

| Situation | Exception |
|---|---|
| `Dictionary` indexer get on a missing key | `KeyNotFoundException` |
| `Dictionary.Add` with a key that is already present | `ArgumentException` |
| `List` index outside the range | `ArgumentOutOfRangeException` |
| `Pop`, `Peek` or `Dequeue` on an empty collection | `InvalidOperationException` |
| A negative capacity | `ArgumentOutOfRangeException` |

`KeyNotFoundException` is in `corelib/Collections/KeyNotFoundException.cs`.
Its message matches the .NET text. The other types come from
`corelib/Exceptions.cs`, and the exceptions can be caught like any other.

## Storage

The collections store their elements in ordinary arrays:

- `List<T>` keeps one array and doubles it when full.
- `Dictionary<TKey, TValue>` keeps parallel arrays for the hash, the key, the
  value, the next entry in the bucket, and a live flag. A bucket index is the
  non-negative hash taken modulo the bucket count. Entries stay in insertion
  order until a resize, when the arrays are rebuilt without the removed
  entries. A removed entry leaves a hole until then.
- `HashSet<T>` is a `Dictionary<T, bool>` that ignores the value.
- `Queue<T>` is a ring buffer that doubles when full.
- `Stack<T>` is an array with a count that doubles when full.

## Limits

- **No enumeration.** `foreach` over a collection is rejected until M8 step 4.
- **The surface is the list above.** There is no `Keys`, `Values`, `Sort`,
  `Capacity` or `TrimExcess`.
- **Null keys are accepted** (see above).
- **Floating-point, `decimal`, delegate, and `object` elements are rejected**
  with `ARC3001`.
- **Struct elements need their own `Equals(T)` and `GetHashCode()`.**
- **Threads are not supported**, so the collections are not safe to share
  between them.

## Verification

`samples/collections.cs` covers each public member of the five types and the
key kinds (`int`, `string`, `long`, `class` with and without an override,
`record`, and `struct`). It also has a dictionary that resizes and removes half
its keys, a queue that wraps around, and destruction points checked with
`WeakReference<T>`, including each `Clear`. It prints `collections PASS` only
when every check holds. It ends with `alloc=270 dead=270 freed=270 live=0`.

The must-fail samples `collection_float_element`, `collection_object_key`,
`collection_struct_no_equals` and `collection_struct_no_hash` expect the
messages above.

The sample was also compiled and run on stock .NET with the installed SDK,
using `dotnet run` in a new console project. The list, dictionary, set, queue, stack and key checks printed
the same results. The destruction checks did not: .NET keeps a removed object
until a collection runs, so `TryGetTarget` stays `true`. That is the
difference described in the [memory model](../memory-model.md).
