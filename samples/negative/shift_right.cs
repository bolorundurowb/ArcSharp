struct Custom
{
    public static Custom operator >>(Custom value, int count)
    {
        return value;
    }
}

static class Program
{
    static Custom field;

    static Custom Value
    {
        get => field;
        set => field = value;
    }

    static void Main()
    {
        Value >>= 1;
    }
}
