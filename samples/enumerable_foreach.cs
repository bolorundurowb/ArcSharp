using System.Collections;
using System.Collections.Generic;

sealed class Item { }

sealed class Sequence : IEnumerable<Item>
{
    public int Factories;
    public int Disposals;
    public int Reads;

    public IEnumerator<Item> GetEnumerator()
    {
        Factories++;
        return new Cursor(this);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    sealed class Cursor : IEnumerator<Item>
    {
        private Sequence owner;
        private int position;

        public Cursor(Sequence sequence) { owner = sequence; }

        public bool MoveNext() { return ++position <= 2; }

        public Item Current
        {
            get
            {
                owner.Reads++;
                return new Item();
            }
        }

        object IEnumerator.Current => Current;

        public void Dispose() { owner.Disposals++; }

        public void Reset() { throw new NotSupportedException(); }
    }
}

static class Program
{
    static void Main()
    {
        var sequence = new Sequence();
        IEnumerable<Item> enumerable = sequence;
        foreach (var item in enumerable)
        {
            break;
        }

        try
        {
            foreach (var item in enumerable)
            {
                throw new Exception();
            }
        }
        catch (Exception) { }

        foreach (var item in enumerable)
        {
            continue;
        }

        if (sequence.Factories == 3 && sequence.Disposals == 3 && sequence.Reads == 4)
        {
            Console.WriteLine("enumerable foreach PASS");
        }
    }
}
