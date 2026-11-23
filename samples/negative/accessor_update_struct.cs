struct Gauge
{
    private int _level;

    public int Level
    {
        get { return _level; }
        set { _level = value; }
    }
}

static class Program
{
    static void Main()
    {
        Gauge gauge = new Gauge();
        gauge.Level++;
    }
}
