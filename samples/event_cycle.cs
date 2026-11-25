class Publisher
{
    public event Action Changed;
    int count;

    public void Subscribe()
    {
        Changed += () => count++;
    }

    public int Raise()
    {
        Changed?.Invoke();
        return count;
    }
}

class Program
{
    static int Main()
    {
        var publisher = new Publisher();
        publisher.Subscribe();
        if (publisher.Raise() != 1)
        {
            return 1;
        }

        Console.WriteLine("event capture cycle");
        return 0;
    }
}
