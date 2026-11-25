class Publisher
{
    public event Action Changed = First;
    public static event Action Global;

    public void Raise()
    {
        Changed?.Invoke();
    }

    public void Clear()
    {
        Changed = null;
    }

    public static void RaiseGlobal()
    {
        Global?.Invoke();
    }

    static void First()
    {
        Program.Record(1);
    }
}

class Program
{
    static int trace;
    static int receivers;

    public static void Record(int digit)
    {
        trace = trace * 10 + digit;
    }

    static Publisher Receiver(Publisher publisher)
    {
        receivers++;
        return publisher;
    }

    static Action Handler(Publisher publisher)
    {
        publisher.Clear();
        return Second;
    }

    static void Second()
    {
        Record(2);
    }

    static void Third()
    {
        Record(3);
    }

    static int Main()
    {
        var publisher = new Publisher();
        publisher.Raise();
        Receiver(publisher).Changed += Handler(publisher);
        publisher.Changed += Third;
        publisher.Raise();
        publisher.Changed -= Second;
        publisher.Raise();
        publisher.Clear();
        publisher.Raise();
        Publisher.Global += Second;
        Publisher.RaiseGlobal();
        Publisher.Global -= Second;
        Publisher.RaiseGlobal();

        if (trace != 12332 || receivers != 1)
        {
            return 1;
        }

        Console.WriteLine("events PASS");
        return 0;
    }
}
