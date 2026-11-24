static class First
{
    public sealed class Meter
    {
        public const int Default = 3;
        public readonly int Value;

        public Meter(int value) => Value = value;

        public static Meter operator +(Meter left, Meter right) => new Meter(left.Value + right.Value);

        public static Meter operator -(Meter value) => new Meter(-value.Value);

        public static explicit operator int(Meter value) => value.Value;

        public static explicit operator long(Meter value) => value.Value + 100L;
    }
}

static class Second
{
    public static class Meter
    {
        public const int Default = 7;
    }
}

static class Program
{
    static void Print(string message) => Console.WriteLine(message);

    static void Main()
    {
        const int start = First.Meter.Default;
        const string message = "operators and members PASS";
        var value = new First.Meter(start);
        var result = -(value + value);
        if ((int)result == -6 && (long)result == 94 && Second.Meter.Default == 7)
        {
            Print(message);
        }
    }
}
