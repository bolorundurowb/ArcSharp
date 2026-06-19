using System;

class Node { public int v; }
class Holder { public WeakReference<Node> w; }
class Program {
    static Holder Make() {
        Holder h = new Holder();
        Node n = new Node();
        n.v = 99;
        h.w = new WeakReference<Node>(n);   // weak; does not keep n alive
        return h;                            // under ARC, n is destroyed here
    }
    static void Main() {
        Holder h = Make();
        if (h.w.TryGetTarget(out var got))
            Console.WriteLine("target v=" + got.v);
        else
            Console.WriteLine("TryGetTarget after death: false (PASS under ARC)");
    }
}
