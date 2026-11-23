using System;
class Animal { public int Legs = 4; }
class Dog : Animal { }
class Box<T> { public T Value; }
static class G
{
    public static T Pick<T>(T a, T b) { return b; }
    public static T First<T>(T[] items, T fallback) { return items.Length > 0 ? items[0] : fallback; }
    public static T Open<T>(Box<Box<T>> b) { return b.Value.Value; }
    public static void Out<T>(out T slot, T value) { slot = value; }
    public static string Name(int v) { return "int"; }
    public static string Name<T>(T v) { return "generic"; }
    public static T Chain<T, U>(T t, U u) where U : T { return u; }
}
class Program
{
    static void Main()
    {
        Console.WriteLine("widened=" + G.Pick(1, 2.5));
        Console.WriteLine("base=" + G.Pick(new Dog(), new Animal()).Legs);
        Animal[] zoo = new Animal[1]; zoo[0] = new Animal();
        Console.WriteLine("array=" + G.First(zoo, new Dog()).Legs);
        Box<Box<int>> bb = new Box<Box<int>>(); bb.Value = new Box<int>(); bb.Value.Value = 8;
        Console.WriteLine("nested=" + G.Open(bb));
        int slot; G.Out(out slot, 3); Console.WriteLine("out=" + slot);
        Console.WriteLine("overload=" + G.Name(1) + "," + G.Name("s") + "," + G.Name(1L));
        Console.WriteLine("chain=" + G.Chain(new Animal(), new Dog()).Legs);
        long wide = 5; Console.WriteLine("long=" + G.Pick(1, wide));
        Console.WriteLine("generic inference PASS");
    }
}
