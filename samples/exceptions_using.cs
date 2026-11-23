using System;

class Resource : IDisposable
{
    string name;
    bool failOnDispose;

    public Resource(string name, bool failOnDispose)
    {
        this.name = name;
        this.failOnDispose = failOnDispose;
        Console.WriteLine("open " + name);
    }

    public void Dispose()
    {
        Console.WriteLine("close " + name);
        if (failOnDispose)
        {
            throw new InvalidOperationException("dispose failed for " + name);
        }
    }
}

class Program
{
    static void Fail()
    {
        throw new ArgumentException("body failed");
    }

    static void DisposeDuringUnwind()
    {
        using (Resource outer = new Resource("outer", false))
        using (Resource inner = new Resource("inner", false))
        {
            Fail();
            Console.WriteLine("not reached");
        }
    }

    static void DisposeThrowsAfterBody()
    {
        using (Resource first = new Resource("first", false))
        using (Resource second = new Resource("second", true))
        {
            Console.WriteLine("body runs");
        }
    }

    static void DisposeThrowsDuringUnwind()
    {
        using (Resource keep = new Resource("keep", false))
        using (Resource bad = new Resource("bad", true))
        {
            Fail();
        }
    }

    static int ReturnFromUsing()
    {
        using (Resource r = new Resource("returned", false))
        {
            return 7;
        }
    }

    static void UsingInsideTry()
    {
        try
        {
            using (Resource r = new Resource("guarded", false))
            {
                Fail();
            }
        }
        catch (ArgumentException e)
        {
            Console.WriteLine("handled " + e.Message);
        }
        finally
        {
            Console.WriteLine("finally after using");
        }
    }

    static void Main()
    {
        try
        {
            DisposeDuringUnwind();
        }
        catch (ArgumentException e)
        {
            Console.WriteLine("1 " + e.Message);
        }

        try
        {
            DisposeThrowsAfterBody();
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine("2 " + e.Message);
        }

        try
        {
            DisposeThrowsDuringUnwind();
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine("3 " + e.Message);
        }

        Console.WriteLine("4 " + ReturnFromUsing());
        UsingInsideTry();
        Console.WriteLine("exceptions using PASS");
    }
}
