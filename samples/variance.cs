#nullable enable

class Animal
{
    public int Number;
}

class Cat : Animal
{
}

interface IProducer<out T>
{
    T Create();
}

interface IConsumer<in T>
{
    int Read(T value);
}

class CatProducer : IProducer<Cat>
{
    public Cat Create()
    {
        var cat = new Cat();
        cat.Number = 4;
        return cat;
    }
}

class AnimalConsumer : IConsumer<Animal>
{
    public int Read(Animal animal)
    {
        return animal.Number;
    }
}

enum Mode
{
    Active = 1
}

struct Number
{
    public int Value;
}

class Program
{
    static T Unmanaged<T>(T value) where T : unmanaged => value;

    static T Present<T>(T value) where T : notnull => value;

    static T Reference<T>(T value) where T : class? => value;

    static T Zero<T>() => default(T);

    static T EnumValue<T>(T value) where T : System.Enum => value;

    static void Main()
    {
        IProducer<Cat> cats = new CatProducer();
        IProducer<Animal> animals = cats;
        IConsumer<Animal> reader = new AnimalConsumer();
        IConsumer<Cat> catReader = reader;
        Animal produced = animals.Create();
        var cat = new Cat();
        cat.Number = 8;

        var number = new Number();
        number.Value = 11;
        Cat? missing = null;

        if (produced.Number == 4 && catReader.Read(cat) == 8
            && Unmanaged(number).Value == 11 && Present(4) == 4 && Reference(missing) == null
            && Zero<double>() == 0.0 && Zero<Number>().Value == 0 && Zero<Cat>() == null
            && EnumValue(Mode.Active) == Mode.Active)
        {
            Console.WriteLine("variance PASS");
        }
    }
}
