interface IRoot
{
    int Read();
}

interface IChild : IRoot
{
}

class Reader : IChild
{
}

static class Program
{
    static void Main()
    {
    }
}
