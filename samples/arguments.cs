class Pair
{
    public int Value;

    public Pair(int a, int b)
    {
        Value = a * 10 + b;
    }
}

class Item
{
}

static class Program
{
    static int order;

    static int Mark(int value)
    {
        order = order * 10 + value;
        return value;
    }

    static int Combine(int a = 5, int b = 6)
    {
        return a * 10 + b;
    }

    static int Read(in int value = 7)
    {
        return value;
    }

    static int Sum(params int[] values)
    {
        int total = 0;
        foreach (int value in values)
        {
            total += value;
        }

        return total;
    }

    static void Consume(params Item[] values)
    {
        Console.WriteLine("unexpected");
    }

    static Item Fail()
    {
        throw new InvalidOperationException();
    }

    static void Main()
    {
        int explicitCall = Combine(b: Mark(2), a: Mark(1));
        var pair = new Pair(b: Mark(3), a: Mark(4));
        int defaults = Combine(b: 8);
        int[] existing = new int[1];
        existing[0] = 16;
        bool caught = false;
        try
        {
            Consume(new Item(), Fail());
        }
        catch (InvalidOperationException)
        {
            caught = true;
        }

        if (explicitCall == 12 && pair.Value == 43 && order == 2134
            && defaults == 58 && Read() == 7 && Read(9) == 9
            && Sum(2, 3, 4) == 9 && Sum() == 0 && Sum(existing) == 16 && caught)
        {
            Console.WriteLine("arguments PASS");
        }
    }
}
