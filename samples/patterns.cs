interface IMarker
{
}

interface ITag : IMarker
{
    int Limit { get; }
}

class Tag : ITag
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

        object other = word;
        object missing = null;
        decimal amount = -1.20m;
        decimal? optional = amount;
        decimal? absent = null;

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
            && reference is ITag tagged && tagged.Limit == 8
            && reference is IMarker
            && other is not IMarker && missing is not IMarker
            && amount is -1.2m and >= -2m and < 0m
            && optional is -1.20m and <= -1m && absent is not < 0m
            && selected == 4 && evaluations == 1)
        {
            Console.WriteLine("patterns PASS");
        }
    }
}
