using System;

class Box
{
    public int Value;

    public Box(int value)
    {
        Value = value;
    }
}

class Program
{
    static int Divide(int a, int b)
    {
        return a / b;
    }

    static Box Make(int value)
    {
        if (value < 0)
        {
            throw new ArgumentException("negative value");
        }

        return new Box(value);
    }

    static void Main()
    {
        try
        {
            throw new InvalidOperationException("first");
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine("caught " + e.Message);
        }

        try
        {
            Box b = Make(-1);
            Console.WriteLine("not reached");
        }
        catch (ArgumentException e)
        {
            Console.WriteLine("caught " + e.Message);
        }
        finally
        {
            Console.WriteLine("finally one");
        }

        try
        {
            Console.WriteLine(Divide(1, 0));
        }
        catch (DivideByZeroException)
        {
            Console.WriteLine("divide by zero");
        }

        try
        {
            try
            {
                throw new NotSupportedException("inner");
            }
            catch (ArgumentException)
            {
                Console.WriteLine("wrong handler");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("outer caught " + e.Message);
        }
    }
}
