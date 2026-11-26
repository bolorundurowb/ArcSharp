namespace System.Collections;

public interface IEnumerable
{
    IEnumerator GetEnumerator();
}

public interface IEnumerator
{
    object Current { get; }

    bool MoveNext();

    void Reset();
}
