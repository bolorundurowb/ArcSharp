// Expected: ARC3001 exceptions are not supported.
class Program {
    static void Main() {
        try {
            Console.WriteLine("x");
        }
        catch (System.Exception e) {
            Console.WriteLine("y");
        }
    }
}
