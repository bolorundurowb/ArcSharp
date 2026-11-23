// Expected: CS0029 only an Exception can be thrown. Roslyn reports it, so the
// lowering never sees the statement.
class Plain
{
}

class Program
{
    static void Main()
    {
        throw new Plain();
    }
}
