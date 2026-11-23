namespace System.Runtime.CompilerServices
{
    // Roslyn marks a static class and its methods with this attribute when
    // it binds an extension method declaration.
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class ExtensionAttribute : Attribute
    {
    }
}
