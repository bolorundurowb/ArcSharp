sealed class Item
{
    public int Value;
}

static class Program
{
    static int calls;

    static Item[] MakeItems()
    {
        calls += 1;
        Item[] items = new Item[3];
        items[0] = new Item();
        items[1] = new Item();
        items[0].Value = 7;
        items[1].Value = 3;
        return items;
    }

    static void Main()
    {
        long sum = 0;
        int[] values = new int[2];
        values[0] = 2;
        values[1] = 5;
        foreach (long value in values)
        {
            sum += value;
        }

        foreach (Item item in MakeItems())
        {
            if (item == null)
            {
                break;
            }

            if (item.Value == 3)
            {
                continue;
            }

            sum += item.Value;
        }

        if (sum == 14 && calls == 1)
        {
            Console.WriteLine("array foreach PASS");
        }
    }
}
