using System;
using System.Collections.Generic;

sealed class Item
{
    public int Id;

    public Item(int id) { Id = id; }
}

sealed class Gate : IDisposable
{
    public static int Open;

    public Gate() { Open++; }

    public void Dispose() { Open--; }
}

sealed class Shelf
{
    private readonly Item first = new Item(10);
    private readonly Item second = new Item(20);
    public int Scale = 2;

    public IEnumerable<int> Scaled()
    {
        yield return first.Id * Scale;
        yield return second.Id * Scale;
    }

    public IEnumerable<Item> Items
    {
        get
        {
            yield return first;
            yield return second;
        }
    }
}

sealed class Box<T>
{
    private readonly T value;

    public Box(T item) { value = item; }

    public IEnumerable<T> Twice()
    {
        yield return value;
        yield return value;
    }
}

static class Program
{
    static IEnumerable<int> Count(int from, int to)
    {
        for (int i = from; i <= to; i++)
        {
            yield return i;
        }
    }

    static IEnumerable<int> Upto(int limit)
    {
        int i = 0;
        while (true)
        {
            if (i >= limit)
            {
                yield break;
            }

            yield return i++;
        }
    }

    static IEnumerable<int> Guarded()
    {
        try
        {
            yield return 1;
            yield return 2;
            yield return 3;
        }
        finally
        {
            Console.WriteLine("guard finally");
        }
    }

    static IEnumerable<T> Repeat<T>(T value, int times)
    {
        for (int i = 0; i < times; i++)
        {
            yield return value;
        }
    }

    static IEnumerable<string> Names()
    {
        yield return "ada";
        yield return "grace";
        yield return "edsger";
    }

    static IEnumerable<int> Classify(int count)
    {
        for (int i = 0; i < count; i++)
        {
            switch (i % 3)
            {
                case 0:
                    yield return 100 + i;
                    break;
                case 1:
                    yield return 200 + i;
                    break;
                default:
                    continue;
            }
        }
    }

    static IEnumerable<int> Rebased(int start)
    {
        start = start * 2;
        yield return start;
        start = start + 1;
        yield return start;
    }

    static IEnumerable<int> Gated()
    {
        using (var gate = new Gate())
        {
            yield return Gate.Open;
            yield return Gate.Open;
        }
    }

    static IEnumerable<int> Flatten(int rows)
    {
        for (int r = 1; r <= rows; r++)
        {
            foreach (int c in Count(1, r))
            {
                yield return r * 10 + c;
            }
        }
    }

    static IEnumerable<int> Mapped(int factor)
    {
        Func<int, int> scale = x => x * factor;
        for (int i = 1; i <= 3; i++)
        {
            yield return scale(i);
        }
    }

    static IEnumerable<int> Explodes()
    {
        yield return 1;
        throw new InvalidOperationException();
    }

    static IEnumerator<int> Cursor()
    {
        yield return 7;
        yield return 8;
    }

    static IEnumerable<Item> Fresh(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return new Item(i + 1);
        }
    }

    static int Total(IEnumerable<int> source)
    {
        int total = 0;
        foreach (int n in source)
        {
            total += n;
        }

        return total;
    }

    static void Main()
    {
        Console.WriteLine("count sum=" + Total(Count(1, 4)));
        Console.WriteLine("upto sum=" + Total(Upto(5)));

        var counted = Count(1, 3);
        Console.WriteLine("twice " + Total(counted) + " " + Total(counted));

        foreach (int n in Guarded())
        {
            Console.WriteLine("guarded " + n);
            if (n == 2)
            {
                break;
            }
        }

        Console.WriteLine("guarded all=" + Total(Guarded()));

        int repeated = 0;
        foreach (string s in Repeat("x", 3))
        {
            repeated += s.Length;
        }

        Console.WriteLine("repeat strings=" + repeated);
        Console.WriteLine("repeat ints=" + Total(Repeat(5, 4)));

        foreach (string name in Names())
        {
            Console.WriteLine("name " + name);
        }

        Console.WriteLine("classify=" + Total(Classify(7)));
        Console.WriteLine("rebased=" + Total(Rebased(5)));

        Console.WriteLine("gated=" + Total(Gated()) + " open=" + Gate.Open);
        foreach (int n in Gated())
        {
            break;
        }

        Console.WriteLine("gate open after break=" + Gate.Open);

        Console.WriteLine("flatten=" + Total(Flatten(3)));
        Console.WriteLine("mapped=" + Total(Mapped(4)));

        var shelf = new Shelf();
        Console.WriteLine("shelf scaled=" + Total(shelf.Scaled()));
        int ids = 0;
        foreach (Item item in shelf.Items)
        {
            ids += item.Id;
        }

        Console.WriteLine("shelf ids=" + ids);

        var box = new Box<int>(9);
        Console.WriteLine("box=" + Total(box.Twice()));

        int caught = 0;
        try
        {
            foreach (int n in Explodes())
            {
                caught += n;
            }
        }
        catch (InvalidOperationException)
        {
            caught += 100;
        }

        Console.WriteLine("caught=" + caught);

        IEnumerator<int> cursor = Cursor();
        int walked = 0;
        while (cursor.MoveNext())
        {
            walked += cursor.Current;
        }

        cursor.Dispose();
        Console.WriteLine("cursor=" + walked);

        int fresh = 0;
        foreach (Item item in Fresh(4))
        {
            fresh += item.Id;
        }

        Console.WriteLine("fresh=" + fresh);
        Console.WriteLine("iterators done");
    }
}
