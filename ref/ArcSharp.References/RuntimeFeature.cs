namespace System.Runtime.CompilerServices
{
    // Roslyn accepts `static abstract` interface members only when the core
    // library declares this feature name.
    public static class RuntimeFeature
    {
        public const string VirtualStaticsInInterfaces = nameof(VirtualStaticsInInterfaces);
    }
}
