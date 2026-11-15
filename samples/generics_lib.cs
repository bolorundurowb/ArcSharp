interface IValue<T>
{
    T Get();
}

class Box<T> : IValue<T>
{
    public T Value { get; set; }
    public static int Count;

    public Box(T value)
    {
        this.Value = value;
        Count = Count + 1;
    }

    public virtual T Get()
    {
        return this.Value;
    }

    public U Echo<U>(U value)
    {
        return value;
    }
}

class Derived<T> : Box<T>
{
    public T Stored;
    public Derived(T value) : base(value)
    {
        Stored = value;
    }

    public override T Get()
    {
        return Stored;
    }
}

class Tag
{
    public string Text;
}

class Link<T>
{
    public T Value;
    public Link<T> Next;
}

class Factory<T> where T : class, new()
{
    public T Create()
    {
        return new T();
    }
}
