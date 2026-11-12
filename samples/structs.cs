// Struct code generation: value semantics, reference fields, methods, and
// structs nested in structs and classes.
struct Point {
    public int x;
    public int y;
    public Point(int a, int b) { x = a; y = b; }
    public int Sum() { return x + y; }
    public void Shift() { x = x + 1; }
}
struct Named {
    public string name;
    public int score;
}
struct Inner {
    public Point spot;
    public int tag;
}
struct Card {
    public string title;   // a reference field inside a nested struct
    public Inner where;
}
class Holder {
    public Named item;     // a struct field inside a class object
    public Card card;
}
class Program {
    static void ShiftX(ref Point p) { p.x = p.x + 1; }
    static void Read(out Point p) { p = new Point(7, 8); }
    static void Main() {
        // a struct local: constructor and instance method
        Point a = new Point(2, 3);
        Console.WriteLine("a.sum=" + a.Sum());

        // value semantics: b is a copy, so writing it leaves a alone
        Point b = a;
        b.x = 10;
        Console.WriteLine("a.x=" + a.x + " b.x=" + b.x);

        // a struct with a reference field: each copy owns its own reference
        Named n;
        n.name = "kit";
        n.score = 9;
        Named m = n;
        n.name = "zed";
        Console.WriteLine(m.name + " " + n.name + " " + m.score);

        // 'ref' aliases the caller's storage, 'out' fills it
        ShiftX(ref a);
        Console.WriteLine("a.x=" + a.x);
        Point o;
        Read(out o);
        Console.WriteLine("out=" + o.Sum());

        // a struct field inside a class, copied out and back
        Holder h = new Holder();
        h.item.name = "held";
        h.item.score = 5;
        Named copy = h.item;
        copy.score = 6;
        Console.WriteLine("held=" + h.item.name + " score=" + h.item.score + " copy=" + copy.score);

        // nested structs: the inner value keeps its own size and fields
        Inner outer;
        outer.spot = new Point(4, 5);
        outer.tag = 1;
        Point inner = outer.spot;
        inner.x = 40;
        Console.WriteLine("outer.x=" + outer.spot.x + " tag=" + outer.tag + " inner.x=" + inner.x);

        // a nested struct that itself holds a reference field
        Card c;
        c.title = "card";
        c.where = outer;
        Card d = c;
        d.title = "copy";
        d.where.tag = 2;
        Console.WriteLine("card=" + c.title + " " + d.title + " tags=" + c.where.tag + "," + d.where.tag);

        // constructing a temporary and calling a method on it
        int temp = new Point(20, 22).Sum();
        Console.WriteLine("temp=" + temp);
    }
}
