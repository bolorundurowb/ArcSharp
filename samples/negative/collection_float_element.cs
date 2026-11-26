using System;
using System.Collections.Generic;

class Program
{
    static void Main()
    {
        var values = new HashSet<double>();
        values.Add(1.5);
        Console.WriteLine(values.Contains(1.5));
    }
}
