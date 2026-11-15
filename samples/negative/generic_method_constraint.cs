class Program
{
    static T Identity<T>(T value) where T : struct
    {
        return value;
    }
    static void Main()
    {
        Identity<string>("invalid");
    }
}
