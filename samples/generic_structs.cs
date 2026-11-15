using System;

struct Pair<T>
{
    public T Item;
    public long Tag;
}

struct Wide
{
    public string Name;
    public long X;
    public long Y;
}

class Holder<T>
{
    public T Item;
    public int Sentinel;
}

class Program
{
    static void Copy<T>(ref T target, in T source)
    {
        target = source;
    }

    static void Main()
    {
        Pair<string> a;
        a.Item = "first";
        a.Tag = 10;
        Pair<string> b = a;
        a.Item = "second";
        Wide w;
        w.Name = "wide";
        w.X = 20;
        w.Y = 30;
        var holder = new Holder<Wide>();
        holder.Item = w;
        holder.Sentinel = 99;
        Pair<Wide> nested;
        nested.Item = w;
        nested.Tag = 40;
        Pair<Wide> copied = new Pair<Wide>();
        Copy<Pair<Wide>>(ref copied, in nested);
        nested.Item.Name = "changed";
        Console.WriteLine("copies=" + b.Item + "," + a.Item + "," + copied.Item.Name);
        Console.WriteLine("layout=" + holder.Item.X + "," + holder.Item.Y + "," + holder.Sentinel + "," + copied.Tag);
    }
}
