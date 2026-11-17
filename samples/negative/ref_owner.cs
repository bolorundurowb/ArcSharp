struct Point
{
    public int X;

    public void Shift()
    {
        X = X + 1;
    }
}

class Node
{
    public weak Node Other;
}

class Program
{
    static void Take(ref Node n) { }

    static void Write(in Point p)
    {
        p.X = 1;
        p.Shift();
    }

    static void Main()
    {
        Node n = new Node();
        Take(ref n.Other);
        Point p;
        p.X = 0;
        Write(in p);
    }
}
