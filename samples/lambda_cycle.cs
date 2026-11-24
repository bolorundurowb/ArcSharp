delegate int Reader();

class Source
{
    Reader _reader;

    public void Subscribe()
    {
        _reader = () => Read();
    }

    int Read() => 42;
    public int Publish() => _reader();
}

class Program
{
    static void Main()
    {
        var source = new Source();
        source.Subscribe();
        if (source.Publish() == 42)
        {
            Console.WriteLine("lambda capture cycle");
        }
    }
}
