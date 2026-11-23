sealed class Counter
{
    public readonly int Value;
}

static class Program
{
    static void Main()
    {
        var counter = new Counter();
        counter.Value = 1;
    }
}
