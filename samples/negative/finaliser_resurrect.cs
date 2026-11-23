// A finaliser must not store 'this' anywhere that outlives it. Each class below
// does so in a way the compiler can see, and each store is rejected (ARC2016).
class Registry
{
    public static object last;
    public object slot;
    public object[] all = new object[1];
}

class Direct
{
    ~Direct()
    {
        Registry.last = this;
    }
}

class ThroughLocal
{
    ~ThroughLocal()
    {
        object self = this;
        object alias = self;
        Registry.last = alias;
    }
}

class IntoField
{
    public Registry registry;

    ~IntoField()
    {
        registry.slot = this;
    }
}

class IntoArray
{
    public Registry registry;

    ~IntoArray()
    {
        registry.all[0] = this;
    }
}

class Program
{
    static void Main()
    {
    }
}
