class Account
{
    public decimal Balance;
    public decimal Rate { get; set; }

    public decimal Interest()
    {
        return Balance * Rate;
    }
}

struct Price
{
    public decimal Amount;
    public int Quantity;

    public decimal Total()
    {
        return Amount * Quantity;
    }
}

static class Program
{
    const decimal TaxRate = 0.0825m;

    static int _failures;
    static long _sink;

    static void Check(string name, string actual, string expected)
    {
        if (actual != expected)
        {
            _failures++;
            Console.WriteLine("FAIL " + name + ": expected " + expected + ", got " + actual);
        }
    }

    static void CheckTrue(string name, bool actual)
    {
        if (!actual)
        {
            _failures++;
            Console.WriteLine("FAIL " + name);
        }
    }

    static decimal Half(decimal value)
    {
        return value / 2;
    }

    static void Arithmetic()
    {
        Check("add scale", (1.10m + 2.2m).ToString(), "3.30");
        Check("subtract to zero", (1.0m - 1.0m).ToString(), "0.0");
        Check("multiply scale", (1.5m * 2.50m).ToString(), "3.750");
        Check("divide exact", (10m / 4m).ToString(), "2.5");
        Check("divide keeps scale", (6.0m / 2m).ToString(), "3.0");
        Check("divide whole", (6m / 2m).ToString(), "3");
        Check("one third", (1m / 3m).ToString(), "0.3333333333333333333333333333");
        Check("two thirds", (2m / 3m).ToString(), "0.6666666666666666666666666667");
        Check("hundred thirds", (100m / 3m).ToString(), "33.333333333333333333333333333");
        Check("one seventh", (1m / 7m).ToString(), "0.1428571428571428571428571429");
        Check("one eighth", (1m / 8m).ToString(), "0.125");
        Check("negative divide", (-7m / 2m).ToString(), "-3.5");
        Check("remainder", (7.5m % 2m).ToString(), "1.5");
        Check("negative remainder", (-7.5m % 2m).ToString(), "-1.5");
        Check("remainder scale", (5.50m % 2m).ToString(), "1.50");
        Check("rounding multiply", (0.1234567890123456789012345678m * 0.1m).ToString(), "0.0123456789012345678901234568");
        Check("small product", (0.0000000001m * 0.0000000001m).ToString(), "0.00000000000000000001");
        Check("tiny product rounds away", (0.0000000000000001m * 0.0000000000000001m).ToString(), "0.0000000000000000000000000000");
        Check("negate", (-(3.20m)).ToString(), "-3.20");
        Check("negative literal", (-0.05m).ToString(), "-0.05");
        Check("leading zeros", 0.001m.ToString(), "0.001");
        Check("half helper", Half(5m).ToString(), "2.5");
        Check("order", (2m + 3m * 4m).ToString(), "14");
        Check("tax", (19.99m * 3m * (1m + TaxRate)).ToString(), "64.917525");
    }

    static void Limits()
    {
        Check("max", decimal.MaxValue.ToString(), "79228162514264337593543950335");
        Check("min", decimal.MinValue.ToString(), "-79228162514264337593543950335");
        Check("max minus one", (decimal.MaxValue - 1m).ToString(), "79228162514264337593543950334");
        Check("max halved", (decimal.MaxValue / 2m).ToString(), "39614081257132168796771975168");
        Check("max plus fraction", (decimal.MaxValue + 0.4m).ToString(), "79228162514264337593543950335");
        Check("one", decimal.One.ToString(), "1");
        Check("zero", decimal.Zero.ToString(), "0");

        var overflowed = false;
        try
        {
            decimal big = decimal.MaxValue;
            big = big + 1m;
        }
        catch (OverflowException)
        {
            overflowed = true;
        }

        CheckTrue("add overflow", overflowed);

        overflowed = false;
        try
        {
            decimal big = decimal.MaxValue;
            big = big * 2m;
        }
        catch (OverflowException)
        {
            overflowed = true;
        }

        CheckTrue("multiply overflow", overflowed);

        var divided = false;
        try
        {
            decimal zero = 0m;
            decimal broken = 1m / zero;
        }
        catch (DivideByZeroException)
        {
            divided = true;
        }

        CheckTrue("divide by zero", divided);

        divided = false;
        try
        {
            decimal zero = 0m;
            decimal broken = 1m % zero;
        }
        catch (DivideByZeroException)
        {
            divided = true;
        }

        CheckTrue("remainder by zero", divided);
    }

    static void Comparisons()
    {
        CheckTrue("equal scales", 1.0m == 1.00m);
        CheckTrue("not equal", 1.0m != 1.01m);
        CheckTrue("less", 1.5m < 2m);
        CheckTrue("less or equal", 2m <= 2.0m);
        CheckTrue("greater", 2m > -2m);
        CheckTrue("greater or equal", -1m >= -1.0m);
        CheckTrue("negative order", -3m < -2m);
        CheckTrue("zero signs", 0m == -0m);
        CheckTrue("with int", 3m == 3);
        CheckTrue("int on left", 2 < 2.5m);
        CheckTrue("different scales", 79228162514264337593543950335m > 0.0000000000000000000000000001m);
    }

