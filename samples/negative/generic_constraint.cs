class ReferenceOnly<T> where T : class
{
}
class Program
{
    static void Main()
    {
        var invalid = new ReferenceOnly<int>();
    }
}
