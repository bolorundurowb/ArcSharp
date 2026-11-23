static class Program
{
    static int calls;

    static Counter Create()
    {
        calls++;
        return new Counter();
    }

    static void Main()
    {
        int[] values = new int[2];
        values[0] = 9;
        int index = 0;
        int product = values[index++] *= 3;
        values[0] %= 5;
        short small = 30000;
        small *= 3;
        double large = 5.0;
        large /= 2;
        uint unsigned = 4000000000u;
        unsigned /= 2;
        int property = Create().Value++;
        var counter = new Counter();
        counter.Value <<= 2;
        string label = counter.Label += 2;
        counter.Label += null;

        if (product == 27 && index == 1 && values[0] == 2 && values[1] == 0
            && small == 24464 && large == 2.5 && unsigned == 2000000000u
            && calls == 1 && property == 3 && counter.Value == 12 && label == "start2"
            && counter.Label == label)
        {
            Console.WriteLine("compound PASS");
        }
    }
}

sealed class Counter
{
    public int Value { get; set; }

    public string Label { get; set; }

    public Counter()
    {
        Value = 3;
        Label = "start";
    }
}
