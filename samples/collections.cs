using System;
using System.Collections.Generic;

class Token
{
    public int Id;
    public Token(int id) { Id = id; }
}

class Named
{
    public string Name;
    public Named(string name) { Name = name; }
    public override bool Equals(object other) => other is Named n && n.Name == Name;
    public override int GetHashCode() => Name.Length;
}

record Point(int X, int Y);

struct Cell
{
    public int Row;
    public int Col;
    public Cell(int row, int col) { Row = row; Col = col; }
    public bool Equals(Cell other) => other.Row == Row && other.Col == Col;
    public override bool Equals(object other) => other is Cell c && Equals(c);
    public override int GetHashCode() => Row * 31 + Col;
}

class Program
{
    static void Main()
    {
        bool ok = ListCase() && DictionaryCase() && SetCase() && QueueStackCase() && KeyCase() && DestructionCase();
        Console.WriteLine(ok ? "collections PASS" : "collections FAIL");
    }

    static bool ListCase()
    {
        var list = new List<int>();
        for (int i = 0; i < 20; i++)
        {
            list.Add(i * 3);
        }

        bool ok = list.Count == 20 && list[19] == 57 && list.Contains(30) && !list.Contains(31) && list.IndexOf(12) == 4;
        list.Remove(30);
        list.RemoveAt(0);
        ok = ok && list.Count == 18 && list[0] == 3 && !list.Contains(30);
        list.Insert(0, 42);
        list[1] = 43;
        int[] copy = list.ToArray();
        ok = ok && list.Count == 19 && list[0] == 42 && list[1] == 43 && copy.Length == 19 && copy[0] == 42 && copy[18] == 57;
        list.Clear();
        ok = ok && list.Count == 0;

        var presized = new List<int>(64);
        presized.Add(5);
        ok = ok && presized.Count == 1 && presized[0] == 5;
        Console.WriteLine("list ok=" + ok);
        return ok;
    }

    static bool DictionaryCase()
    {
        var map = new Dictionary<string, int>();
        map.Add("a", 1);
        map["b"] = 2;
        map["b"] = 3;
        bool ok = map.Count == 2 && map["b"] == 3 && map.ContainsKey("a") && !map.ContainsKey("c");
        ok = ok && map.Remove("a") && !map.Remove("a") && map.Count == 1;
        ok = ok && map.TryGetValue("b", out int value) && value == 3;

        var caught = false;
        try
        {
            Console.WriteLine(map["zzz"]);
        }
        catch (KeyNotFoundException)
        {
            caught = true;
        }

        ok = ok && caught;

        var big = new Dictionary<int, int>();
        for (int i = 0; i < 3000; i++)
        {
            big[i * 7] = i;
        }

        for (int i = 0; i < 3000; i += 2)
        {
            big.Remove(i * 7);
        }

        ok = ok && big.Count == 1500 && big[7] == 1 && !big.ContainsKey(0);
        big.Clear();
        ok = ok && big.Count == 0 && !big.ContainsKey(7);

        var sized = new Dictionary<int, string>(100);
        sized[1] = "one";
        ok = ok && sized.Count == 1 && sized[1] == "one";
        Console.WriteLine("dictionary ok=" + ok);
        return ok;
    }

    static bool SetCase()
    {
        var set = new HashSet<string>();
        bool ok = set.Add("x") && !set.Add("x") && set.Contains("x") && !set.Contains("y");
        ok = ok && set.Remove("x") && set.Count == 0;
        ok = ok && set.Add(null) && set.Contains(null) && set.Count == 1;
        set.Clear();
        ok = ok && set.Count == 0 && !set.Contains(null);

        var longs = new HashSet<long>();
        for (int i = 0; i < 50; i++)
        {
            longs.Add((long)i << 32);
        }

        ok = ok && longs.Count == 50 && longs.Contains(7L << 32) && !longs.Contains(1L);
        Console.WriteLine("set ok=" + ok);
        return ok;
    }

