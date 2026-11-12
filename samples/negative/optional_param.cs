// Expected: ARC3001 optional parameters are not supported.
class Program {
    static int Add(int a, int b = 2) {
        return a + b;
    }
    static void Main() {
        Console.WriteLine(Add(1));
    }
}
