// Expected: ARC3001 exception filters are not supported.
class Program
{
    static void Main()
    {
        try
        {
            throw new System.InvalidOperationException("x");
        }
        catch (System.Exception e) when (e.Message == "x")
        {
            Console.WriteLine("filtered");
        }
    }
}
