class Gate
{
    public bool Open;

    public Gate(bool open) => Open = open;

    public static bool operator true(Gate gate) => gate.Open;

    public static bool operator false(Gate gate) => !gate.Open;

    public static Gate operator &(Gate left, Gate right) => right;

    public static Gate operator |(Gate left, Gate right) => left;
}

struct Reading
{
    public int Value;
}

static class Program
{
    static int calls;

    static Gate Create(bool open)
    {
        calls++;
        return new Gate(open);
    }

    static string ReadText() => "captured";

    static void Main()
    {
        var closed = new Gate(false);
        var open = new Gate(true);
        Gate first = closed && Create(true);
        Gate second = open || Create(false);
        Gate third = open && Create(false);
        int key = 1;
        string text = key switch { 0 => "zero", 1 => "one", _ => "other" };
        var reading = new Reading();
        reading.Value = 7;
        Reading copy = key == 1 ? reading : new Reading();
        bool caught = false;

        try
        {
            string unmatched = ReadText() switch { "other" => "unexpected" };
        }
        catch (System.Runtime.CompilerServices.SwitchExpressionException)
        {
            caught = true;
        }

        if (!first.Open && second.Open && !third.Open && calls == 1
            && copy.Value == 7 && text == "one" && caught)
        {
            Console.WriteLine("conditional values PASS");
        }
    }
}
