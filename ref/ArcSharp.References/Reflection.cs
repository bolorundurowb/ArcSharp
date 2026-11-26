namespace System.Reflection;

// Roslyn emits this attribute for types that declare an indexer.
public sealed class DefaultMemberAttribute : Attribute
{
    public DefaultMemberAttribute(string memberName)
    {
    }
}
