// A strong cycle that runs through a struct. A class holds a struct inline,
// and the struct holds a class reference back to the owner. ARC cannot break
// this, so the two owners leak: live=2 is deliberate, like cycle_strong.cs.
struct Link {
    public Owner owner;
}
class Owner {
    public Link link;
}
class Program {
    static void Main() {
        Owner a = new Owner();
        Owner b = new Owner();
        a.link.owner = b;
        b.link.owner = a;
        Console.WriteLine("done (struct cycle)");
    }
}
