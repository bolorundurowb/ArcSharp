namespace System.Collections.Generic;

public sealed class KeyNotFoundException : SystemException
{
    public KeyNotFoundException() : base("The given key was not present in the dictionary.")
    {
    }

    public KeyNotFoundException(string message) : base(message)
    {
    }
}
