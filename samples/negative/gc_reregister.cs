// GC.ReRegisterForFinalize would let an object be finalised twice, which means
// resurrecting it. Resurrection is not supported, so the call is rejected.
class Resource
{
    ~Resource()
    {
    }
}

class Program
{
    static void Main()
    {
        Resource resource = new Resource();
        GC.ReRegisterForFinalize(resource);
    }
}
