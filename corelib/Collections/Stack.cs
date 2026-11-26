namespace System.Collections.Generic;

public sealed class Stack<T>
{
    private T[] _items;
    private int _count;

    public Stack()
    {
        _items = new T[4];
    }

    public int Count => _count;

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
    }

    public T Pop()
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("Stack is empty.");
        }

        _count--;
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
    }
}
