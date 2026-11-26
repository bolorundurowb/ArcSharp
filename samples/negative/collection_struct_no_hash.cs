using System;
using System.Collections.Generic;

struct Plain
{
    public int X;
    public Plain(int x) { X = x; }
    public bool Equals(Plain other) => other.X == X;
}

class Program
{
    static void Main()
    {
        var items = new HashSet<Plain>();
        items.Add(new Plain(1));
        Console.WriteLine(items.Count);
    }
}
