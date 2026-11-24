using System;

delegate void Step(string name);

delegate int Counter();

class Log
{
    public string Text = "";

    public void Add(string name)
    {
        Text = Text + name + ";";
    }
}

class Ticker
{
    public int Count;

    public int Next()
    {
        Count = Count + 1;
        return Count;
    }
}

class Program
{
    static string trace = "";

    static void First(string name)
    {
        trace = trace + "first:" + name + ";";
    }

    static void Second(string name)
    {
        trace = trace + "second:" + name + ";";
    }

    static void Main()
    {
        var log = new Log();
        Step add = log.Add;
        Step both = add + First;
        both += Second;
        both("a");
        Console.WriteLine("log=" + log.Text + " trace=" + trace);

        // The last occurrence leaves, the others stay.
        trace = "";
        both -= First;
        both("b");
        Console.WriteLine("removed first: log=" + log.Text + " trace=" + trace);

        trace = "";
        Step twice = First;
        twice += First;
        twice += Second;
        twice -= First;
        twice("c");
        Console.WriteLine("twice=" + trace);

        // Removing a handler that was never added changes nothing.
        trace = "";
        Step single = First;
        single -= Second;
        single("d");
        Console.WriteLine("absent=" + trace);

        // Nothing is left once every handler has been removed.
        Step only = First;
        only -= First;
        Console.WriteLine("empty=" + (only == null));

        // Delegates compare by target and method, not by identity.
        Step left = log.Add;
        Step right = log.Add;
        Step other = new Log().Add;
        Step alias = left;
        Console.WriteLine("equal=" + (left == right) + " different=" + (left != other) + " same=" + (left == alias));
        Console.WriteLine("nullEqual=" + (left == null) + "/" + (null == left));

        // The value of a multicast call is the value of the last handler.
        var ticker = new Ticker();
        Counter counter = ticker.Next;
        counter += ticker.Next;
        counter += ticker.Next;
        Console.WriteLine("last=" + counter() + " count=" + ticker.Count);

        // Invoking null raises NullReferenceException; '?.' skips it.
        Step missing = null;
        missing?.Invoke("skipped");
        var caught = false;
        try
        {
            missing("boom");
        }
        catch (NullReferenceException)
        {
            caught = true;
        }

        Console.WriteLine("caught=" + caught);
        trace = null;
        Console.WriteLine("delegates multicast PASS");
    }
}
