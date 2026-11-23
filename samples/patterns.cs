class Tag
{
    public int Count;
}

static class Program
{
    static int evaluations;

    static int Read()
    {
        evaluations++;
        return 3;
    }

    static void Main()
    {
        var tag = new Tag();
        tag.Count = 4;
        object reference = tag;
        string text = null;
        string word = "word";

        int selected = 0;
        switch (reference)
        {
            case Tag tooLarge when tooLarge.Count > 9:
                selected = -1;
                break;
            case Tag match when match.Count == 4:
                selected = match.Count;
                break;
        }

        if (Read() is > 2 and < 6 and not 4
            && reference is Tag matched && matched.Count == 4
            && text is var captured && captured is null
            && word is "word" or "other"
            && selected == 4 && evaluations == 1)
        {
            Console.WriteLine("patterns PASS");
        }
    }
}
