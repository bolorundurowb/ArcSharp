sealed class Resource : IDisposable
{
    public static int Disposals;

    public void Dispose()
    {
        Disposals += 1;
    }
}

static class Program
{
    static void Main()
    {
        int steps = 0;
        int cleanups = 0;
        int result;
        for (int i = 0; i < 5; i += 1)
        {
            try
            {
                using (new Resource())
                {
                    while (true)
                    {
                        steps += 1;
                        break;
                    }

                    if (i == 0)
                    {
                        continue;
                    }

                    break;
                }
            }
            finally
            {
                cleanups += 1;
            }
        }

        do
        {
            try
            {
                continue;
            }
            finally
            {
                result = 7;
            }
        }
        while (result == 0);

        if (result == 7 && steps == 2 && cleanups == 2 && Resource.Disposals == 2)
        {
            Console.WriteLine("loop branches PASS");
        }
    }
}
