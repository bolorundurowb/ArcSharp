using System;
using System.Collections.Generic;

struct Plain
{
    public int X;
    public Plain(int x) { X = x; }
}

class Program
{
    static void Main()
    {
        var items = new List<Plain>();
        items.Add(new Plain(1));
        Console.WriteLine(items.Contains(new Plain(1)));
    }
}
