class Publisher
{
    public event Action Changed
    {
        add { }
        remove { }
    }
}

class Program
{
    static void Main()
    {
        var publisher = new Publisher();
        publisher.Changed += () => { };
    }
}
