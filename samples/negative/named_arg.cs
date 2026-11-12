// Expected: ARC3001 named arguments are not supported.
class Program {
    static int Add(int a, int b) {
        return a + b;
    }
    static void Main() {
        Console.WriteLine(Add(a: 1, b: 2));
    }
}
