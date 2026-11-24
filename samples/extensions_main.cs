static class Program
{
    static void Main()
    {
        Crate crate = new Crate();
        crate.Fill(3);
        crate.Label = "box";

        int total = crate.Total();
        int shifted = crate.Total().Plus(4).Times(2);
        string tag = crate.Label.Shout();
        bool partial = crate.Describe() == "box:3" && crate.Weigh() == 3;
        bool numbers = 6.Times(7) == 42 && total == 3 && shifted == 14;
        bool strings = tag == "box!" && "go".Shout() == "go!";

        Pair pair = new Pair();
        pair.First = 2;
        pair.Second = 5;
        bool structs = pair.Sum() == 7 && pair.Sum().Times(pair.First) == 14;

        if (partial && numbers && strings && structs)
        {
            Console.WriteLine("extensions PASS");
        }
    }
}
