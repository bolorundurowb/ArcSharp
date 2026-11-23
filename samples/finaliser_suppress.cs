// GC.SuppressFinalize skips the finaliser of one object and nothing else: the
// object is still destroyed and its fields are still released. GC.Collect and
// GC.WaitForPendingFinalizers do nothing, because every finaliser has already
// run by the time the statement that dropped the last reference has finished.
class Log
{
    public static int finalised;
    public static int disposed;
}

class Payload
{
    public int value;
}

class File : IDisposable
{
    public Payload payload;

    public File()
    {
        payload = new Payload();
    }

    public void Dispose()
    {
        Log.disposed = Log.disposed + 1;
        GC.SuppressFinalize(this);
    }

    ~File()
    {
        Log.finalised = Log.finalised + 1;
    }
}

class Program
{
    static void Main()
    {
        using (File closed = new File())
        {
            closed.payload.value = 1;
        }

        File forgotten = new File();
        forgotten.payload.value = 2;
        WeakReference<Payload> weak = new WeakReference<Payload>(forgotten.payload);
        forgotten = null;

        GC.Collect();
        GC.WaitForPendingFinalizers();

        Payload ignored;
        bool payloadFreed = !weak.TryGetTarget(out ignored);
        if (Log.disposed == 1 && Log.finalised == 1 && payloadFreed)
        {
            Console.WriteLine("suppress finalizer PASS finalised=" + Log.finalised);
        }
    }
}
