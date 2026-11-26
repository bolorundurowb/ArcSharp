class Box
{
    public int V;
    public Box(int v) { V = v; }
}

class Program
{
    static long LongLength = 5;
    static uint UIntLength = 4;
    static ulong ULongLength = 3;
    static ulong HugeLength = 18446744073709551615UL;

    static void Main()
    {
        int[] a = new int[LongLength];
        long li = 4;
        a[li] = 40;
        a[li] += 2;
        uint ui = 2;
        a[ui] = 20;
        Box[] b = new Box[UIntLength];
        b[li - 1] = new Box(7);
        ulong ul = 3;
        string[] s = new string[ULongLength];
        s[ul - 1] = "z";
        s[ul - 1] += "y";
        string wide = a.Length + "," + a[li] + "," + a[ui] + "," + b.Length + "," + b[ul].V + "," + s.Length + "," + s[2];
        Console.WriteLine("wide=" + wide);

        bool outOfRange = false;
        try
        {
            long bad = 5;
            a[bad] = 1;
        }
        catch (IndexOutOfRangeException)
        {
            outOfRange = true;
        }

        bool overflow = false;
        try
        {
            int[] huge = new int[HugeLength];
            Console.WriteLine("huge length=" + huge.Length);
        }
        catch (OverflowException)
        {
            overflow = true;
        }

        if (wide == "5,42,20,4,7,3,zy" && outOfRange && overflow)
        {
            Console.WriteLine("arrays wide index PASS");
        }
    }
}
