// The finaliser runs at the statement that drops the last strong reference. It
// does not run when a local leaves its block, while another reference exists,
// or later at an unspecified collection. A weak reference shows when the
// object is gone, and the log shows when its finaliser ran.
class Log
{
    public static int finalised;
}

class Resource
{
    public int id;

    ~Resource()
    {
        Log.finalised = Log.finalised + 1;
        Console.WriteLine("resource " + id + " finalised");
    }
}

class Holder
{
    public Resource kept;
}

class Program
{
    static bool Alive(WeakReference<Resource> weak)
    {
        return weak.TryGetTarget(out Resource target);
    }

    static void Main()
    {
        Holder holder = new Holder();
        Resource resource = new Resource();
        resource.id = 1;
        holder.kept = resource;
        WeakReference<Resource> weak = new WeakReference<Resource>(resource);

        resource = null;
        bool afterLocal = Alive(weak) && Log.finalised == 0;
        Console.WriteLine("local dropped, holder still has it");

        holder.kept = null;
        bool afterField = !Alive(weak) && Log.finalised == 1;
        Console.WriteLine("field cleared");

        // A temporary that nothing keeps is finalised at the end of its statement.
        new Resource().id = 2;
        bool afterTemporary = Log.finalised == 2;

        if (afterLocal && afterField && afterTemporary)
        {
            Console.WriteLine("finaliser release points PASS");
        }
    }
}
