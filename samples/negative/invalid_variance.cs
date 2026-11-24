interface IProducer<out T>
{
    void Receive(T value);
}

static class Program
{
    static void Main()
    {
    }
}
