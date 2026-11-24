using System;

delegate int Operation(int left, int right);

delegate void Notify(string message);

class Calculator
{
    public int Offset;

    public Calculator(int offset)
    {
        Offset = offset;
    }

    public int Add(int left, int right)
    {
        return left + right + Offset;
    }

    public static int Multiply(int left, int right)
    {
        return left * right;
    }
}

class Program
{
    static int Apply(Operation operation, int left, int right)
    {
        return operation(left, right);
    }

    static void Main()
    {
        var calculator = new Calculator(100);
        Operation add = calculator.Add;
        Operation multiply = Calculator.Multiply;
        Operation explicitCreation = new Operation(calculator.Add);

        Console.WriteLine("instance=" + add(1, 2));
        Console.WriteLine("static=" + multiply(3, 4));
        Console.WriteLine("explicit=" + explicitCreation.Invoke(5, 6));
        Console.WriteLine("argument=" + Apply(multiply, 6, 7));
        Console.WriteLine("delegates basic PASS");
    }
}
