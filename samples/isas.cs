class Animal { public virtual void Speak() { } }
class Dog : Animal { public void Bark() { } }
class Cat : Animal { public void Meow() { } }

class Program
{
    static void Main()
    {
        Animal a = new Dog();
        if (a is Dog)
        {
            Console.WriteLine("is Dog: PASS");
        }

        if (a is Cat)
        {
            Console.WriteLine("is Cat: FAIL");
        }
        else
        {
            Console.WriteLine("is Cat: false");
        }

        Dog? d = a as Dog;
        if (d != null)
        {
            Console.WriteLine("as Dog: PASS");
        }

        Cat? c = a as Cat;
        if (c == null)
        {
            Console.WriteLine("as Cat: null");
        }
    }
}
