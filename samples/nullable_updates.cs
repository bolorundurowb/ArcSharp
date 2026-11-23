struct Money
{
    public int Cents;

    public Money(int cents)
    {
        Cents = cents;
    }

    public static Money operator +(Money a, Money b)
    {
        return new Money(a.Cents + b.Cents);
    }

    public static Money operator -(Money a)
    {
        return new Money(-a.Cents);
    }

    public static Money operator ++(Money a)
    {
        return new Money(a.Cents + 1);
    }

    public static bool operator ==(Money a, Money b)
    {
        return a.Cents == b.Cents;
    }

    public static bool operator !=(Money a, Money b)
    {
        return a.Cents != b.Cents;
    }

    public static bool operator <(Money a, Money b)
    {
        return a.Cents < b.Cents;
    }

    public static bool operator >(Money a, Money b)
    {
        return a.Cents > b.Cents;
    }

    public static implicit operator int(Money m)
    {
        return m.Cents;
    }

    public static explicit operator Money(int cents)
    {
        return new Money(cents);
    }
}

class Counter
{
    public int? Hits;
    public int? Stored { get; set; }

    private int? _backing;

    public int? Guarded
    {
        get { return _backing; }
        set { _backing = value; }
    }
}

static class Program
{
    static int _reads;

    static int Index()
    {
        _reads++;
        return 1;
    }

    static void Main()
    {
        int? count = 5;
        int? before = count++;
        int? after = ++count;
        bool incrementing = before == 5 && after == 7 && count == 7;

        count--;
        --count;
        bool decrementing = count == 5;

        int? none = null;
        none++;
        int? stillNone = none--;
        bool absent = !none.HasValue && !stillNone.HasValue;

        int? total = 10;
        total += 5;
        total -= 3;
        total *= 2;
        total /= 4;
        total %= 4;
        bool arithmetic = total == 2;

        total += none;
        bool propagates = !total.HasValue;

        int? shifted = 3;
        shifted <<= 4;
        shifted >>= 1;
        shifted |= 1;
        shifted &= 0x3d;
        shifted ^= 5;
        bool bits = shifted == 0x1c;

        byte? small = 250;
        small += 10;
        bool narrow = small == 4;

        long? wide = 1;
        wide += 4000000000;
        wide++;
        bool widening = wide == 4000000002;

        double? ratio = 1.5;
        ratio *= 2;
        ratio++;
        bool floating = ratio == 4.0;

        bool? flag = true;
        flag &= false;
        bool? other = null;
        other |= true;
        bool? unknown = null;
        unknown &= true;
        bool booleans = flag == false && other == true && !unknown.HasValue;

        int? mixed = 1;
        mixed += 1L > 0 ? 2 : 0;
        bool operands = mixed == 3;

        int?[] values = new int?[3];
        values[1] = 1;
        values[Index()]++;
        values[Index()] += 10;
        bool oneEvaluation = values[1] == 12 && _reads == 2 && !values[0].HasValue;

        Counter counter = new Counter();
        counter.Hits = 1;
        counter.Hits++;
        counter.Stored = 4;
        counter.Stored *= 3;
        counter.Guarded = 7;
        counter.Guarded++;
        counter.Guarded += 2;
        bool members = counter.Hits == 2 && counter.Stored == 12 && counter.Guarded == 10;

        Money? cash = new Money(100);
        Money? tip = new Money(25);
        Money? nothing = null;
        Money? sum = cash + tip;
        bool userBinary = sum.Value.Cents == 125 && !(cash + nothing).HasValue;
        bool userUnary = (-cash).Value.Cents == -100 && !(-nothing).HasValue;
        bool userCompare = cash == new Money(100) && cash != tip && tip < cash && !(cash < nothing)
            && !(cash > nothing) && nothing == null && cash != nothing;

        cash += tip;
        cash++;
        nothing += tip;
        bool userUpdates = cash.Value.Cents == 126 && !nothing.HasValue;

        Money? held = new Money(7);
        int? asInt = held;
        int? asNone = nothing;
        int exact = (int)held;
        Money? built = (Money?)5;
        int? wrapped = new Money(4);
        bool conversions = asInt == 7 && !asNone.HasValue && exact == 7 && built.Value.Cents == 5 && wrapped == 4;

        bool all = incrementing && decrementing && absent && arithmetic && propagates && bits && narrow
            && widening && floating && booleans && operands && oneEvaluation && members
            && userBinary && userUnary && userCompare && userUpdates && conversions;
        if (all)
        {
            Console.WriteLine("nullable updates PASS");
        }
        else
        {
            Console.WriteLine("nullable updates FAIL");
            Console.WriteLine(incrementing);
            Console.WriteLine(decrementing);
            Console.WriteLine(absent);
            Console.WriteLine(arithmetic);
            Console.WriteLine(propagates);
            Console.WriteLine(bits);
            Console.WriteLine(narrow);
            Console.WriteLine(widening);
            Console.WriteLine(floating);
            Console.WriteLine(booleans);
            Console.WriteLine(operands);
            Console.WriteLine(oneEvaluation);
            Console.WriteLine(members);
            Console.WriteLine(userBinary);
            Console.WriteLine(userUnary);
            Console.WriteLine(userCompare);
            Console.WriteLine(userUpdates);
            Console.WriteLine(conversions);
        }
    }
}
