class Base
{
    public int Number = 40;

    public Base()
    {
        Number = Number + 1;
    }
}

class Derived : Base
{
    public static int Calls;
    public static int First = Next();
    public static int Second = Next();
    public string Text = "ready";

    static int Next()
    {
        Calls = Calls + 1;
        return Calls;
    }

    public Derived()
    {
    }

    public Derived(int number) : this()
    {
        Number = Number + number;
    }
}

class Implicit : Base
{
    public string Text = "implicit";
}

class Program
{
    static void Main()
    {
        Derived derived = new Derived(1);
        Implicit implicitCtor = new Implicit();
        if (derived.Number == 42 && derived.Text == "ready"
            && implicitCtor.Number == 41 && implicitCtor.Text == "implicit"
            && Derived.First == 1 && Derived.Second == 2 && Derived.Calls == 2)
        {
            Console.WriteLine("initialisers PASS");
        }
    }
}
