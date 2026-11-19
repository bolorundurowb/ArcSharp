using System;

class Node { public int v; }
class Holder { public WeakReference<Node> w; }
class Program {
    static Holder Make() {
        Holder h = new Holder();
        Node n = new Node();     // strong owned by local n
        n.v = 99;
        h.w = new WeakReference<Node>(n);   // does not keep n alive
        return h;                // n released here -> n dies
    }
    static void Main() {
        Holder h = Make();
        if (h.w.TryGetTarget(out var got)) Console.WriteLine("FAIL");
        else Console.WriteLine("weak-after-death is null: PASS");
    }
}
