// Expected: ARC3001 for an anonymous method.
delegate int Handler(int value);
class Program
{
    static void Main()
    {
        Handler handler = delegate(int value) { return value + 1; };
        Console.WriteLine(handler(1));
    }
}
