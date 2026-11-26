namespace System.Collections.Generic;

// Entries live in parallel arrays, so no nested entry type is needed. A removed
// entry is unlinked from its bucket and cleared at once, and the next resize
// compacts the arrays. Entries stay in insertion order until a resize.
public sealed class Dictionary<TKey, TValue>
{
    private int[] _buckets;
    private int[] _next;
    private int[] _hashes;
    private bool[] _live;
    private TKey[] _keys;
    private TValue[] _values;
    private int _used;
    private int _count;

    public Dictionary()
    {
        Allocate(4);
    }

    public Dictionary(int capacity)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException("Capacity must not be negative.");
        }

        Allocate(capacity < 4 ? 4 : capacity);
    }

    public int Count => _count;

    public TValue this[TKey key]
    {
        get
        {
            int entry = FindEntry(key, ElementEquality.GetHash(key));
            if (entry < 0)
            {
                throw new KeyNotFoundException("The given key was not present in the dictionary.");
            }

            return _values[entry];
        }
        set
        {
            int hash = ElementEquality.GetHash(key);
            int entry = FindEntry(key, hash);
            if (entry >= 0)
            {
                _values[entry] = value;
            }
            else
            {
                Insert(key, value, hash);
            }
        }
    }

    public void Add(TKey key, TValue value)
    {
        int hash = ElementEquality.GetHash(key);
        if (FindEntry(key, hash) >= 0)
        {
            throw new ArgumentException("An item with the same key has already been added.");
        }

        Insert(key, value, hash);
    }

    public bool ContainsKey(TKey key)
    {
        return FindEntry(key, ElementEquality.GetHash(key)) >= 0;
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        int entry = FindEntry(key, ElementEquality.GetHash(key));
        if (entry < 0)
        {
            value = default(TValue);
            return false;
        }

        value = _values[entry];
        return true;
    }

    public bool Remove(TKey key)
    {
        int hash = ElementEquality.GetHash(key);
        int bucket = BucketOf(hash, _buckets.Length);
        int previous = -1;
        for (int entry = _buckets[bucket]; entry >= 0; entry = _next[entry])
        {
            if (_hashes[entry] == hash && ElementEquality.AreEqual(_keys[entry], key))
            {
                if (previous < 0)
                {
                    _buckets[bucket] = _next[entry];
                }
                else
                {
                    _next[previous] = _next[entry];
                }

                _live[entry] = false;
                _keys[entry] = default(TKey);
                _values[entry] = default(TValue);
                _count--;
                return true;
            }

            previous = entry;
        }

        return false;
    }

    public void Clear()
    {
        for (int entry = 0; entry < _used; entry++)
        {
            _keys[entry] = default(TKey);
            _values[entry] = default(TValue);
            _live[entry] = false;
        }

        for (int bucket = 0; bucket < _buckets.Length; bucket++)
        {
            _buckets[bucket] = -1;
        }

        _used = 0;
        _count = 0;
    }

    private void Allocate(int capacity)
    {
        _buckets = new int[capacity];
        _next = new int[capacity];
        _hashes = new int[capacity];
        _live = new bool[capacity];
        _keys = new TKey[capacity];
        _values = new TValue[capacity];
        for (int bucket = 0; bucket < capacity; bucket++)
        {
            _buckets[bucket] = -1;
        }
    }

    private int FindEntry(TKey key, int hash)
    {
        for (int entry = _buckets[BucketOf(hash, _buckets.Length)]; entry >= 0; entry = _next[entry])
        {
            if (_hashes[entry] == hash && ElementEquality.AreEqual(_keys[entry], key))
            {
                return entry;
            }
        }

        return -1;
    }

    private void Insert(TKey key, TValue value, int hash)
    {
        if (_used == _keys.Length)
        {
            Resize();
        }

        int entry = _used;
        _used++;
        _keys[entry] = key;
        _values[entry] = value;
        _hashes[entry] = hash;
        _live[entry] = true;

        int bucket = BucketOf(hash, _buckets.Length);
        _next[entry] = _buckets[bucket];
        _buckets[bucket] = entry;
        _count++;
    }

    // Doubles the arrays unless at least half the entries are holes, in which
    // case it compacts them at the same size.
    private void Resize()
    {
        int capacity = _count < _keys.Length / 2 ? _keys.Length : _keys.Length * 2;
        TKey[] keys = new TKey[capacity];
        TValue[] values = new TValue[capacity];
        int[] hashes = new int[capacity];
        bool[] live = new bool[capacity];
        int[] buckets = new int[capacity];
        int[] next = new int[capacity];
        for (int bucket = 0; bucket < capacity; bucket++)
        {
            buckets[bucket] = -1;
        }

        int used = 0;
        for (int entry = 0; entry < _used; entry++)
        {
            if (!_live[entry])
            {
                continue;
            }

            keys[used] = _keys[entry];
            values[used] = _values[entry];
            hashes[used] = _hashes[entry];
            live[used] = true;

            int target = BucketOf(hashes[used], capacity);
            next[used] = buckets[target];
            buckets[target] = used;
            used++;
        }

        _keys = keys;
        _values = values;
        _hashes = hashes;
        _live = live;
        _buckets = buckets;
        _next = next;
        _used = used;
    }

    private static int BucketOf(int hash, int bucketCount)
    {
        return (hash & 0x7FFFFFFF) % bucketCount;
    }
}
