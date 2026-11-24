struct Pair
{
    public int Left;
}

record Holder(Pair Item);

static class Program
{
    static void Main()
    {
        Holder holder = new Holder(new Pair());
    }
}
