class Target
{
}

class Program
{
    static bool Alive(WeakReference<Target> weak)
    {
        return weak.TryGetTarget(out var target);
    }

    static void Main()
    {
        Target first = new Target();
        Target second = first;
        WeakReference<Target> weak = new WeakReference<Target>(first);
        bool before = Alive(weak);
        first = null;
        bool afterFirst = Alive(weak);
        second = null;
        bool afterLast = Alive(weak);
        if (before && afterFirst && !afterLast)
        {
            Console.WriteLine("destruction points PASS");
        }
    }
}
