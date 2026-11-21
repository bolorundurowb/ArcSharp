class Item
{
    public int Read()
    {
        return 7;
    }
}

class Program
{
    static WeakReference<Item> probe;
    static int observed;

    static bool Alive()
    {
        return probe.TryGetTarget(out var target);
    }

    static Item ReturnOwner()
    {
        Item item = new Item();
        return item;
    }

    static void StoreOwner()
    {
        Item item = new Item();
        probe = new WeakReference<Item>(item);
        Item destination = item;
    }

    static void Observe(Item item)
    {
        if (item != null && Alive())
        {
            observed = observed + 1;
        }
    }

    static void PassOwner(bool early)
    {
        Item item = new Item();
        probe = new WeakReference<Item>(item);
        if (early)
        {
            return;
        }

        Observe(item);
    }

    static void InspectAlias(Item value, ref Item slot)
    {
        slot = null;
        if (value != null && Alive())
        {
            observed = observed + 1;
        }
    }

    static void Main()
    {
        Item item = ReturnOwner();
        Item same = item;
        if (same == item && same == item && item.Read() == 7)
        {
            observed = observed + 1;
        }

        StoreOwner();
        bool storeDead = !Alive();
        PassOwner(false);
        bool passedDead = !Alive();
        PassOwner(true);
        bool earlyDead = !Alive();

        Item aliased = new Item();
        probe = new WeakReference<Item>(aliased);
        InspectAlias(aliased, ref aliased);
        bool aliasDead = !Alive();
        probe = null;
        if (observed == 3 && storeDead && passedDead && earlyDead && aliasDead)
        {
            Console.WriteLine("lifetime reads and moves PASS");
        }
    }
}
