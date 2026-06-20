class Program
{
    static void Swap(ref int a, ref int b)
    {
        int tmp = a;
        a = b;
        b = tmp;
    }

    static void GetOut(out int x)
    {
        x = 42;
    }

    static void AddOne(in int n, out int result)
    {
        result = n + 1;
    }

    static void Main()
    {
        int a = 1;
        int b = 2;
        Swap(ref a, ref b);
        Console.WriteLine(a);
        Console.WriteLine(b);

        int x;
        GetOut(out x);
        Console.WriteLine(x);

        int r;
        AddOne(in a, out r);
        Console.WriteLine(r);
    }
}
