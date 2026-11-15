class Expanding<T>
{
    public Expanding<Expanding<T>> Next;
}
class Program
{
    static void Main()
    {
        var invalid = new Expanding<int>();
    }
}
