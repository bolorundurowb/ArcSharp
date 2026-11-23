// Finalisers in a richer object graph. Inside a finaliser the object is already
// dead to weak references, but it can still call its own methods and release
// its children, and a child released that way is finalised at that statement.
// Array elements are finalised in index order, and a finaliser on a generic
// class runs for each closed type.
class Log
{
    public static string text = "";
}

class Inner
{
    public int id;

    ~Inner()
    {
        Log.text = Log.text + "inner" + id + ";";
    }
}

class Outer
{
    public Inner held;
    public WeakReference<Outer> self;

    public void Note(string what)
    {
        Log.text = Log.text + what;
    }

    ~Outer()
    {
        Note("outer-start;");
        Outer seen;
        if (self.TryGetTarget(out seen))
        {
            Note("weak-visible;");
        }
        else
        {
            Note("weak-gone;");
        }

        held = null;
        Note("outer-end;");
    }
}

class Box<T> where T : class
{
    public T value;

    ~Box()
    {
        Log.text = Log.text + "box;";
    }
}

class Program
{
    static bool Expect(string want)
    {
        bool same = Log.text == want;
        if (!same)
        {
            Console.WriteLine("want " + want + " got " + Log.text);
        }

        Log.text = "";
        return same;
    }

    static void DropOuter()
    {
        Outer outer = new Outer();
        outer.self = new WeakReference<Outer>(outer);
        outer.held = new Inner();
        outer.held.id = 7;
    }

    static void DropArray()
    {
        Inner[] items = new Inner[3];
        for (int i = 0; i < 3; i = i + 1)
        {
            items[i] = new Inner();
            items[i].id = i + 1;
        }
    }

    static void DropGeneric()
    {
        Box<Inner> box = new Box<Inner>();
        box.value = new Inner();
        box.value.id = 9;
    }

    static void Main()
    {
        DropOuter();
        bool outer = Expect("outer-start;weak-gone;inner7;outer-end;");
        DropArray();
        bool array = Expect("inner1;inner2;inner3;");
        DropGeneric();
        bool generic = Expect("box;inner9;");
        if (outer && array && generic)
        {
            Console.WriteLine("finaliser graph PASS");
        }

        Log.text = null;
    }
}