    static void Conversions()
    {
        int whole = 7;
        long big = 3000000000;
        ulong biggest = 18446744073709551615;
        double tenth = 0.1;
        double hundred = 100.0;
        double digits = 123456789.123456789;
        double small = 0.00001;
        float half = 2.5f;
        float tenthFloat = 0.1f;
        double third = 1.0 / 3.0;
        double large = 1e28;
        double longer = 12345.678901234567;
        Check("from int", ((decimal)whole).ToString(), "7");
        Check("from long", ((decimal)big).ToString(), "3000000000");
        Check("from ulong", ((decimal)biggest).ToString(), "18446744073709551615");
        Check("from negative", ((decimal)(-12)).ToString(), "-12");
        Check("from char", ((decimal)'A').ToString(), "65");
        Check("implicit", (whole + 0.5m).ToString(), "7.5");
        Check("from double", ((decimal)tenth).ToString(), "0.1000000000000000055511151231");
        Check("from double whole", ((decimal)hundred).ToString(), "100");
        Check("from double digits", ((decimal)digits).ToString(), "123456789.12345679104328155518");
        Check("from small double", ((decimal)small).ToString(), "0.0000100000000000000008180305");
        Check("from third", ((decimal)third).ToString(), "0.3333333333333333148296162562");
        Check("from large double", ((decimal)large).ToString(), "9999999999999999583119736832");
        Check("from long double", ((decimal)longer).ToString(), "12345.678901234567092615179718");
        Check("from float digits", ((decimal)tenthFloat).ToString(), "0.100000001490116119384765625");
        Check("from float", ((decimal)half).ToString(), "2.5");
        Check("to int", "" + ((int)3.99m), "3");
        Check("to int negative", "" + ((int)-3.99m), "-3");
        Check("to long", "" + ((long)3000000000.5m), "3000000000");
        Check("to byte", "" + ((byte)255.9m), "255");
        Check("to uint", "" + ((uint)4000000000m), "4000000000");
        Check("to ulong", "" + ((ulong)18446744073709551615m), "18446744073709551615");
        Check("to short", "" + ((short)-32768m), "-32768");
        Check("to char", "" + (int)(char)66m, "66");
        CheckTrue("to double", (double)1.5m == 1.5);
        CheckTrue("to double fraction", (double)0.25m == 0.25);
        CheckTrue("to float", (float)0.5m == 0.5f);

        var outOfRange = 0;
        try
        {
            decimal over = 256m;
            _sink = (byte)over;
        }
        catch (OverflowException)
        {
            outOfRange++;
        }

        try
        {
            decimal over = 2147483648m;
            _sink = (int)over;
        }
        catch (OverflowException)
        {
            outOfRange++;
        }

        try
        {
            decimal under = -1m;
            _sink = (uint)under;
        }
        catch (OverflowException)
        {
            outOfRange++;
        }

        try
        {
            decimal under = -1m;
            _sink = (long)(ulong)under;
        }
        catch (OverflowException)
        {
            outOfRange++;
        }

        try
        {
            decimal over = 10000000000000000000m;
            _sink = (long)over;
        }
        catch (OverflowException)
        {
            outOfRange++;
        }

        Check("range errors", "" + outOfRange, "5");
    }

    static void Updates()
    {
        decimal d = 10m;
        d++;
        Check("increment", d.ToString(), "11");
        d--;
        d--;
        Check("decrement", d.ToString(), "9");
        decimal old = d++;
        Check("postfix value", old.ToString(), "9");
        Check("prefix value", (++d).ToString(), "11");
        d += 5;
        d -= 0.5m;
        d *= 2;
        d /= 4;
        d %= 5;
        Check("compound", d.ToString(), "2.75");
        decimal text = 1.5m;
        Check("concatenation", "total: " + text + "!", "total: 1.5!");
        Check("default", default(decimal).ToString(), "0");
        Check("new", new decimal().ToString(), "0");
        Check("new from int", new decimal(-5).ToString(), "-5");
        Check("new from long", new decimal(3000000000).ToString(), "3000000000");
    }

    static void Storage()
    {
        Account account = new Account();
        account.Balance = 1000.50m;
        account.Rate = 0.05m;
        Check("class members", account.Interest().ToString(), "50.0250");
        account.Rate += 0.01m;
        account.Rate++;
        Check("property update", account.Rate.ToString(), "1.06");
        Check("unary plus", (+account.Rate).ToString(), "1.06");

        Price price = new Price();
        price.Amount = 2.25m;
        price.Quantity = 4;
        Check("struct members", price.Total().ToString(), "9.00");

        decimal[] items = new decimal[4];
        items[0] = 1.1m;
        items[1] = 2.2m;
        items[2] = 3.3m;
        decimal sum = 0m;
        for (int i = 0; i < items.Length; i++)
        {
            sum += items[i];
        }

        Check("array sum", sum.ToString(), "6.6");
        items[3] += items[0] * 10;
        Check("array update", items[3].ToString(), "11.0");
    }

    static void Nullables()
    {
        decimal? value = 1.5m;
        decimal? none = null;
        value += 1;
        value++;
        Check("nullable update", value.Value.ToString(), "3.5");
        CheckTrue("nullable add absent", !(value + none).HasValue);
        CheckTrue("nullable compare", value > 3m && value == 3.50m && none != value && !(none < 1m));
        decimal? product = value * 2m;
        Check("nullable multiply", product.Value.ToString(), "7.0");
        none++;
        CheckTrue("nullable increment absent", !none.HasValue);
        decimal fallback = none ?? 9.5m;
        Check("coalesce", fallback.ToString(), "9.5");
        decimal? widened = 4;
        Check("nullable from int", widened.Value.ToString(), "4");
        Check("nullable negate", (-value).Value.ToString(), "-3.5");
    }

    static void Main()
    {
        Arithmetic();
        Limits();
        Comparisons();
        Conversions();
        Updates();
        Storage();
        Nullables();

        if (_failures == 0)
        {
            Console.WriteLine("decimals PASS");
        }
        else
        {
            Console.WriteLine("decimals FAIL");
        }
    }
}
