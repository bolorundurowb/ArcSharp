static class Program
{
    static void Main()
    {
        int errors = 0;
        int high = 2147483647;
        ulong large = 18446744073709551615UL;
        long low = -9223372036854775808L;
        byte narrow = 255;
        int negative = -1;
        double floating = 256.0;
        double fraction = -0.75;

        try
        {
            high = checked(high + 1);
        }
        catch (OverflowException)
        {
            errors++;
        }

        try
        {
            checked { large *= 2; }
        }
        catch (OverflowException)
        {
            errors++;
        }

        try
        {
            low = checked(-low);
        }
        catch (OverflowException)
        {
            errors++;
        }

        try
        {
            checked { narrow++; }
        }
        catch (OverflowException)
        {
            errors++;
        }

        try
        {
            Console.WriteLine(checked((uint)negative));
        }
        catch (OverflowException)
        {
            errors++;
        }

        try
        {
            Console.WriteLine(checked((byte)floating));
        }
        catch (OverflowException)
        {
            errors++;
        }

        checked
        {
            if (errors == 6 && narrow == 255 && large == 18446744073709551615UL
                && low == -9223372036854775808L && (uint)fraction == 0
                && unchecked(high + 1) == -2147483648)
            {
                Console.WriteLine("checked PASS");
            }
        }
    }
}
