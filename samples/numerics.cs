class Program
{
    static void Main()
    {
        byte b = 200;
        sbyte sb = -50;
        short s = -1000;
        ushort us = 50000;
        uint ui = 3000000000u;
        ulong ul = 10000000000ul;

        Console.WriteLine(b);
        Console.WriteLine(sb);
        Console.WriteLine(s);
        Console.WriteLine(us);
        Console.WriteLine(ui);
        Console.WriteLine(ul);

        uint big = 4000000000u;
        uint half = big / 2u;
        Console.WriteLine(half);

        ulong prod = ul * 2ul;
        Console.WriteLine(prod);

        int cast = (int)b + sb;
        Console.WriteLine(cast);

        bool gt = ui > 1000000000u;
        Console.WriteLine(gt);
    }
}
