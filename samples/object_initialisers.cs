class Engine
{
    public int Power;
    public string Name;
}

class Car
{
    public int Wheels;
    public string Label;
    public Engine Motor = new Engine();
    public Engine Spare;
    public int Writes;

    private int _speed;

    public int Speed
    {
        get { return _speed; }
        set
        {
            _speed = value;
            Writes++;
        }
    }

    public Car()
    {
        Wheels = 4;
    }

    public Car(string label)
    {
        Label = label;
        Wheels = 3;
    }
}

static class Program
{
    static int _order;

    static int Next(int value)
    {
        _order = _order * 10 + value;
        return value;
    }

    static Car Build(int wheels)
    {
        return new Car { Wheels = wheels };
    }

    static void Main()
    {
        Car plain = new Car { Label = "plain", Speed = 90 };
        bool fieldsAndProperties = plain.Label == "plain" && plain.Speed == 90 && plain.Wheels == 4
            && plain.Writes == 1;

        Car named = new Car("named") { Speed = 5, Wheels = 6 };
        bool constructorFirst = named.Label == "named" && named.Wheels == 6 && named.Speed == 5;

        Car nested = new Car
        {
            Motor = { Power = 300, Name = "v8" },
            Spare = new Engine { Power = 10 }
        };
        bool nestedMembers = nested.Motor.Power == 300 && nested.Motor.Name == "v8"
            && nested.Spare.Power == 10 && nested.Spare.Name == null;

        Car ordered = new Car { Wheels = Next(1), Speed = Next(2), Label = "" + Next(3) };
        bool sourceOrder = _order == 123 && ordered.Label == "3";

        Car viaCall = Build(8);
        bool inReturn = viaCall.Wheels == 8;

        int local = 11;
        Car usesLocal = new Car { Wheels = local + 1 };
        bool expression = usesLocal.Wheels == 12;

        if (fieldsAndProperties && constructorFirst && nestedMembers && sourceOrder && inReturn && expression)
        {
            Console.WriteLine("object initialisers PASS");
        }
    }
}
