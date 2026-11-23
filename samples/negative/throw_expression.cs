// Expected: ARC3001 throw expressions are not supported. A 'throw' statement is.
class Program
{
    static int Pick(int value)
    {
        return value > 0 ? value : throw new System.ArgumentException("not positive");
    }

    static void Main()
    {
        Console.WriteLine(Pick(1));
    }
}
