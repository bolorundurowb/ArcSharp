using System.Collections;

namespace System.Collections.Generic;

// A circular buffer. Vacated slots are cleared so a dequeued element is
// released at the Dequeue call, not when the slot is overwritten.
public sealed class Queue<T> : IEnumerable<T>
{
    private T[] _items;
    private int _head;
    private int _count;
    private int _version;

    public Queue()
    {
        _items = new T[4];
    }

    public int Count => _count;

    internal int Version => _version;

    internal T GetEnumerationElement(int index) => _items[(_head + index) % _items.Length];

    public IEnumerator<T> GetEnumerator()
    {
        return new QueueEnumerator<T>(this);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Enqueue(T item)
    {
        if (_count == _items.Length)
        {
            Grow();
        }

        _items[(_head + _count) % _items.Length] = item;
        _count++;
        _version++;
    }

    public T Dequeue()
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("Queue is empty.");
        }

        T item = _items[_head];
        _items[_head] = default(T);
        _head = (_head + 1) % _items.Length;
        _count--;
        _version++;
        return item;
    }

    public T Peek()
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("Queue is empty.");
        }

        return _items[_head];
    }

    public void Clear()
    {
        for (int i = 0; i < _count; i++)
        {
            _items[(_head + i) % _items.Length] = default(T);
        }

        _head = 0;
        _count = 0;
        _version++;
    }

    private void Grow()
    {
        T[] items = new T[_items.Length * 2];
        for (int i = 0; i < _count; i++)
        {
            items[i] = _items[(_head + i) % _items.Length];
        }

        _items = items;
        _head = 0;
    }
}

internal sealed class QueueEnumerator<T> : IEnumerator<T>
{
    private readonly Queue<T> _owner;
    private readonly int _version;
    private int _index;
    private T _current;

    public QueueEnumerator(Queue<T> owner)
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
