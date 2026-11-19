// Expected: CS1009 invalid Unicode escape.
class Program
{
    static void Main()
    {
        Console.WriteLine("\u12");
    }
}
