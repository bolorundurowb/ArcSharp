namespace System.Collections;

// Roslyn binds array foreach through these members. ArcSharp lowers the
// loop to indexed access and does not call an enumerator at run time.
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
