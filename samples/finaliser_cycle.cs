// A strong cycle never reaches a zero count, so its finalisers never run. This
// is the documented cost of a cycle, and ARC3100 warns about it. It is the
// reason a back-edge should be a WeakReference<T>.
class Log
{
    public static int finalised;
}

class Parent
{
    public Child child;

    ~Parent()
    {
        Log.finalised = Log.finalised + 1;
    }
}

class Child
{
    public Parent owner;

    ~Child()
    {
        Log.finalised = Log.finalised + 1;
    }
}

class Program
{
    static void Link()
    {
        Parent parent = new Parent();
        Child child = new Child();
        parent.child = child;
        child.owner = parent;
    }

    static void Main()
    {
        Link();
        Console.WriteLine("cycle finalised " + Log.finalised);
    }
}
