record Table(int[] Cells);

static class Program
{
    static void Main()
    {
        Table table = new Table(new int[2]);
        Console.WriteLine(table.ToString());
    }
}
