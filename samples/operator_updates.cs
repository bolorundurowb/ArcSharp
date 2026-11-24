class Box
{
    public int Value;

    public Box(int value) => Value = value;

    public static Box operator ++(Box value) => new Box(value.Value + 1);

    public static Box operator +(Box value, int amount) => new Box(value.Value + amount);
}

class Derived : Box
{
    public Derived(int value) : base(value)
    {
    }
}

struct Reading
{
    public int Value;

    public static Reading operator ++(Reading value)
    {
        value.Value++;
        return value;
    }

    public static Reading operator +(Reading value, int amount)
    {
        value.Value += amount;
        return value;
    }
}

static class Program
{
    static void Main()
    {
        var box = new Box(1);
        var old = box++;
        var addition = box += 3;
        var reading = new Reading();
        var before = reading++;
        var after = reading += 4;
        Box[] array = new Derived[1];
        array[0] = new Derived(8);
        int index = 0;
        bool caught = false;
        try
        {
            array[index++]++;
        }
        catch (ArrayTypeMismatchException)
        {
            caught = true;
        }

        if (old.Value == 1 && box.Value == 5 && addition == box
            && before.Value == 0 && after.Value == 5 && reading.Value == 5
            && caught && index == 1 && array[0].Value == 8)
        {
            Console.WriteLine("operator updates PASS");
        }
    }
}
