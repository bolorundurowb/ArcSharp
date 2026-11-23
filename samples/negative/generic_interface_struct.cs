interface IMapper
{
    T Pick<T>(T first, T second);
}

struct Mapper : IMapper
{
    public T Pick<T>(T first, T second)
    {
        return second;
    }
}

static class Program
{
    static void Main()
    {
        IMapper mapper = new Mapper();
        int picked = mapper.Pick(1, 2);
    }
}
