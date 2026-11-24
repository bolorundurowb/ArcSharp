interface IRoot
{
    int Read();

    int Code { get; }
}

interface ILeft : IRoot
{
    int Left();
}

interface IRight : IRoot
{
    int Right();
}

interface ICombined : ILeft, IRight
{
}

class Reader : ICombined
{
    public virtual int Read() => 3;

    int IRoot.Code => 4;

    int ILeft.Left() => 5;

    int IRight.Right() => 6;
}

class Derived : Reader
{
    public override int Read() => 7;
}

static class Program
{
    static void Main()
    {
        ICombined combined = new Derived();
        IRoot root = combined;
        if (combined.Left() == 5 && combined.Right() == 6
            && combined.Read() == 7 && root.Read() == 7 && root.Code == 4)
        {
            Console.WriteLine("base interfaces PASS");
        }
    }
}
