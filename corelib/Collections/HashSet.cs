using System.Collections;

namespace System.Collections.Generic;

public sealed class HashSet<T> : IEnumerable<T>
{
    private readonly Dictionary<T, bool> _items;

    public HashSet()
    {
        _items = new Dictionary<T, bool>();
    }

    public int Count => _items.Count;

    public IEnumerator<T> GetEnumerator()
    {
        return new HashSetEnumerator<T>(_items.GetEnumerator());
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public bool Add(T item)
    {
        if (_items.ContainsKey(item))
        {
            return false;
        }

        _items.Add(item, true);
        return true;
    }

    public bool Contains(T item)
    {
        return _items.ContainsKey(item);
    }

    public bool Remove(T item)
    {
        return _items.Remove(item);
    }

    public void Clear()
    {
        _items.Clear();
    }
}

internal sealed class HashSetEnumerator<T> : IEnumerator<T>
{
    private readonly IEnumerator<KeyValuePair<T, bool>> _entries;

    public HashSetEnumerator(IEnumerator<KeyValuePair<T, bool>> entries)
    {
        _entries = entries;
    }

    public T Current => _entries.Current.Key;

    object IEnumerator.Current
    {
        get
        {
            throw new NotSupportedException("Non-generic enumeration is not supported.");
        }
    }

    public bool MoveNext()
    {
        return _entries.MoveNext();
    }

    public void Reset()
    {
        _entries.Reset();
    }

    public void Dispose()
    {
        _entries.Dispose();
    }
}