    static bool QueueStackCase()
    {
        var queue = new Queue<int>();
        for (int i = 0; i < 4; i++)
        {
            queue.Enqueue(i);
        }

        queue.Dequeue();
        queue.Dequeue();
        for (int i = 4; i < 12; i++)
        {
            queue.Enqueue(i);
        }

        bool ok = queue.Peek() == 2 && queue.Count == 10;
        int expected = 2;
        while (queue.Count > 0)
        {
            if (queue.Dequeue() != expected)
            {
                ok = false;
            }

            expected++;
        }

        ok = ok && expected == 12;

        var stack = new Stack<int>();
        for (int i = 0; i < 9; i++)
        {
            stack.Push(i);
        }

        ok = ok && stack.Pop() == 8 && stack.Peek() == 7 && stack.Count == 8;
        stack.Clear();
        queue.Clear();
        ok = ok && stack.Count == 0 && queue.Count == 0;
        Console.WriteLine("queue and stack ok=" + ok);
        return ok;
    }

    static bool KeyCase()
    {
        var nodes = new List<Token>();
        var first = new Token(1);
        nodes.Add(first);
        nodes.Add(new Token(1));
        bool ok = nodes.IndexOf(first) == 0 && !nodes.Contains(new Token(1));

        var named = new Dictionary<Named, int>();
        named[new Named("alpha")] = 7;
        ok = ok && named.ContainsKey(new Named("alpha")) && named[new Named("alpha")] == 7;

        var points = new HashSet<Point>();
        ok = ok && points.Add(new Point(1, 2)) && !points.Add(new Point(1, 2)) && points.Count == 1;

        var cells = new List<Cell>();
        cells.Add(new Cell(2, 3));
        ok = ok && cells.Contains(new Cell(2, 3)) && !cells.Contains(new Cell(3, 2));

        var cellNames = new Dictionary<Cell, string>();
        cellNames[new Cell(2, 3)] = "found";
        ok = ok && cellNames[new Cell(2, 3)] == "found";
        Console.WriteLine("keys ok=" + ok);
        return ok;
    }

    static bool DestructionCase()
    {
        var list = new List<Token>();
        list.Add(new Token(1));
        list.Add(new Token(2));
        var removedAt = new WeakReference<Token>(list[0]);
        list.RemoveAt(0);
        bool ok = !IsAlive(removedAt) && list.Count == 1;

        var map = new Dictionary<string, Token>();
        map["x"] = new Token(3);
        var replaced = new WeakReference<Token>(map["x"]);
        map["x"] = new Token(4);
        ok = ok && !IsAlive(replaced);

        var removed = new WeakReference<Token>(map["x"]);
        map.Remove("x");
        ok = ok && !IsAlive(removed) && map.Count == 0;

        var queue = new Queue<Token>();
        queue.Enqueue(new Token(5));
        var dequeued = new WeakReference<Token>(queue.Peek());
        queue.Dequeue();
        ok = ok && !IsAlive(dequeued);

        var stack = new Stack<Token>();
        stack.Push(new Token(6));
        var popped = new WeakReference<Token>(stack.Peek());
        stack.Pop();
        ok = ok && !IsAlive(popped);

        list.Add(new Token(8));
        var cleared = new WeakReference<Token>(list[0]);
        list.Clear();
        ok = ok && !IsAlive(cleared) && list.Count == 0;

        var set = new HashSet<Token>();
        var inSet = AddToSet(set);
        set.Clear();
        ok = ok && !IsAlive(inSet) && set.Count == 0;

        var clearedMap = new Dictionary<string, Token>();
        clearedMap["y"] = new Token(12);
        var mapValue = new WeakReference<Token>(clearedMap["y"]);
        clearedMap.Clear();
        ok = ok && !IsAlive(mapValue) && clearedMap.Count == 0;

        queue.Enqueue(new Token(10));
        var queued = new WeakReference<Token>(queue.Peek());
        queue.Clear();
        ok = ok && !IsAlive(queued);

        stack.Push(new Token(11));
        var pushed = new WeakReference<Token>(stack.Peek());
        stack.Clear();
        ok = ok && !IsAlive(pushed);
        Console.WriteLine("destruction ok=" + ok);
        return ok;
    }

    static WeakReference<Token> AddToSet(HashSet<Token> set)
    {
        var token = new Token(9);
        set.Add(token);
        return new WeakReference<Token>(token);
    }

    static bool IsAlive(WeakReference<Token> reference)
    {
        return reference.TryGetTarget(out var target);
    }
}
