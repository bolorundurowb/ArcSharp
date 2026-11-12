class Program
{
    static void Main()
    {
        var match = '\u0041' == 'A';
        Console.WriteLine("\u0041");
        Console.WriteLine("letter=\u0041 match=" + match + " bytes=" + "\U0001F600".Length);
    }
}
