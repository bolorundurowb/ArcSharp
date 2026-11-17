class Program
{
    static ref int Get(ref int x)
    {
        return ref x;
    }

    static void Main()
    {
        int n = 1;
        ref int a = ref n;
        Get(ref n);
    }
}
