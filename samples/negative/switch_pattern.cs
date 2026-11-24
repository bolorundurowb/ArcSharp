class Sequence
{
    public int Length => 1;

    public int this[int index] => index;
}

static class Program
{
    static void Main()
    {
        var values = new Sequence();
        switch (values)
        {
            case [4]:
                break;
        }
    }
}
