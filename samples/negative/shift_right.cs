struct Custom
{
    public static Custom operator >>(Custom value, int count)
    {
        return value;
    }
}

static class Program
{
    static void Main()
    {
        Custom value = new Custom();
        value >>= 1;
    }
}
