class Node { public int v; }
class Holder { public weak Node w; }
class Program {
    static Holder Make() {
        Holder h = new Holder();
        Node n = new Node();     // strong owned by local n
        n.v = 99;
        h.w = n;                 // weak: does not keep n alive
        return h;                // n released here -> n dies
    }
    static void Main() {
        Holder h = Make();
        Node got = h.w;          // weak load of a dead target -> null
        if (got == null) Console.WriteLine("weak-after-death is null: PASS");
        else Console.WriteLine("FAIL");
    }
}
