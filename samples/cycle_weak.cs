using System;

class Parent {
    public Child child;
}
class Child {
    public WeakReference<Parent> owner;   // weak back-reference: breaks the cycle
    public int id;
}
class Program {
    static void Link() {
        Parent p = new Parent();
        Child c = new Child();
        c.id = 7;
        p.child = c;
        c.owner = new WeakReference<Parent>(p);
    }
    static void Main() {
        Link();
        Console.WriteLine("done (weak cycle)");
    }
}
