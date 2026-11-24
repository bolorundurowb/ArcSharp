class Payload
{
    public int Code;

    public Payload(int code)
    {
        Code = code;
    }
}

struct Pair
{
    public Payload Item;
    public int Number;
}

interface ITransform
{
    Pair Apply(Pair input, int delta);
}

class Transformer : ITransform
{
    public Pair Apply(Pair input, int delta)
    {
        input.Number += delta;
        return input;
    }
}

static class Program
{
    static Pair Make()
    {
        Pair pair = new Pair();
        pair.Item = new Payload(1);
        pair.Number = 2;
        try
        {
            return pair;
        }
        finally
        {
            pair.Item = new Payload(10);
            pair.Number = 20;
        }
    }

    static int Reset(ref Pair pair)
    {
        pair.Item = null;
        pair.Number = 90;
        return 4;
    }

    static int Fail()
    {
        throw new InvalidOperationException();
    }

    static void Main()
    {
        Pair original = Make();
        ITransform transformer = new Transformer();
        Pair result = transformer.Apply(original, Reset(ref original));
        bool caught = false;
        try
        {
            transformer.Apply(result, Fail());
        }
        catch (InvalidOperationException)
        {
            caught = true;
        }

        if (result.Number == 6 && result.Item.Code == 1
            && original.Number == 90 && original.Item == null && caught)
        {
            Console.WriteLine("struct calls PASS");
        }
    }
}
