// Expected: ARC3001 lambdas require by-value parameters, so a ref parameter is rejected.
delegate void Writer(ref int value);

class Program
{
    static void Main()
    {
        Writer writer = (ref int value) => { value = 1; };
    }
}
