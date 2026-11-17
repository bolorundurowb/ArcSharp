// Interior references stay valid when the callee drops every named owner.
// The fingerprint is the length or value read after that drop.
class Box
{
    public string Name;
    public int[] Items;
}

struct Slot
{
    public string Name;

    public void ReadAfterDrop()
    {
        Program.Held = null;
        Program.Seen = Name.Length;
    }
}

class Wrap
{
    public Slot Slot;
    public Box Child;
}

class Cell
{
    public Cell(ref int slot)
    {
        slot = slot + 1;
    }
}

class Program
{
    public static Box Root;
    public static Wrap Held;
    public static int Seen;
    static int Counter;

    static void ReadName(ref string slot)
    {
        Root = null;
        Seen = slot.Length;
    }

    static void WriteItem(ref int slot)
    {
        Root.Items = null;
        slot = 7;
        Seen = slot;
    }

    static void Forward(ref int slot)
    {
        WriteItem(ref slot);
    }

    static void ReadChild(ref string slot)
    {
        Held = null;
        Seen = slot.Length;
    }

    static int ThroughIn(in int n)
    {
        return n;
    }

    static void Bump(ref int slot)
    {
        slot = slot + 1;
    }

    static void Main()
    {
        Root = new Box();
        Root.Name = "kept";
        ReadName(ref Root.Name);
        int name = Seen;

        Root = new Box();
        Root.Items = new int[1];
        Forward(ref Root.Items[0]);
        int item = Seen;
        Root = null;

        Held = new Wrap();
        Held.Child = new Box();
        Held.Child.Name = "child";
        ReadChild(ref Held.Child.Name);
        int child = Seen;

        Held = new Wrap();
        Held.Slot.Name = "struct";
        Held.Slot.ReadAfterDrop();
        int slot = Seen;

        Counter = 4;
        Bump(ref Counter);
        int counter = ThroughIn(in Counter);

        int local = 1;
        new Cell(ref local);

        Console.WriteLine("owners=" + name + "," + item + "," + child + "," + slot + "," + counter + "," + local);
    }
}
