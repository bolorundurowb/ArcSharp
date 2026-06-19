interface IShape {
    int Area();
}
class Rect : IShape {
    public int w;
    public int h;
    public Rect(int a, int b) { w = a; h = b; }
    public int Area() { return w * h; }
}
class Circle : IShape {
    public int r;
    public Circle(int radius) { r = radius; }
    public int Area() { return 3 * r * r; }
}
class Program {
    static int Measure(IShape s) { return s.Area(); }   // interface dispatch
    static void Main() {
        IShape a = new Rect(3, 4);
        IShape b = new Circle(5);
        Console.WriteLine("rect=" + Measure(a));
        Console.WriteLine("circle=" + Measure(b));
    }
}
