class Program
{
    static void Main()
    {
        float a = 1.5f;
        float b = 2.0f;
        float c = a + b;
        Console.WriteLine(c);

        double d = 3.5;
        double e = c * 2.0;
        Console.WriteLine(e);

        float pi = 3.14159f;
        float r = 2.5f;
        float area = pi * r * r;
        Console.WriteLine(area);

        Console.WriteLine("area=" + area + " pi=" + pi);

        bool gt = area > 10.0f;
        Console.WriteLine(gt);
    }
}
