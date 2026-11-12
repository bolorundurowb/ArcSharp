// Expected: ARC3001 expression-bodied members are not supported.
class Program {
    static int One() => 1;
    static void Main() {
        Console.WriteLine(One());
    }
}
