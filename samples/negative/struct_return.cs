// Expected: ARC3001 struct return values are not supported.
struct Point {
    public int x;
}
class Program {
    static Point Make() {
        Point p;
        return p;
    }
    static void Main() {
    }
}
