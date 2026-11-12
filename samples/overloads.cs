class Box
{
    public int Kind(int n)
    {
        return n + 1;
    }

    public int Kind(string s)
    {
        return s.Length;
    }
}

class Program
{
    static string Show(int n)
    {
        return "int=" + n;
    }

    static string Show(string s)
    {
        return "str=" + s;
    }

    static void Main()
    {
        Box b = new Box();
        Console.WriteLine(Show(7) + " " + Show("ok") + " " + b.Kind(3) + " " + b.Kind("abcd"));
    }
}
