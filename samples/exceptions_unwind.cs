using System;

class Marker
{
}

class Item
{
    public int Id;
    public Marker Peer;

    public Item(int id)
    {
        Id = id;
    }
}

class Fragile
{
    public Item Held;

    public Fragile(Item item, bool fail)
    {
        Held = item;
        if (fail)
        {
            throw new InvalidOperationException("constructor failed");
        }
    }
}

class Holder
{
    public Item Slot;
    public Item[] Items = new Item[2];
}

class Program
{
    static int stage;

    static Item Build(int id)
    {
        return new Item(id);
    }

    static void Fail(string why)
    {
        throw new ArgumentException(why);
    }

    // The new Item is an argument temporary when Fail throws.
    static void FailWithTemporary()
    {
        Take(new Item(1), Fail2());
    }

    static Item Fail2()
    {
        throw new NotSupportedException("argument failed");
    }

    static void Take(Item first, Item second)
    {
        stage = 99;
    }

    static void LocalsAcrossFrames()
    {
        Item a = new Item(10);
        Item b = new Item(11);
        a.Peer = new Marker();
        Deeper(a);
    }

    static void Deeper(Item item)
    {
        Item copy = new Item(item.Id + 1);
        copy.Peer = new Marker();
        Fail("from deeper");
    }

    static void ConstructorThrows()
    {
        Fragile f = new Fragile(new Item(20), true);
        stage = 98;
    }

    static void StoreThenThrow()
    {
        Holder h = new Holder();
        h.Slot = new Item(30);
        h.Items[0] = new Item(31);
        h.Items[5] = new Item(32);
    }

    static void ConcatThrows()
    {
        string s = "left " + Describe(new Item(40)) + FailString();
        stage = 97;
    }

    static string Describe(Item item)
    {
        return "item" + item.Id;
    }

    static string FailString()
    {
        throw new InvalidOperationException("concat failed");
    }

    static Item ReturnInTryFinally()
    {
        Item result = new Item(50);
        try
        {
            return result;
        }
        finally
        {
            Console.WriteLine("finally after return");
        }
    }

    static Item FinallyThrowsAfterReturn()
    {
        try
        {
            return new Item(60);
        }
        finally
        {
            Fail("finally replaced the return");
        }
    }

    static void NestedFinally()
    {
        Item outer = new Item(70);
        try
        {
            Item inner = new Item(71);
            try
            {
                Fail("nested");
            }
            finally
            {
                Console.WriteLine("inner finally " + inner.Id);
            }
        }
        finally
        {
            Console.WriteLine("outer finally " + outer.Id);
        }
    }

    static void Rethrow()
    {
        try
        {
            Fail("original");
        }
        catch (ArgumentException e)
        {
            Console.WriteLine("rethrowing " + e.Message);
            throw;
        }
    }

    static void ThrowCaught(Exception e)
    {
        throw e;
    }

    static void CatchThenThrowNew()
    {
        try
        {
            Fail("first");
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException("second after " + e.Message);
        }
    }

    static void FinallyThrowReplaces()
    {
        try
        {
            Fail("lost");
        }
        finally
        {
            throw new NotImplementedException("replacement");
        }
    }

    static int Recover()
    {
        try
        {
            return 1;
        }
        catch (Exception)
        {
            return 2;
        }
        finally
        {
            Console.WriteLine("recover finally");
        }
    }

    static void Main()
    {
        try
        {
            FailWithTemporary();
        }
        catch (NotSupportedException e)
        {
            Console.WriteLine("1 " + e.Message + " stage=" + stage);
        }

        try
        {
            LocalsAcrossFrames();
        }
        catch (ArgumentException e)
        {
            Console.WriteLine("2 " + e.Message);
        }

        try
        {
            ConstructorThrows();
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine("3 " + e.Message + " stage=" + stage);
        }

        try
        {
            StoreThenThrow();
        }
        catch (IndexOutOfRangeException e)
        {
            Console.WriteLine("4 " + e.Message);
        }

        try
        {
            ConcatThrows();
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine("5 " + e.Message);
        }

        Item kept = ReturnInTryFinally();
        Console.WriteLine("6 kept " + kept.Id);

        try
        {
            Item lost = FinallyThrowsAfterReturn();
            Console.WriteLine("not reached");
        }
        catch (ArgumentException e)
        {
            Console.WriteLine("7 " + e.Message);
        }

        try
        {
            NestedFinally();
        }
        catch (ArgumentException e)
        {
            Console.WriteLine("8 " + e.Message);
        }

        try
        {
            Rethrow();
        }
        catch (ArgumentException e)
        {
            Console.WriteLine("9 " + e.Message);
        }

        try
        {
            CatchThenThrowNew();
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine("10 " + e.Message);
        }

        try
        {
            FinallyThrowReplaces();
        }
        catch (NotImplementedException e)
        {
            Console.WriteLine("11 " + e.Message);
        }

        try
        {
            ThrowCaught(new ArgumentNullException("stored"));
        }
        catch (ArgumentException e)
        {
            Console.WriteLine("12 " + e.Message);
        }

        Console.WriteLine("13 recover " + Recover());
        Console.WriteLine("exceptions unwind PASS");
    }
}
