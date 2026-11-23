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
        if (values is [4])
        {
        }
    }
}
