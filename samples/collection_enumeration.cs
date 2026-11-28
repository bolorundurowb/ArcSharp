using System;
using System.Collections.Generic;

class Item
{
    public int Id;
    public Item(int id) { Id = id; }
}

class Program
{
    static void Main()
    {
        Console.WriteLine(ListCase() && DictionaryCase() && SetCase() && QueueCase() && StackCase()
            ? "collection enumeration PASS" : "collection enumeration FAIL");
    }

    static bool ListCase()
    {
        var list = new List<Item>();
        list.Add(new Item(1));
        list.Add(new Item(2));
        IEnumerable<Item> items = list;
        int result = 0;
        foreach (var item in items)
        {
            result = result * 10 + item.Id;
        }

        foreach (var item in list)
        {
            if (item.Id != 1)
            {
                return false;
            }

            break;
        }

        try
        {
            foreach (var item in list)
            {
                list.Add(item);
            }
        }
        catch (InvalidOperationException)
        {
            return result == 12;
        }

        return false;
    }

    static bool DictionaryCase()
    {
        var map = new Dictionary<int, Item>();
        map.Add(7, new Item(1));
        map.Add(8, new Item(2));
        map.Add(9, new Item(3));
        map.Remove(8);
        IEnumerable<KeyValuePair<int, Item>> entries = map;
        int count = 0;
        int mask = 0;
        foreach (var pair in entries)
        {
            count++;
            if (pair.Key == 7 && pair.Value.Id == 1)
            {
                mask |= 1;
            }
            if (pair.Key == 9 && pair.Value.Id == 3)
            {
                mask |= 2;
            }
        }

        return count == 2 && mask == 3;
    }

    static bool SetCase()
    {
        var set = new HashSet<int>();
        set.Add(1);
        set.Add(2);
        set.Add(3);
        set.Remove(2);
        int mask = 0;
        int count = 0;
        foreach (int item in set)
        {
            mask |= 1 << item;
            count++;
        }

        return count == 2 && mask == 10;
    }

    static bool QueueCase()
    {
        var queue = new Queue<int>();
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);
        queue.Enqueue(4);
        queue.Dequeue();
        queue.Enqueue(5);
        int result = 0;
        foreach (int item in queue)
        {
            result = result * 10 + item;
        }

        return result == 2345;
    }

    static bool StackCase()
    {
        var stack = new Stack<int>();
        foreach (int item in stack)
        {
            return false;
        }

        stack.Push(1);
        stack.Push(2);
        stack.Push(3);
        int result = 0;
        foreach (int item in stack)
        {
            result = result * 10 + item;
        }

        return result == 321;
    }
}
