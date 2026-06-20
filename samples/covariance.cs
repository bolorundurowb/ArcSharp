class Animal { }
class Dog : Animal { }

class Program
{
    static void Main()
    {
        Animal[] animals = new Dog[2];
        animals[0] = new Dog();
        Console.WriteLine("stored Dog");
    }
}
