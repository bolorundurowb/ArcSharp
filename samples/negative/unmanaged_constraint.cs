struct References
{
    public string Item;
}

static class Program
{
    static T RequireUnmanaged<T>(T value) where T : unmanaged => value;

    static void Main()
    {
        RequireUnmanaged(new References());
    }
}
