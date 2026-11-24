// Expected: ARC3001 a delegate cannot bind to an instance method of a struct.
delegate int Reader();

struct Counter
{
    public int Value;

    public int Read()
    {
        return Value;
    }
}

class Program
{
    static void Main()
    {
        var counter = new Counter();
        Reader reader = counter.Read;
    }
}
