class Person
{
    public string Name { get; set; }
    public int Age { get; set; }
}

class Program
{
    static void Main()
    {
        Person p = new Person();
        p.Name = "Alice";
        p.Age = 30;
        Console.WriteLine(p.Name);
        Console.WriteLine(p.Age);
    }
}
