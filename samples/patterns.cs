class Tag
{
    public int Count;

    public int Limit => Count * 2;

    public void Deconstruct(out int count, out int limit)
    {
        count = Count;
        limit = Limit;
    }
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
        int[] values = new int[4];
        values[0] = 1;
        values[1] = 2;
        values[2] = 3;
        values[3] = 4;

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
            && reference is Tag(4, > 7) { Limit: 8 } shape && shape.Count == 4
            && values is [1, .. var middle, 4] && middle.Length == 2 && middle[1] == 3
            && selected == 4 && evaluations == 1)
        {
            Console.WriteLine("patterns PASS");
        }
    }
}
