// Expected: ARC3001 'foreach' is not supported.
class Program {
    static void Main() {
        int[] xs = new int[2];
        foreach (var x in xs) {
            Console.WriteLine(x);
        }
    }
}
