using System.Collections;

namespace System.Collections.Generic;

public sealed class List<T> : IEnumerable<T>
{
    private T[] _items;
    private int _count;
    private int _version;

    public List()
    {
        _items = new T[4];
    }

    public List(int capacity)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException("Capacity must not be negative.");
        }

        _items = new T[capacity];
    }

    public int Count => _count;

    internal int Version => _version;

    public T this[int index]
    {
        get
        {
            CheckIndex(index);
            return _items[index];
        }
        set
        {
            CheckIndex(index);
            _version++;
            _items[index] = value;
        }
    }

    public void Add(T item)
    {
        if (_count == _items.Length)
        {
            Grow(_count + 1);
        }

        _version++;
        _items[_count] = item;
        _count++;
    }

    public void Insert(int index, T item)
    {
        if (index < 0 || index > _count)
        {
            throw new ArgumentOutOfRangeException("Index must be within the list.");
        }

        if (_count == _items.Length)
        {
            Grow(_count + 1);
        }

        _version++;
        for (int i = _count; i > index; i--)
        {
            _items[i] = _items[i - 1];
        }

        _items[index] = item;
        _count++;
    }

    public bool Remove(T item)
    {
        int index = IndexOf(item);
        if (index < 0)
        {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    public void RemoveAt(int index)
    {
        CheckIndex(index);
        _version++;
        _count--;
        for (int i = index; i < _count; i++)
        {
            _items[i] = _items[i + 1];
        }

        // Clearing the vacated slot releases the removed element at this statement.
        _items[_count] = default(T);
    }

    public void Clear()
    {
        _version++;
        for (int i = 0; i < _count; i++)
        {
            _items[i] = default(T);
        }

        _count = 0;
    }

    public bool Contains(T item)
    {
        return IndexOf(item) >= 0;
    }

    public int IndexOf(T item)
    {
        for (int i = 0; i < _count; i++)
        {
            if (ElementEquality.AreEqual(_items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    public T[] ToArray()
    {
        T[] result = new T[_count];
        for (int i = 0; i < _count; i++)
        {
            result[i] = _items[i];
        }

        return result;
    }

    public IEnumerator<T> GetEnumerator()
    {
        return new ListEnumerator<T>(this);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private void CheckIndex(int index)
    {
        if (index < 0 || index >= _count)
        {
            throw new ArgumentOutOfRangeException("Index was out of range. Must be non-negative and less than the size of the collection.");
        }
    }

    private void Grow(int minimum)
    {
        int capacity = _items.Length * 2;
        if (capacity < 4)
        {
            capacity = 4;
        }

        if (capacity < minimum)
        {
            capacity = minimum;
        }

        T[] items = new T[capacity];
        for (int i = 0; i < _count; i++)
        {
            items[i] = _items[i];
        }

        _items = items;
    }
}

internal sealed class ListEnumerator<T> : IEnumerator<T>
{
    private List<T> _list;
    private int _version;
    private int _index;
    private T _current;

    public ListEnumerator(List<T> list)
    {
        _list = list;
        _version = list.Version;
        _current = default(T);
    }

    public T Current => _current;

    object IEnumerator.Current
    {
        get
        {
            throw new NotSupportedException("Non-generic enumeration of a collection is not supported.");
        }
    }

    public bool MoveNext()
    {
        CheckVersion();
        if (_index < _list.Count)
        {
            _current = _list[_index];
            _index++;
            return true;
        }

        _current = default(T);
        return false;
    }

    public void Reset()
    {
        CheckVersion();
        _index = 0;
        _current = default(T);
    }

    public void Dispose()
    {
    }

    private void CheckVersion()
    {
        if (_version != _list.Version)
        {
            throw new InvalidOperationException("Collection was modified during enumeration.");
        }
    }
}
