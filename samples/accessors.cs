interface IStore
{
    int this[int index] { get; set; }
}

abstract class Snapshot
{
    public abstract long Latest { get; }
}

class Store : Snapshot, IStore
{
    private int[] entries = new int[3];
    private int count;
    private string note;

    public int Initialised { get; } = 6;

    public override long Latest => count;

    public string Note
    {
        get => note;
        set
        {
            note = value;
            count++;
        }
    }

    public int this[int index]
    {
        get
        {
            return entries[index];
        }
        set => entries[index] = value;
    }
}

static class Program
{
    static void Main()
    {
        var store = new Store();
        IStore indexed = store;
        Snapshot snapshot = store;
        int assigned = (indexed[2] = 9);
        string saved = (store.Note = "note");

        if (assigned == 9 && indexed[2] == 9 && store.Initialised == 6
            && saved == store.Note && snapshot.Latest == 1)
        {
            Console.WriteLine("accessors PASS");
        }
    }
}
