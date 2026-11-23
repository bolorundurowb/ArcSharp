// The compiler cannot see a store that happens inside another method, so this
// program compiles. The runtime catches it instead: the finaliser returns with
// a strong count above zero, and the process aborts with exit code 70 rather
// than hand out a reference to memory that is about to be freed.
class Registry
{
    public static Zombie last;

    public static void Keep(Zombie value)
    {
        last = value;
    }
}

class Zombie
{
    ~Zombie()
    {
        Registry.Keep(this);
    }
}

class Program
{
    static void Main()
    {
        Zombie zombie = new Zombie();
        zombie = null;
        Console.WriteLine("not reached");
    }
}
