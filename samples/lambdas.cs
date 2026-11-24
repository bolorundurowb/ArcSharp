delegate int Counter(int step);
delegate int Reader();

class Source
{
    int _value = 40;

    public Reader MakeReader() => () => _value + 2;
}

class Program
{
    static Counter MakeCounter(int initial)
    {
        int value = initial;
        Counter add = step => { value += step; return value + initial; };
        Counter subtract = step => { value -= step; return value; };
        value = subtract(2);
        initial = 1;
        return add;
    }

    static void Main()
    {
        Counter add = MakeCounter(42);
        Reader reader = new Source().MakeReader();
        Counter twice = static (int value) => value * 2;
        if (add(1) == 42 && add(2) == 44 && reader() == 42 && twice(3) == 6)
        {
            Counter absent = null;
            try
            {
                absent(0);
            }
            catch (NullReferenceException)
            {
                Console.WriteLine("lambdas PASS");
            }
        }
    }
}
