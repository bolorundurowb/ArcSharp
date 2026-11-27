using System.Collections.Generic;

struct Source
{
    public IEnumerable<int> Numbers()
    {
        yield return 1;
    }
}

static class Program
{
    static void Main()
    {
        var source = new Source();
        foreach (int n in source.Numbers())
        {
            System.Console.WriteLine(n);
        }
    }
}
