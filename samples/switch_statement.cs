static class Program
{
    static int calls;

    static string Select()
    {
        calls += 1;
        return "beta";
    }

    static void Main()
    {
        int result;
        switch (Select())
        {
            case "alpha":
                result = 1;
                break;
            case null:
            case "beta":
                result = 2;
                break;
            default:
                result = 3;
                break;
        }

        for (int i = 0; i < 3; i += 1)
        {
            switch (i)
            {
                case 0:
                    continue;
                case 1:
                    result += 2;
                    break;
                default:
                    result += 3;
                    break;
            }

            result += 10;
        }

        if (calls == 1 && result == 27)
        {
            Console.WriteLine("switch PASS");
        }
    }
}
