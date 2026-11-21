class Base
{
    public string Name { get; }
    public int Value { get; set; }

    public Base()
    {
        Name = "base";
    }
}

class Derived : Base
{
    public Derived() : base()
    {
    }
}

class Program
{
    static void Main()
    {
        Derived derived = new Derived();
        int wide = 511;
        byte[] values = new byte[1];
        values[0] = (byte)wide;
        derived.Value = values[0];
        if (derived.Name == "base" && derived.Value == 255 && (sbyte)wide == -1)
        {
            Console.WriteLine("properties and narrowing PASS");
        }
    }
}
