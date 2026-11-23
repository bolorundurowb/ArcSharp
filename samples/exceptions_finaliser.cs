using System;

class Log
{
    public static int childFinalised;
    public static int quietFinalised;
}

class Child
{
    ~Child()
    {
        Log.childFinalised = Log.childFinalised + 1;
        Console.WriteLine("child finalised");
    }
}

class Parent
{
    public Child Part;

    ~Parent()
    {
        Console.WriteLine("parent finaliser throws");
        throw new InvalidOperationException("finaliser failed");
    }
}

class Quiet
{
    ~Quiet()
    {
        try
        {
            throw new ArgumentException("handled inside");
        }
        catch (ArgumentException e)
        {
            Console.WriteLine("quiet caught " + e.Message);
        }

        Log.quietFinalised = Log.quietFinalised + 1;
    }
}

class Program
{
    static void Drop()
    {
        Parent parent = new Parent();
        parent.Part = new Child();
    }

    static void UnwindThroughFinaliser()
    {
        Parent parent = new Parent();
        parent.Part = new Child();
        throw new NotSupportedException("original survives");
    }

    static void Main()
    {
        Drop();
        Console.WriteLine("after drop: child=" + Log.childFinalised);

        try
        {
            UnwindThroughFinaliser();
        }
        catch (NotSupportedException e)
        {
            Console.WriteLine("caught " + e.Message + " child=" + Log.childFinalised);
        }

        Quiet quiet = new Quiet();
        quiet = null;
        Console.WriteLine("quiet=" + Log.quietFinalised);
        Console.WriteLine("exceptions finaliser PASS");
    }
}
