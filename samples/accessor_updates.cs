interface IScore
{
    int Points { get; set; }
}

class Money
{
    public int Cents;

    public static Money Of(int cents)
    {
        Money money = new Money();
        money.Cents = cents;
        return money;
    }

    public static Money operator +(Money left, Money right)
    {
        Money sum = new Money();
        sum.Cents = left.Cents + right.Cents;
        return sum;
    }

    public static Money operator ++(Money value)
    {
        Money next = new Money();
        next.Cents = value.Cents + 1;
        return next;
    }
}

class Ledger : IScore
{
    private int _count;
    private int _points;
    private int[] _slots = new int[3];
    private string _label = "a";
    private Money _balance = new Money();

    public int Reads;
    public int Writes;

    public int Count
    {
        get { Reads++; return _count; }
        set { Writes++; _count = value; }
    }

    public int this[int index]
    {
        get { return _slots[index]; }
        set { _slots[index] = value; }
    }

    public string Label
    {
        get { return _label; }
        set { _label = value; }
    }

    public Money Balance
    {
        get { return _balance; }
        set { _balance = value; }
    }

    public int Points
    {
        get { return _points; }
        set { _points = value; }
    }

    public virtual int Level { get; set; }

    public static int Shared
    {
        get { return _shared; }
        set { _shared = value; }
    }

    private static int _shared;

    public void Bump()
    {
        Count += 2;
        Count++;
    }
}

static class Program
{
    static int calls;

    static Ledger Pick(Ledger ledger)
    {
        calls++;
        return ledger;
    }

    static int Next()
    {
        calls++;
        return 1;
    }

    static void Main()
    {
        Ledger ledger = new Ledger();

        ledger.Count += 5;
        int before = ledger.Count++;
        int after = ++ledger.Count;
        int old = ledger.Count--;
        bool counts = before == 5 && after == 7 && old == 7 && ledger.Count == 6;

        ledger.Bump();
        bool member = ledger.Count == 9;

        // One getter and one setter call per update, whatever the form.
        int reads = ledger.Reads;
        int writes = ledger.Writes;
        ledger.Count *= 2;
        bool once = ledger.Reads - reads == 1 && ledger.Writes - writes == 1 && ledger.Count == 18;

        ledger[1] += 4;
        ledger[1]++;
        int slot = ledger[Next()]--;
        bool indexer = ledger[1] == 4 && slot == 5 && calls == 1;

        calls = 0;
        Pick(ledger)[2] += 3;
        Pick(ledger).Count -= 1;
        bool single = calls == 2 && ledger[2] == 3 && ledger.Count == 17;

        ledger.Label += "b";
        ledger.Label += 1;
        bool text = ledger.Label == "ab1";

        ledger.Balance += Money.Of(5);
        ledger.Balance++;
        bool money = ledger.Balance.Cents == 6;

        ledger.Level += 3;
        ledger.Level++;
        bool virtualMember = ledger.Level == 4;

        Ledger.Shared += 7;
        Ledger.Shared--;
        bool shared = Ledger.Shared == 6;

        IScore score = ledger;
        score.Points += 8;
        score.Points++;
        bool dispatch = score.Points == 9 && ledger.Points == 9;

        if (counts && member && once && indexer && single && text && money && virtualMember && shared && dispatch)
        {
            Console.WriteLine("accessor updates PASS");
        }
    }
}
