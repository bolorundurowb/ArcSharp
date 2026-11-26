namespace System.Collections.Generic;

// A circular buffer. Vacated slots are cleared so a dequeued element is
// released at the Dequeue call, not when the slot is overwritten.
public sealed class Queue<T>
{
    private T[] _items;
    private int _head;
    private int _count;

    public Queue()
    {
        _items = new T[4];
    }

    public int Count => _count;

    public void Enqueue(T item)
    {
        if (_count == _items.Length)
        {
            Grow();
        }

        _items[(_head + _count) % _items.Length] = item;
        _count++;
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
