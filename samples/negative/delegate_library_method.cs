// Expected: ARC3001 a delegate cannot bind to a library method.
delegate void Printer(string text);

class Program
{
    static void Main()
    {
        Printer printer = System.Console.WriteLine;
    }
}
