static class Program
{
    static void Main()
    {
        int count;
        do
        {
            count = 1;
        }
        while (count == 0);

        do
        {
            count += 2;
        }
        while (count < 5);

        if (count == 5)
        {
            Console.WriteLine("do while PASS");
        }
    }
}
