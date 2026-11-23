// A chain long enough to overflow a stack that releases one frame per link.
// Dropping the head destroys every node, and every finaliser runs, without
// recursing on the call stack. The nodes form a list, not a cycle, so
// ARC3100 warns about the type and nothing leaks.
class Log
{
    public static int finalised;
}

class Node
{
    public Node next;

    ~Node()
    {
        Log.finalised = Log.finalised + 1;
    }
}

class Program
{
    static void Build(int count)
    {
        Node head = null;
        for (int i = 0; i < count; i = i + 1)
        {
            Node node = new Node();
            node.next = head;
            head = node;
        }

        Console.WriteLine("built " + count);
    }

    static void Main()
    {
        Build(100000);
        Console.WriteLine("deep graph finalised " + Log.finalised);
    }
}
