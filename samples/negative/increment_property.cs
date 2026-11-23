static class Program
{
    static int field;

    static int Value
    {
        get
        {
            return field;
        }
        set
        {
            field = value;
        }
    }

    static void Main()
    {
        Value++;
    }
}
