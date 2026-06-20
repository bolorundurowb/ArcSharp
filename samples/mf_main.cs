// Entry point for the multi-file sample. References Greeter, which is declared
// in mf_lib.cs. Compile with:  arcsharp samples/mf_main.cs samples/mf_lib.cs
class Program
{
    static void Main()
    {
        Greeter g = new Greeter();
        Console.WriteLine("twice=" + g.Twice(21));
    }
}
