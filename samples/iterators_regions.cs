using System;
using System.Collections.Generic;

struct Point
{
    public int X;
    public int Y;
}

sealed class Trace
{
    public string Log = "";
}

sealed class Pair<TKey, TValue>
{
    public TKey Key;
    public TValue Value;

    public Pair(TKey key, TValue value)
    {
        Key = key;
        Value = value;
    }
}

static class Program
{
    static IEnumerable<int> Walk(int steps)
    {
        Point p = new Point();
        p.X = 1;
        for (int i = 0; i < steps; i++)
        {
            p.Y = p.Y + i;
            yield return p.X + p.Y;
            p.X = p.X + 1;
        }
    }

    static IEnumerable<int> Nested(Trace trace)
    {
        try
        {
            trace.Log += "a";
            try
            {
                trace.Log += "b";
                yield return 1;
                trace.Log += "c";
                yield return 2;
            }
            finally
            {
                trace.Log += "d";
            }

            yield return 3;
        }
        finally
        {
            trace.Log += "e";
        }
    }

    static IEnumerable<int> Caught()
    {
        int seen = 0;
        yield return 1;
        try
        {
            seen++;
            throw new InvalidOperationException();
        }
        catch (InvalidOperationException)
        {
            seen += 10;
        }

        yield return seen;
    }

    static IEnumerable<int> FromArray(int length)
    {
        int[] data = new int[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = i * i;
        }

        foreach (int value in data)
        {
            yield return value;
        }
    }

    static IEnumerable<string> Words(string text)
    {
        string current = "";
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ')
            {
                yield return current;
                current = "";
            }
            else
            {
                current += c;
            }
        }

        yield return current;
    }

    static IEnumerable<TKey> Keys<TKey, TValue>(Pair<TKey, TValue> first, Pair<TKey, TValue> second)
    {
        yield return first.Key;
        yield return second.Key;
    }

    static IEnumerable<int> Empty()
    {
        yield break;
    }

    static IEnumerable<int> Chain(int depth)
    {
        if (depth == 0)
        {
            yield return 0;
            yield break;
        }

        foreach (int n in Chain(depth - 1))
        {
            yield return n + depth;
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
        Console.WriteLine("walk=" + Total(Walk(4)));

        var trace = new Trace();
        Total(Nested(trace));
        Console.WriteLine("nested full=" + trace.Log);

        trace.Log = "";
        foreach (int n in Nested(trace))
        {
            if (n == 1)
            {
                break;
            }
        }

        Console.WriteLine("nested break=" + trace.Log);

        trace.Log = "";
        IEnumerator<int> untouched = Nested(trace).GetEnumerator();
        untouched.Dispose();
        Console.WriteLine("nested untouched=[" + trace.Log + "]");

        Console.WriteLine("caught=" + Total(Caught()));
        Console.WriteLine("array=" + Total(FromArray(5)));

        int words = 0;
        foreach (string word in Words("one two three"))
        {
            words += word.Length;
        }

        Console.WriteLine("words=" + words);

        var first = new Pair<string, int>("alpha", 1);
        var second = new Pair<string, int>("beta", 2);
        foreach (string key in Keys(first, second))
        {
            Console.WriteLine("key " + key);
        }

        Console.WriteLine("empty=" + Total(Empty()));
        Console.WriteLine("chain=" + Total(Chain(3)));
        Console.WriteLine("regions done");
    }
}
