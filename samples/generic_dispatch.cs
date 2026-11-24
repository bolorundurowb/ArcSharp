class Node
{
    public int Id;

    public Node(int id)
    {
        Id = id;
    }
}

interface IMapper
{
    T Pick<T>(T first, T second);
}

interface IPicker<K>
{
    V Pair<V>(K key, V value);
}

class LastMapper : IMapper
{
    public T Pick<T>(T first, T second)
    {
        return second;
    }
}

class FirstMapper : IMapper
{
    public T Pick<T>(T first, T second)
    {
        return first;
    }
}

class IntPicker : IPicker<int>
{
    public int Calls;

    public V Pair<V>(int key, V value)
    {
        Calls += key;
        return value;
    }
}

class Shape
{
    public virtual T Echo<T>(T value)
    {
        return value;
    }

    public virtual string Name()
    {
        return "shape";
    }
}

class Round : Shape
{
    public int Seen;

    public override T Echo<T>(T value)
    {
        Seen++;
        return value;
    }
}

class Ball : Round
{
    public override T Echo<T>(T value)
    {
        Seen += 10;
        return value;
    }
}

class Flat : Shape
{
}

abstract class Maker
{
    public abstract T Make<T>(T seed);
}

class Doubler : Maker
{
    public Shape Helper = new Round();
    public double Last;

    public override T Make<T>(T seed)
    {
        // Echo<double> is first used here, after the override was resolved.
        Last = Helper.Echo(2.5);
        return seed;
    }
}

class Holder<S>
{
    public S Stored;

    public virtual U Convert<U>(U extra)
    {
        return extra;
    }
}

class Counted<S> : Holder<S>
{
    public int Converted;

    public override U Convert<U>(U extra)
    {
        Converted++;
        return extra;
    }
}

static class Program
{
    static U Use<M, U>(M mapper, U first, U second) where M : IMapper
    {
        return mapper.Pick(first, second);
    }

    static void Main()
    {
        IMapper last = new LastMapper();
        IMapper first = new FirstMapper();
        Node a = new Node(1);
        Node b = new Node(2);
        bool interfaces = last.Pick(1, 2) == 2 && first.Pick(1, 2) == 1
            && last.Pick("x", "y") == "y" && first.Pick("x", "y") == "x"
            && last.Pick(a, b).Id == 2 && first.Pick(a, b).Id == 1;

        IPicker<int> picker = new IntPicker();
        bool generic = picker.Pair(3, "three") == "three" && picker.Pair(4, b).Id == 2
            && ((IntPicker)picker).Calls == 7;

        Shape plain = new Shape();
        Shape round = new Round();
        Shape ball = new Ball();
        Shape flat = new Flat();
        bool overrides = plain.Echo(5) == 5 && round.Echo(6) == 6 && ball.Echo("ball") == "ball"
            && flat.Echo(a).Id == 1 && round.Echo(a).Id == 1 && ((Round)round).Seen == 2
            && ((Round)ball).Seen == 10;

        Maker maker = new Doubler();
        bool abstractMember = maker.Make(8) == 8 && maker.Make(b).Id == 2
            && ((Doubler)maker).Last == 2.5 && ((Round)((Doubler)maker).Helper).Seen == 2;

        Holder<string> holder = new Counted<string>();
        holder.Stored = "kept";
        bool genericOwner = holder.Convert(4) == 4 && holder.Convert("s") == "s"
            && ((Counted<string>)holder).Converted == 2 && holder.Stored == "kept";

        bool constrained = Use(last, 5, 9) == 9 && Use(first, a, b).Id == 1
            && Use(new LastMapper(), "p", "q") == "q";

        if (interfaces && generic && overrides && abstractMember && genericOwner && constrained)
        {
            Console.WriteLine("generic dispatch PASS");
        }
    }
}
