class Parent {
    public Child child;
}
class Child {
    public weak Parent owner;   // WEAK back-reference -> breaks the cycle
    public int id;
}
class Program {
    static void Link() {
        Parent p = new Parent();
        Child c = new Child();
        c.id = 7;
        p.child = c;
        c.owner = p;            // no cycle: owner is weak
    }
    static void Main() {
        Link();
        Console.WriteLine("done (weak cycle)");
    }
}
