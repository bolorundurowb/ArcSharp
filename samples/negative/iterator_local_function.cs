using System.Collections.Generic;

static class Program
{
    static void Main()
    {
        IEnumerable<int> Local()
        {
            yield return 1;
        }

        foreach (int n in Local())
        {
            System.Console.WriteLine(n);
        }
    }
}
