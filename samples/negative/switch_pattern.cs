class Tag
{
    public int Count;
}

static class Program
{
    static void Main()
    {
        Tag value = new Tag();
        switch (value)
        {
            case { Count: 4 }:
                break;
        }
    }
}
