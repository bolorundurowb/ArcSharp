class Counter
{
    public static int Count { get; set; }
}

class Program
{
    static void Main()
    {
        Counter.Count = 3;
        Counter.Count = Counter.Count + 1;
        Console.WriteLine("count=" + Counter.Count);
    }
}
