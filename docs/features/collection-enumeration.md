# Collection enumeration

`List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `Queue<T>` and
`Stack<T>` implement `IEnumerable<T>` for their element type. A dictionary
yields `KeyValuePair<TKey, TValue>` values with `Key` and `Value` properties.
Each call to `GetEnumerator()` creates an independent reference enumerator.

| Collection | Iteration order |
|---|---|
| List | Index order |
| Queue | First in, first out |
| Stack | Last in, first out |
| Dictionary and hash set | Unspecified. Removed entries are skipped |

List, queue and stack enumerators detect mutations in `MoveNext()` and
`Reset()` and throw `InvalidOperationException`. Successful insertions
invalidate dictionary and hash set enumerators. Removing or clearing entries,
replacing a dictionary value, and adding a duplicate to a set preserve the
enumerator. An existing enumerator reads each entry as it advances.

The reference enumerators use the same method dispatch and cleanup as
[enumerable foreach](enumerable-foreach.md). The enumerator keeps its source
alive under ARC, and generic `foreach` releases the enumerator when the loop
ends, including on break or exception. The set enumerator wraps its backing
dictionary enumerator and reads the key from each pair. Non-generic
`IEnumerator.Current` throws `NotSupportedException`, as it does for
[iterators](iterators.md).

The [collection enumeration sample](../../samples/collection_enumeration.cs)
checks iteration over all five collections, including reference-valued pairs,
skipped entries, circular queue order, an early break, and mutation detection.
The Windows verification harness checks the output and ARC accounting.
