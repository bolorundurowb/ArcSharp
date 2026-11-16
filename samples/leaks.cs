// Opt-in leak report (ARC_LEAKS=1): one object kept by a static field, one
// unreachable strong cycle, and one unreachable cycle through an array.
class Node {
    public int id;
}

class Box {
    public static Node kept;
}

class Parent {
    public Child child;
}

class Child {
    public Parent owner;
}

class Cell {
    public Cell[] peers;
}

class Program {
    static void Link() {
        Parent p = new Parent();
        Child c = new Child();
        p.child = c;
        c.owner = p;
    }

    static void ArrayCycle() {
        Cell a = new Cell();
        Cell b = new Cell();
        Cell[] peers = new Cell[2];
        peers[0] = a;
        peers[1] = b;
        a.peers = peers;
        b.peers = peers;
    }

    static void Main() {
        Node n = new Node();
        n.id = 1;
        Box.kept = n;
        Link();
        ArrayCycle();
        Console.WriteLine("leaks reported");
    }
}
