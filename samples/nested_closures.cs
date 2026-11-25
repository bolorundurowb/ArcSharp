class Counter
{
    public int Value;

    public Func<int, Func<int>> Factory(int initial)
    {
        return step =>
        {
            int calls = 0;
            return () =>
            {
                calls++;
                initial += step;
                Value++;
                return initial + calls + Value;
            };
        };
    }
}

class Program
{
    static Func<int, int> Make(int start)
    {
        int Even(int n) => n == 0 ? start : Odd(n - 1);
        int Odd(int n) => n == 0 ? start + 1 : Even(n - 1);
        int Invoke(int n) => Even(n);
        start++;
        return Invoke;
    }

    static void Main()
    {
        var counter = new Counter();
        var factory = counter.Factory(10);
        var first = factory(2);
        var second = factory(3);
        int nested = first() + second() + first();
        var calculate = Make(19);
        int recursive = calculate(3);

        int count;
        count = 0;
        void Tick() => count++;
        Action a = Tick;
        Action b = Tick;
        Action combined = a + b;
        combined -= Tick;
        combined();

        static void Bump(ref int value) => value++;
        Bump(ref count);

        T Identity<T>(T value)
        {
            T Read() => value;
            return Read();
        }

        Func<int>[] readers = new Func<int>[2];
        int[] items = new int[2];
        items[0] = Identity(7);
        items[1] = 8;
        int index = 0;
        foreach (int item in items)
        {
            readers[index++] = () => item;
        }
        if (nested == 54 && recursive == 21 && a == b && count == 2
            && readers[0]() == 7 && readers[1]() == 8 && Identity("generic") == "generic")
        {
            Console.WriteLine("nested closures ok");
        }
    }
}
