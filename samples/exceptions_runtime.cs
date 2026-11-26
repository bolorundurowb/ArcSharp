using System;

class Animal
{
}

class Dog : Animal
{
}

class Cat : Animal
{
}

class Cell
{
    public int Value;

    public int Read()
    {
        return Value;
    }
}

class Wrapper
{
    public Cell Inner = null;
}

class Program
{
    static int Zero = 0;
    static int MinusOne = -1;

    static void Main()
    {
        int[] numbers = new int[3];
        try
        {
            numbers[3] = 1;
            Console.WriteLine("not reached");
        }
        catch (IndexOutOfRangeException e)
        {
            Console.WriteLine("index: " + e.Message);
        }

        try
        {
            Animal pet = new Cat();
            Dog dog = (Dog)pet;
            Console.WriteLine("not reached");
        }
        catch (InvalidCastException e)
        {
            Console.WriteLine("cast: " + e.Message);
        }

        Cell none = null;
        try
        {
            int value = none.Value;
            Console.WriteLine("not reached");
        }
        catch (NullReferenceException)
        {
            Console.WriteLine("null field read");
        }

        try
        {
            none.Value = 4;
            Console.WriteLine("not reached");
        }
        catch (NullReferenceException)
        {
            Console.WriteLine("null field write");
        }

        try
        {
            int value = none.Read();
            Console.WriteLine("not reached");
        }
        catch (NullReferenceException)
        {
            Console.WriteLine("null call");
        }

        int[] missing = null;
        try
        {
            int length = missing.Length;
            Console.WriteLine("not reached");
        }
        catch (NullReferenceException)
        {
            Console.WriteLine("null length");
        }

        try
        {
            int first = missing[0];
            Console.WriteLine("not reached");
        }
        catch (NullReferenceException)
        {
            Console.WriteLine("null element");
        }

        try
        {
            int quotient = 10 / Zero;
            Console.WriteLine("not reached");
        }
        catch (DivideByZeroException e)
        {
            Console.WriteLine("divide: " + e.Message);
        }

        try
        {
            int remainder = 10 % Zero;
            Console.WriteLine("not reached");
        }
        catch (DivideByZeroException)
        {
            Console.WriteLine("remainder");
        }

        try
        {
            long big = 10;
            long quotient = big / Zero;
            Console.WriteLine("not reached");
        }
        catch (DivideByZeroException)
        {
            Console.WriteLine("long divide");
        }

        try
        {
            int smallest = -2147483647 - 1;
            int quotient = smallest / MinusOne;
            Console.WriteLine("not reached");
        }
        catch (OverflowException e)
        {
            Console.WriteLine("overflow: " + e.Message);
        }

        try
        {
            Animal[] pets = new Dog[1];
            pets[0] = new Cat();
            Console.WriteLine("not reached");
        }
        catch (ArrayTypeMismatchException)
        {
            Console.WriteLine("array type mismatch");
        }

        try
        {
            int[] negative = new int[MinusOne];
            Console.WriteLine("not reached");
        }
        catch (OverflowException)
        {
            Console.WriteLine("negative array length");
        }

        try
        {
            string[] negative = new string["x".Length * MinusOne];
            Console.WriteLine("not reached");
        }
        catch (OverflowException)
        {
            Console.WriteLine("negative reference array length");
        }

        try
        {
            throw null;
        }
        catch (NullReferenceException)
        {
            Console.WriteLine("throw null");
        }

        try
        {
            Wrapper chain = new Wrapper();
            chain.Inner.Value = 1;
            Console.WriteLine("not reached");
        }
        catch (SystemException e)
        {
            Console.WriteLine("system exception base: " + e.Message);
        }

        Console.WriteLine("exceptions runtime PASS");
    }
}
