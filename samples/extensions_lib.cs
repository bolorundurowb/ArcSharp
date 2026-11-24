struct Pair
{
    public int First;
    public int Second;
}

// The two halves of Crate live in different files and are merged by Roslyn.
partial class Crate
{
    public int Items;
    public string Label;

    public void Fill(int count)
    {
        Items = count;
    }
}

partial class Crate
{
    public int Weigh()
    {
        return Items;
    }

    public string Describe()
    {
        return Label + ":" + Items;
    }
}

static class Extensions
{
    public static int Total(this Crate crate)
    {
        return crate.Weigh();
    }

    public static int Plus(this int value, int amount)
    {
        return value + amount;
    }

    public static int Times(this int value, int factor)
    {
        return value * factor;
    }

    public static string Shout(this string text)
    {
        return text + "!";
    }

    public static int Sum(this Pair pair)
    {
        return pair.First + pair.Second;
    }
}
