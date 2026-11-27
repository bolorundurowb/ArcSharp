namespace System.Collections.Generic;

public interface IEnumerable<out T> : System.Collections.IEnumerable
{
    new IEnumerator<T> GetEnumerator();
}

public interface IEnumerator<out T> : System.Collections.IEnumerator, IDisposable
{
    new T Current { get; }
}
