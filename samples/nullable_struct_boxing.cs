interface IShape
{
    int Area();
}

struct Square : IShape
{
    public int Side;

    public Square(int side)
    {
        Side = side;
    }

    public int Area()
    {
        return Side * Side;
    }
}

struct Other
{
    public int Value;

    public Other(int value)
    {
        Value = value;
    }
}

static class Program
{
    static void Main()
    {
        Square? present = new Square(4);
        Square? absent = null;

        object boxedPresent = present;
        object boxedAbsent = absent;
        bool boxing = boxedPresent != null && boxedAbsent == null;

        Square? roundTrip = (Square?)boxedPresent;
        Square? emptyRoundTrip = (Square?)boxedAbsent;
        bool unboxing = roundTrip.HasValue && roundTrip.Value.Side == 4 && !emptyRoundTrip.HasValue;

        // A boxed nullable is the box of the contained struct.
        Square plain = (Square)boxedPresent;
        bool plainUnbox = plain.Side == 4;
        object boxedPlain = new Square(6);
        Square? fromPlain = (Square?)boxedPlain;
        bool sameBox = fromPlain.Value.Side == 6;

        IShape shape = present;
        IShape none = absent;
        bool interfaces = shape.Area() == 16 && none == null;

        Square? asSquare = boxedPresent as Square?;
        Other? asOther = boxedPresent as Other?;
        Square? asNull = boxedAbsent as Square?;
        bool tryCast = asSquare.HasValue && asSquare.Value.Side == 4 && !asOther.HasValue && !asNull.HasValue;

        bool failed = false;
        try
        {
            Other? wrong = (Other?)boxedPresent;
            failed = wrong.HasValue;
        }
        catch (InvalidCastException)
        {
            failed = true;
        }

        if (boxing && unboxing && plainUnbox && sameBox && interfaces && tryCast && failed)
        {
            Console.WriteLine("nullable struct boxing PASS");
        }
        else
        {
            Console.WriteLine("nullable struct boxing FAIL");
            Console.WriteLine(boxing);
            Console.WriteLine(unboxing);
            Console.WriteLine(plainUnbox);
            Console.WriteLine(sameBox);
            Console.WriteLine(interfaces);
            Console.WriteLine(tryCast);
            Console.WriteLine(failed);
        }
    }
}
