abstract class Measure
{
    public abstract int Read();
}

class ConcreteMeasure : Measure
{
    public override int Read()
    {
        return 42;
    }
}

class Program
{
    static string Make()
    {
        return "arc";
    }

    static void Main()
    {
        Measure measure = new ConcreteMeasure();
        string text = Make();
        object boxed = text;
        if ((measure.Read() == 42 && (text == Make() && text != null))
            && (text == "arc" || Make() == "never") && boxed != Make())
        {
            Console.WriteLine("dispatch and logic PASS");
        }
    }
}
