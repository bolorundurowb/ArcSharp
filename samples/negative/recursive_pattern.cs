class Tag
{
    public int Count;
}

static class Program
{
    static void Main()
    {
        Tag value = new Tag();
        if (value is { Count: 4 })
        {
        }
    }
}
