using System;

class Parent {
    public Child child;
}
class Child {
    public WeakReference<Parent> owner;   // idiomatic C#: weak back-reference
    public int id;
}
class Program {
    static void Link() {
        Parent p = new Parent();
        Child c = new Child();
        c.id = 7;
        p.child = c;
        c.owner = new WeakReference<Parent>(p);   // breaks the parent<->child cycle
    }
    static void Main() {
        Link();
        Console.WriteLine("done (WeakReference cycle)");
    }
}
