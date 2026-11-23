using System;

class Payload
{
    public int Number = 8;
}

struct Item
{
    public Payload Value;
}

static class Program
{
    static int Main()
    {
        int? missing = null;
        int? value = 5;
        long? wider = value;
        int? combined = value + 2;
        int? absent = value + missing;
        bool? unsure = null;
        bool? definitelyFalse = unsure & false;
        bool? definitelyTrue = unsure | true;
        var item = new Item();
        item.Value = new Payload();
        Item? nullableItem = item;
        item.Value = null;
        bool caught = false;
        try
        {
            int ignored = missing.Value;
        }
        catch (InvalidOperationException)
        {
            caught = true;
        }

        if (missing is null && value is int known && known == 5
            && combined == 7 && absent == null && !(absent < 10)
            && (wider ?? 0L) == 5L && (missing ?? 9) == 9
            && definitelyFalse == false && definitelyTrue == true
            && nullableItem.HasValue && nullableItem.Value.Value.Number == 8
            && missing.GetValueOrDefault() == 0 && missing.GetValueOrDefault(4) == 4
            && caught)
        {
            Console.WriteLine("nullable values PASS");
            return 0;
        }

        return 1;
    }
}
