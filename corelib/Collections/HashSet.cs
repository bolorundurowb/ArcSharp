namespace System.Collections.Generic;

public sealed class HashSet<T>
{
    private readonly Dictionary<T, bool> _items;

    public HashSet()
    {
        _items = new Dictionary<T, bool>();
    }

    public int Count => _items.Count;

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
