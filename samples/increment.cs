static class Program
{
    static void Main()
    {
        int[] values = new int[2];
        values[0] = 9;
        int index = 0;
        int before = values[index++]++;
        int after = --values[0];
        byte small = 255;
        ++small;
        double large = 3.0;
        double original = large--;

        if (before == 9 && after == 9 && index == 1 && values[1] == 0
            && small == 0 && original == 3.0 && large == 2.0)
        {
            Console.WriteLine("increment PASS");
        }
    }
}
