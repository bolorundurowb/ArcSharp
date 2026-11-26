namespace System.Collections.Generic;

// The collections compare and hash their elements through these two methods.
// The compiler replaces each call with the comparison that the closed element
// type needs, so the bodies below run only under the .NET runtime that builds
// the reference assembly. They use members the reference surface declares.
internal static class ElementEquality
{
    public static bool AreEqual<T>(T left, T right)
    {
        return left == null ? right == null : left.Equals(right);
    }

    public static int GetHash<T>(T value)
    {
        return value == null ? 0 : value.GetHashCode();
    }
}
