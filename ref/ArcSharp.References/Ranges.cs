namespace System
{
    // Roslyn binds list and slice patterns against these metadata members.
    public readonly struct Index
    {
        public Index(int value, bool fromEnd = false)
        {
        }

        public int GetOffset(int length) => 0;
    }

    public readonly struct Range
    {
        public Range(Index start, Index end)
        {
        }
    }
}

namespace System.Runtime.CompilerServices
{
    public static class RuntimeHelpers
    {
        public static T[] GetSubArray<T>(T[] array, System.Range range) => null;
    }
}
