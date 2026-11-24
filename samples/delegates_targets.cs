using System;

interface IShape
{
    int Sides();
}

class Shape : IShape
{
    public virtual int Sides()
    {
        return 0;
    }

    public virtual string Name()
    {
        return "shape";
    }
}

class Triangle : Shape
{
    public override int Sides()
    {
        return 3;
    }

    public override string Name()
    {
        return "triangle";
    }
}

struct Point
{
    public int X;
    public int Y;
}

delegate int Measure();

delegate string Describe();

delegate void Swap(ref int left, ref int right);

delegate bool TryRead(out int value);

delegate Point Shift(Point point, int by);

delegate Shape Make(int sides);

delegate Item Chain(Item previous);

class Item
{
    public int Number;
}

class Program
{
    static void Exchange(ref int left, ref int right)
    {
        var held = left;
        left = right;
        right = held;
    }

    static bool Produce(out int value)
    {
        value = 41;
        return true;
    }

    static Point Move(Point point, int by)
    {
        var moved = new Point();
        moved.X = point.X + by;
        moved.Y = point.Y + by;
        return moved;
    }

    static Shape Build(int sides)
    {
        if (sides == 3)
        {
            return new Triangle();
        }

        return new Shape();
    }

    static Item Next(Item previous)
    {
        var item = new Item();
        item.Number = previous.Number + 1;
        return item;
    }

    static int Twice<T>(Func<T, int> measure, T value)
    {
        return measure(value) * 2;
    }

    static void Show(string text)
    {
        Console.WriteLine(text);
    }

    static int Length(string text)
    {
        return text.Length;
    }

    static void Main()
    {
        // A virtual method binds to the object's own override.
        Shape shape = new Triangle();
        Describe name = shape.Name;
        Measure virtualSides = shape.Sides;
        Console.WriteLine("virtual=" + name() + "/" + virtualSides());

        // An interface method binds through the interface table.
        IShape interfaced = new Triangle();
        Measure interfaceSides = interfaced.Sides;
        Console.WriteLine("interface=" + interfaceSides());

        // ref, out, struct and reference parameters and results.
        Swap swap = Exchange;
        var one = 1;
        var two = 2;
        swap(ref one, ref two);
        TryRead read = Produce;
        var produced = 0;
        var ok = read(out produced);
        Shift shift = Move;
        var start = new Point();
        start.X = 1;
        start.Y = 2;
        var moved = shift(start, 10);
        Make make = Build;
        Chain chain = Next;
        var seed = new Item();
        seed.Number = 7;
        Console.WriteLine("swap=" + one + "," + two + " out=" + produced + "/" + ok + " shift=" + moved.X + "," + moved.Y);
        Console.WriteLine("make=" + make(3).Name() + "/" + make(0).Name() + " chain=" + chain(chain(seed)).Number);

        // The shared Func and Action delegates, and generic methods that accept them.
        Func<string, int> length = Length;
        Console.WriteLine("generic=" + Twice(length, "abcd") + " func=" + length("xyz"));

        Action<string> print = Show;
        print("action=ok");
        Console.WriteLine("delegates targets PASS");
    }
}
