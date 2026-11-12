// Expected: ARC3001 arrays of struct element type are not supported.
struct Point {
    public int x;
}
class Program {
    static void Main() {
        Point[] points = new Point[2];
    }
}
