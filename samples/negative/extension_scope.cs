namespace Tools
{
    static class Helpers
    {
        public static int Twice(this int value)
        {
            return value * 2;
        }
    }
}

static class Program
{
    static void Main()
    {
        int result = 3.Twice();
    }
}
