using System.Collections;

namespace System.Collections.Generic;

public sealed class Stack<T> : IEnumerable<T>
{
    private T[] _items;
    private int _count;
    private int _version;

    public Stack()
    {
        _items = new T[4];
    }

    public int Count => _count;

    internal int Version => _version;

    internal T GetEnumerationElement(int index) => _items[_count - 1 - index];

    public IEnumerator<T> GetEnumerator()
    {
        return new StackEnumerator<T>(this);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Push(T item)
    {
        if (_count == _items.Length)
        {
            T[] items = new T[_items.Length * 2];
            for (int i = 0; i < _count; i++)
            {
                items[i] = _items[i];
            }

            _items = items;
        }

        _items[_count] = item;
        _count++;
        _version++;
    }

    public T Pop()
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("Stack is empty.");
        }

        _count--;
        _version++;
        T item = _items[_count];
        _items[_count] = default(T);
        return item;
    }

    public T Peek()
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("Stack is empty.");
        }

        return _items[_count - 1];
    }

    public void Clear()
    {
        for (int i = 0; i < _count; i++)
        {
            _items[i] = default(T);
        }

        _count = 0;
        _version++;
    }
}

internal sealed class StackEnumerator<T> : IEnumerator<T>
{
    private readonly Stack<T> _owner;
    private readonly int _version;
    private int _index;
    private T _current;

    public StackEnumerator(Stack<T> owner)
    {
        _owner = owner;
        _version = owner.Version;
    }

    public T Current
    {
        get
        {
            if (_index <= 0)
            {
                throw new InvalidOperationException("Enumeration has not started or has already finished.");
            }

            return _current;
        }
    }

    object IEnumerator.Current
    {
        get
        {
            throw new NotSupportedException("Non-generic enumeration is not supported.");
        }
    }

    public bool MoveNext()
    {
        CheckVersion();
        if (_index < 0 || _index >= _owner.Count)
        {
            Dispose();
            return false;
        }

        _current = _owner.GetEnumerationElement(_index);
        _index++;
        return true;
    }

    public void Reset()
    {
        CheckVersion();
        _index = 0;
        _current = default(T);
    }

    public void Dispose()
    {
        _index = -1;
        _current = default(T);
    }

    private void CheckVersion()
    {
        if (_version != _owner.Version)
        {
            throw new InvalidOperationException("Collection was modified during enumeration.");
        }
    }
}
