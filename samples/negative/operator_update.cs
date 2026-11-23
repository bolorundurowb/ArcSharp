class Gate
{
    public static Gate operator ++(Gate value) => value;
}

static class Program
{
    static void Main()
    {
        var gate = new Gate();
        gate++;
    }
}
