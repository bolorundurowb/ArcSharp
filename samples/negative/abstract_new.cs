// Expected: ARC2012 cannot instantiate an abstract class.
abstract class Shape
{
}

class Program
{
    static void Main()
    {
        Shape s = new Shape();
    }
}
