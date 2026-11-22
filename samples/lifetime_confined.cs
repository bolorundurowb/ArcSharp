class ConfinedBox
{
    public int Value;
}

class PublishingBox
{
    public static PublishingBox Saved;

    public PublishingBox()
    {
        Saved = this;
    }
}

class Program
{
    static ConfinedBox Saved;

    static int ReadConfined()
    {
        ConfinedBox box = new ConfinedBox();
        int[] numbers = new int[2];
        if (box is ConfinedBox)
        {
            return box.Value + numbers[1] + numbers.Length;
        }

        return -1;
    }

    static void Escape()
    {
        ConfinedBox box = new ConfinedBox();
        Console.WriteLine(box.Value);
        Saved = box;

        PublishingBox published = new PublishingBox();
        if (published is PublishingBox)
        {
            Console.WriteLine("published");
        }
    }

    static void Main()
    {
        int value = ReadConfined();
        Escape();
        if (value == 2 && Saved is ConfinedBox && PublishingBox.Saved is PublishingBox)
        {
            Console.WriteLine("confined lifetimes PASS");
        }

        Saved = null;
        PublishingBox.Saved = null;
    }
}
