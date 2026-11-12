// Expected: ARC3001 lambdas ('=>') are not supported.
class Program {
    static void Main() {
        int x = 1;
        System.Func<int, int> g = x => x + 1;
    }
}
