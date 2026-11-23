// Finalisers run at the last strong release, derived class before base, and
// before the object's children are released. A child is finalised after its
// parent's whole chain has returned. The log records the order, and the program
// compares it with the order the memory model specifies.
class Log
{
    public static string text = "";
}

class Child
{
    public int id;

    ~Child()
    {
        Log.text = Log.text + "child" + id + ";";
    }
}

class Base
{
    public Child shared;

    ~Base()
    {
        // The children are still reachable: nothing has been released yet.
        Log.text = Log.text + "base(shared=" + shared.id + ");";
    }
}

class Derived : Base
{
    public Child own;

    ~Derived()
    {
        Log.text = Log.text + "derived(own=" + own.id + ",shared=" + shared.id + ");";
    }
}

// A class between a finaliser and the root contributes nothing of its own.
class Middle : Derived
{
}

class Leaf : Middle
{
    ~Leaf()
    {
        Log.text = Log.text + "leaf;";
    }
}

class Program
{
    static void Build()
    {
        Leaf leaf = new Leaf();
        leaf.shared = new Child();
        leaf.shared.id = 1;
        leaf.own = new Child();
        leaf.own.id = 2;
        Log.text = Log.text + "built;";
    }

    static void Main()
    {
        Build();
        Log.text = Log.text + "returned;";
        Console.WriteLine(Log.text);
        string want = "built;leaf;derived(own=2,shared=1);base(shared=1);child1;child2;returned;";
        if (Log.text == want)
        {
            Console.WriteLine("finaliser order PASS");
        }

        Log.text = null;
    }
}
