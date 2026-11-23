sealed class Gate
{
    public static bool operator true(Gate value) => true;

    public static bool operator false(Gate value) => false;

    public static Gate operator &(Gate left, Gate right) => left;
}

static class Program
{
    static void Main()
    {
        var value = new Gate();
        Gate combined = value && value;
    }
}
