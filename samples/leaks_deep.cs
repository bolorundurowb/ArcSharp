using System;

class Link
{
    public Link Next;
}

class Ring
{
    public Ring Next;
}

class Program
{
    static Link head;

    static void BuildChain(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var link = new Link();
            link.Next = head;
            head = link;
        }
    }

    static void BuildRing(int count)
    {
        var first = new Ring();
        var last = first;
        for (int i = 1; i < count; i++)
        {
            var ring = new Ring();
            last.Next = ring;
            last = ring;
        }

        last.Next = first;
    }

    static void Main()
    {
        BuildChain(10000);
        BuildRing(10000);
        Console.WriteLine("deep leaks built");
    }
}
