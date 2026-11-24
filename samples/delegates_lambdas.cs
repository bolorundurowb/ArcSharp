using System;

delegate int Compute(int value);

delegate void Notify(string text);

class Accumulator
{
    public int Total;

    // The lambda captures 'this' and the parameter 'step'.
    public Compute Adder(int step)
    {
        return value =>
        {
            Total = Total + value + step;
            return Total;
        };
    }

    public int Read(int value)
    {
        return Total + value;
    }
}

class Program
{
    static string log = "";

    static void Record(string text)
    {
        log = log + text + ";";
    }

    static void Main()
    {
        // A local captured by a lambda is shared with the method that declares it.
        var offset = 10;
        Compute plus = value => value + offset;
        offset = 20;
        Console.WriteLine("captured=" + plus(1));

        // Lambdas and method groups combine into one multicast delegate. The
        // value of the call is the value of the last handler.
        var accumulator = new Accumulator();
        Compute chain = accumulator.Read;
        chain += plus;
        chain += value => value * 2;
        Console.WriteLine("chain=" + chain(5));

        // A lambda that writes through 'this' keeps its own state between calls.
        var adder = accumulator.Adder(3);
        var first = adder(4);
        var second = adder(1);
        Console.WriteLine("adder=" + first + "," + second + " total=" + accumulator.Total);

        // A lambda in a multicast list is removed by equality, like any handler.
        Notify notify = text => Record("a:" + text);
        Notify other = text => Record("b:" + text);
        notify += other;
        notify("x");
        notify -= other;
        notify("y");
        Console.WriteLine("log=" + log);

        // Two lambdas written apart are two closures, so they are not equal.
        Notify separate = text => Record(text);
        Notify alsoSeparate = text => Record(text);
        Console.WriteLine("same=" + (separate == alsoSeparate));

        // A null delegate skips '?.Invoke' and raises NullReferenceException on a call.
        Notify missing = null;
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
        log = null;
        Console.WriteLine("delegates lambdas PASS");
    }
}
