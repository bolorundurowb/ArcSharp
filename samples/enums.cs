enum Mode : byte
{
    First = 1,
    Second = 2,
    Both = First | Second
}

enum Limit : ulong
{
    High = 9223372036854775808UL,
    Last = 18446744073709551615UL
}

struct State
{
    public Mode Mode;
}

static class Program
{
    static Limit Echo(Limit value = Limit.High)
    {
        return value;
    }

    static void Main()
    {
        State state = new State();
        state.Mode = Mode.First | Mode.Second;
        State copy = state;
        Mode[] values = new Mode[1];
        values[0] = copy.Mode++;
        copy.Mode -= (byte)1;
        int result = 0;
        switch (values[0])
        {
            case Mode.Both:
                result = 1;
                break;
        }

        if (result == 1 && copy.Mode == Mode.Both && (byte)state.Mode == 3
            && (ulong)Echo() == 9223372036854775808UL && Limit.Last > Limit.High)
        {
            Console.WriteLine("enums PASS");
        }
    }
}
