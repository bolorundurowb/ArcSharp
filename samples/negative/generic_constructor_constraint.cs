class NoDefault
{
    public NoDefault(int value)
    {
    }
}
class Factory<T> where T : new()
{
}
class Program
{
    static void Main()
    {
        var invalid = new Factory<NoDefault>();
    }
}
