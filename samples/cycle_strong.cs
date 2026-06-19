class Parent {
    public Child child;
}
class Child {
    public Parent owner;   // STRONG back-reference -> forms a cycle
    public int id;
}
class Program {
    static void Link() {
        Parent p = new Parent();
        Child c = new Child();
        c.id = 7;
        p.child = c;
        c.owner = p;          // cycle: p -> c -> p
    }
    static void Main() {
        Link();               // both objects become unreachable here
        Console.WriteLine("done (strong cycle)");
    }
}
