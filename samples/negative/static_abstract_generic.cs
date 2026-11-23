interface IConvert
{
    static abstract T Make<T>();
}

class Maker : IConvert
{
    public static T Make<T>()
    {
        return default(T);
    }
}

static class Program
{
    static int Build<C>() where C : IConvert
    {
        return C.Make<int>();
    }

    static void Main()
    {
        int value = Build<Maker>();
    }
}
