static class Trace
{
    public static int Order;
}

class Ready
{
    public static readonly int Value = 4;

    static Ready()
    {
        Trace.Order = Trace.Order * 10 + 1;
        Value += 3;
        Observe();
    }

    public Ready(int value)
    {
        Trace.Order = Trace.Order * 10 + value;
    }

    public static void Observe()
    {
        Trace.Order = Trace.Order * 10 + 2;
    }
}

class Broken
{
    static Broken()
    {
        Trace.Order = Trace.Order * 10 + 4;
        throw new InvalidOperationException();
    }

    public static void Observe()
    {
        Trace.Order = -1;
    }
}

static class Program
{
    static int Mark()
    {
        Trace.Order = Trace.Order * 10 + 3;
        return 8;
    }

    static void Main()
    {
        var value = new Ready(Mark());
        Ready.Observe();
        TypeInitializationException first = null;
        bool repeated = false;

        try
        {
            Broken.Observe();
        }
        catch (TypeInitializationException problem)
        {
            first = problem;
        }

        try
        {
            Broken.Observe();
        }
        catch (TypeInitializationException problem)
        {
            repeated = problem == first;
        }

        if (Ready.Value == 7 && Trace.Order == 312824 && repeated
            && first.TypeName == "Broken" && first.InnerException is InvalidOperationException)
        {
            Console.WriteLine("static constructor PASS");
        }
    }
}
