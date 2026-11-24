static class Program
{
    static int calls;

    static bool Check()
    {
        calls += 1;
        return true;
    }

    static void Main()
    {
        int value = 1;
        value <<= 33;
        value |= 4;
        value ^= 1;
        value &= 7;
        int negative = -8;
        int arithmetic = negative >> 1;
        negative >>>= 1;
        long wide = 1;
        wide <<= -1;
        ulong unsigned = 9223372036854775808UL;
        unsigned >>= 63;
        bool eager = false & Check();
        eager |= Check();
        eager ^= true;

        if (value == 7 && ~value == -8 && arithmetic == -4 && negative == 2147483644
            && wide < 0 && unsigned == 1 && calls == 2 && !eager)
        {
            Console.WriteLine("bitwise PASS");
        }
    }
}
