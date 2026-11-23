using System.Collections;

class Bag : IEnumerable
{
    public int Total;

    public void Add(int value)
    {
        Total += value;
    }

    public IEnumerator GetEnumerator()
    {
        return null;
    }
}

static class Program
{
    static void Main()
    {
        Bag bag = new Bag { 1, 2, 3 };
    }
}
