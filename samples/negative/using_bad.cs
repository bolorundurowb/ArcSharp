// Expected: ARC2011 the 'using' resource has no accessible parameterless Dispose.
class Program {
    static void Main() {
        using (var x = new Program()) {
            Console.WriteLine("x");
        }
    }
}
