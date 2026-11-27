using System.Collections;
using System.Collections.Generic;

namespace System.Runtime.CompilerServices;

// The base class of every iterator method. The compiler derives one class from
// it per iterator and overrides Step and Clone. Step runs the body up to its next
// yield. State is 0 before the first Step, a positive number while suspended
// at a yield, and -1 while running or finished, so an exception that leaves
// Step also ends the iteration. The fields are public because the generated
// class writes them directly.
public abstract class IteratorBase<T> : IEnumerable<T>, IEnumerator<T>
{
    public int State;
    public T Value;
    public bool Claimed;
    public bool Disposing;

    public T Current => Value;

    object IEnumerator.Current
    {
        get
        {
            throw new NotSupportedException("Non-generic enumeration of an iterator is not supported.");
        }
    }

    // The first call hands out the iterator itself. A later call needs a fresh
    // iterator, because this one may be part way through its body.
    public IEnumerator<T> GetEnumerator()
    {
        if (!Claimed && State == 0)
        {
            Claimed = true;
            return this;
        }

        IteratorBase<T> copy = Clone();
        copy.Claimed = true;
        return copy;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public bool MoveNext()
    {
        return Step();
    }

    public void Reset()
    {
        throw new NotSupportedException("An iterator cannot be reset.");
    }

    // Resuming a suspended body with Disposing set makes it return from the
    // yield, which runs the finally blocks and using cleanups it is inside.
    public void Dispose()
    {
        if (State > 0)
        {
            Disposing = true;
            Step();
        }

        State = -1;
    }

    public abstract bool Step();

    public abstract IteratorBase<T> Clone();
}
