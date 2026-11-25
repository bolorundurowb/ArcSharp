class Tracked
{
    public static int Finalised;

    ~Tracked()
    {
        Finalised++;
    }
}

struct Result
{
    public Tracked Value;

    public Result(Tracked value)
    {
        Value = value;
    }
}

class Program
{
    static Tracked ReturnReference()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                try
                {
                    return new Tracked();
                }
                finally
                {
                    throw new InvalidOperationException();
                }
            }
            catch (InvalidOperationException)
            {
                if (Tracked.Finalised != i + 1)
                {
                    throw new Exception("reference survived into the handler");
                }
            }
        }

        return null;
    }

    static Result ReturnStruct()
    {
        try
        {
            try
            {
                return new Result(new Tracked());
            }
            finally
            {
                throw new InvalidOperationException();
            }
        }
        catch (InvalidOperationException)
        {
            if (Tracked.Finalised != 4)
            {
                throw new Exception("struct field survived into the handler");
            }
        }

        return default;
    }

    static void Main()
    {
        ReturnReference();
        ReturnStruct();
        Console.WriteLine("abandoned returns PASS");
    }
}
