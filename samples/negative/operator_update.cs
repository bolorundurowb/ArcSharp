class Gate
{
    public static Gate operator ++(Gate value) => value;
}

static class Program
{
    static Gate Update(Gate value)
    {
        value++;
        return value;
    }

    static void Main()
    {
    }
}
