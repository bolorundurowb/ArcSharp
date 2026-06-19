interface IShape { int Area(); }

class Shape : IShape {
    public int w;
    public int h;
    public Shape(int a, int b) { w = a; h = b; }
    public virtual int Area() { return w * h; }
}

class Square : Shape {
    public Square(int s) : base(s, s) { }
    public override int Area() { return w * w; }
}

class Node {
    public Node next;
    public weak Node owner;
    public int value;
}

class Program {
    static int Total(int[] xs) {
        int s = 0;
        for (int i = 0; i < xs.Length; i = i + 1) { s = s + xs[i]; }
        return s;
    }
    static void Main() {
        Shape sh = new Square(5);
        Console.WriteLine(sh.Area());
        int[] a = new int[3];
        a[0] = 10; a[1] = 20; a[2] = 12;
        Console.WriteLine("sum=" + Total(a));
    }
}
