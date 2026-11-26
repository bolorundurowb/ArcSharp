using System;
using System.Collections.Generic;

class Program
{
    static void Main()
    {
        var names = new Dictionary<object, int>();
        names["a"] = 1;
        Console.WriteLine(names.Count);
    }
}
