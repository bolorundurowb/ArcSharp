// An exception that no handler catches ends the program. The runtime prints
// the exception, releases what the unwind owned, prints the accounting line,
// and exits with code 70. The accounting line still shows live=0.
class Item
{
    public int Id;
}

class Program
{
    static void Fail(Item item)
    {
        Item second = new Item();
        throw new System.InvalidOperationException("nobody catches this");
    }

    static void Main()
    {
        Item first = new Item();
        Fail(first);
        Console.WriteLine("not reached");
    }
}
