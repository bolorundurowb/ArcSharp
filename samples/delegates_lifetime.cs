using System;

delegate void Handler(int value);

delegate int Reader();

class Listener
{
    public int Seen;
    public string Name;

    public Listener(string name)
    {
        Name = name;
    }

    ~Listener()
    {
        Journal.Text = Journal.Text + "finalised " + Name + ";";
    }

    public void OnValue(int value)
    {
        Seen = Seen + value;
    }

    public void Explode(int value)
    {
        throw new InvalidOperationException("handler failed");
    }
}

class Journal
{
    public static string Text = "";
}

class Source
{
    public Handler Changed;
    public int Raised;

    public void Raise(int value)
    {
        Raised = Raised + 1;
        Changed?.Invoke(value);
    }
}

class Program
{
    static Reader MakeReader(Listener listener)
    {
        return listener.Name.Length == 0 ? null : MakeReaderFor(listener);
    }

    static Reader MakeReaderFor(Listener listener)
    {
        return new Probe(listener).Read;
    }

    static int Total(Reader[] readers)
    {
        var total = 0;
        for (var index = 0; index < readers.Length; index++)
        {
            total = total + readers[index]();
        }

        return total;
    }

    static Handler Subscribe(string name)
    {
        var created = new Listener(name);
        return created.OnValue;
    }

    static void Main()
    {
        Run();
        Console.WriteLine("journal=" + Journal.Text);
        Journal.Text = null;
    }

    static void Run()
    {
        // The delegate keeps its target alive. The target is finalised when the
        // delegate goes, because no local still names the target.
        var handler = Subscribe("kept");

        Console.WriteLine("before=" + Journal.Text);
        handler(5);
        handler(6);
        handler = null;
        Console.WriteLine("after=" + Journal.Text);

        // A delegate field subscribes and unsubscribes like an event would.
        var source = new Source();
        var first = new Listener("first");
        var second = new Listener("second");
        source.Changed += first.OnValue;
        source.Changed += second.OnValue;
        source.Raise(10);
        source.Changed -= first.OnValue;
        source.Raise(1);
        Console.WriteLine("first=" + first.Seen + " second=" + second.Seen + " raised=" + source.Raised);
        source.Changed = null;
        source.Raise(100);
        Console.WriteLine("raised=" + source.Raised + " second=" + second.Seen);

        // An exception in a handler leaves the remaining handlers uncalled and the
        // counts balanced.
        var failing = new Listener("failing");
        var after = new Listener("after");
        Handler guarded = first.OnValue;
        guarded += failing.Explode;
        guarded += after.OnValue;
        var message = "none";
        try
        {
            guarded(1000);
        }
        catch (InvalidOperationException error)
        {
            message = error.Message;
        }

        Console.WriteLine("message=" + message + " first=" + first.Seen + " after=" + after.Seen);

        // Delegates in an array, built from a returned delegate.
        var readers = new Reader[2];
        readers[0] = MakeReader(first);
        readers[1] = MakeReader(second);
        Console.WriteLine("total=" + Total(readers));
        Console.WriteLine("delegates lifetime PASS");
    }
}

class Probe
{
    public Listener Target;

    public Probe(Listener target)
    {
        Target = target;
    }

    public int Read()
    {
        return Target.Seen;
    }
}
