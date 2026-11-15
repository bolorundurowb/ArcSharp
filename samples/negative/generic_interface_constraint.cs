interface IValue<T>
{
    T Get();
}
class Tag
{
}
class Program
{
    static void Require<T>() where T : IValue<int>
    {
    }
    static void Main()
    {
        Require<Tag>();
    }
}
