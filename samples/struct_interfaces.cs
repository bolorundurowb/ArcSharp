interface ICounter
{
    int Increment();
}

struct Counter : ICounter
{
    public int Count;
    public string Prefix;

    int ICounter.Increment()
    {
        Count++;
        return Count;
    }

    public override string ToString()
    {
        return Prefix + Count;
    }
}

struct Plain
{
}

class Other
{
}

static class Program
{
    static int Increment<T>(T value) where T : ICounter
    {
        return value.Increment();
    }

    static void Main()
    {
        Counter counter = new Counter();
        counter.Count = 5;
        counter.Prefix = "counter=";
        ICounter boxed = counter;
        counter.Prefix = null;
        int updated = boxed.Increment();
        Counter unboxed = (Counter)boxed;
        object original = boxed;
        bool caught = false;

        try
        {
            object unrelated = new Other();
            Counter invalid = (Counter)unrelated;
        }
        catch (InvalidCastException)
        {
            caught = true;
        }

        Console.WriteLine(unboxed);
        Console.WriteLine(new Plain());

        if (counter.Count == 5 && updated == 6 && unboxed.Count == 6
            && Increment(unboxed) == 7 && unboxed.Count == 6
            && original is Counter && caught)
        {
            Console.WriteLine("struct interfaces PASS");
        }
    }
}
