// Roslyn cannot bind a record without these declarations. ArcSharp never
// lowers them: it writes the equality, hashing and formatting members of a
// record itself, and rejects any direct use of the types below.
namespace System
{
    public interface IEquatable<T>
    {
        bool Equals(T other);
    }

    public abstract class Type
    {
    }
}

namespace System.Runtime.CompilerServices
{
    // An `init` accessor carries this type as a required modifier.
    public static class IsExternalInit
    {
    }
}

namespace System.Text
{
    public sealed class StringBuilder
    {
        public StringBuilder Append(string value)
        {
            return this;
        }
    }
}
