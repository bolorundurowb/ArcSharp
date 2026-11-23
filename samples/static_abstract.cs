interface IMonoid<T> where T : IMonoid<T>
{
    static abstract T Identity();

    static abstract T operator +(T left, T right);

    static abstract T operator -(T value);

    static abstract string Label { get; }

    static abstract int Created { get; set; }

    static virtual int Weight()
    {
        return 1;
    }
}

class Total : IMonoid<Total>
{
    public int Amount;
    private static int _created;

    public static Total Make(int amount)
    {
        Total total = new Total();
        total.Amount = amount;
        _created++;
        return total;
    }

    public static Total Identity()
    {
        return Make(0);
    }

    public static Total operator +(Total left, Total right)
    {
        return Make(left.Amount + right.Amount);
    }

    public static Total operator -(Total value)
    {
        return Make(-value.Amount);
    }

    public static string Label
    {
        get { return "total"; }
    }

    public static int Created
    {
        get { return _created; }
        set { _created = value; }
    }

    public static int Weight()
    {
        return 5;
    }
}

struct Count : IMonoid<Count>
{
    public int Items;
    private static int _created;

    public static Count Identity()
    {
        Count zero = new Count();
        _created++;
        return zero;
    }

    public static Count operator +(Count left, Count right)
    {
        Count sum = new Count();
        sum.Items = left.Items + right.Items;
        _created++;
        return sum;
    }

    public static Count operator -(Count value)
    {
        Count negated = new Count();
        negated.Items = -value.Items;
        return negated;
    }

    public static string Label
    {
        get { return "count"; }
    }

    public static int Created
    {
        get { return _created; }
        set { _created = value; }
    }
}

static class Program
{
    static T Fold<T>(T first, T second, T third) where T : IMonoid<T>
    {
        return T.Identity() + first + second + third;
    }

    static string Describe<T>() where T : IMonoid<T>
    {
        return T.Label + ":" + T.Weight();
    }

    static int Reset<T>() where T : IMonoid<T>
    {
        T.Created = 0;
        return T.Created;
    }

    static void Main()
    {
        Total sum = Fold(Total.Make(1), Total.Make(2), Total.Make(3));
        Total negative = -sum;

        Count one = new Count();
        one.Items = 4;
        Count two = new Count();
        two.Items = 6;
        Count counted = Fold(one, two, one);

        bool classes = sum.Amount == 6 && negative.Amount == -6;
        bool structs = counted.Items == 14 && (-counted).Items == -14;
        bool labels = Describe<Total>() == "total:5" && Describe<Count>() == "count:1";
        bool properties = Total.Created > 0 && Reset<Total>() == 0 && Total.Created == 0
            && Reset<Count>() == 0;

        if (classes && structs && labels && properties)
        {
            Console.WriteLine("static abstract PASS");
        }
    }
}
