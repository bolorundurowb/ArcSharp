class Payload
{
    public int Number;

    public Payload(int number)
    {
        Number = number;
    }
}

struct Entry
{
    public int Number;
    public Payload Item;
    public int Tail;
}

class Holder
{
    private Entry value;

    public Entry Value
    {
        get => value;
        set
        {
            this.value = value;
            value.Number = -1;
        }
    }
}

static class Program
{
    static Entry root;

    static void Replace(ref Entry entry)
    {
        entry.Number += 1;
    }

    static void Main()
    {
        Entry[] entries = new Entry[2];
        entries[0].Number = 2;
        entries[0].Item = new Payload(5);
        entries[0].Tail = 3;
        entries[1] = entries[0];
        Replace(ref entries[1]);
        entries[1].Item = new Payload(7);
        entries[1].Tail = 4;
        root = entries[1];

        var holder = new Holder();
        Entry assigned = (holder.Value = root);
        root.Item = null;
        int sum = 0;
        foreach (Entry entry in entries)
        {
            sum += entry.Number + entry.Item.Number + entry.Tail;
        }

        if (sum == 24 && assigned.Number == 3 && assigned.Item.Number == 7
            && holder.Value.Item.Number == 7 && root.Item == null && root.Tail == 4)
        {
            Console.WriteLine("struct storage PASS");
        }
    }
}
