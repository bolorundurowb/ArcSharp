// The only library the Roslyn frontend binds against. Method bodies exist so
// the C# compiler can emit the assembly; ArcSharp never executes them.
//
// Predefined type names (Object, String, Void, the numeric structs, Array,
// ValueType) must stay in namespace System. Roslyn looks them up by those
// names. Do not add members the compiler cannot lower. String.Concat and
// Object.ToString are public because Roslyn lowers + through them, and a
// direct call is not a supported library feature. There is no equality
// operator on String, so == stays reference equality.
//
// Every type a program names through `using System` is in namespace System,
// including Console. System.Console.WriteLine and a using System directive
// both bind. There is no global Console. The legacy `weak` modifier is
// rewritten to ArcSharp.Runtime.WeakAttribute, which is not part of the
// language surface.

namespace System
{
    public class Object
    {
        public Object()
        {
        }

        // Roslyn lowers string concatenation of a non-string through this
        // method. A direct call is not a supported library feature.
        public virtual string ToString()
        {
            return null;
        }
    }

    public abstract class ValueType
    {
    }

    // The C# compiler cannot emit this assembly without Attribute and
    // AttributeUsageAttribute. AttributeTargets is an enum, so System.Enum
    // has to exist as well. Enum declarations are not a supported language
    // feature; the Roslyn lowering has to reject them.
    public abstract class Enum : ValueType
    {
    }

    // Roslyn asks for these predefined types while binding operators such as
    // char equality. They are not a supported language feature.
    public abstract class Delegate
    {
    }

    public abstract class MulticastDelegate : Delegate
    {
    }

    public abstract class Attribute
    {
    }

    public enum AttributeTargets
    {
        Assembly = 0x0001,
        Module = 0x0002,
        Class = 0x0004,
        Struct = 0x0008,
        Enum = 0x0010,
        Constructor = 0x0020,
        Method = 0x0040,
        Property = 0x0080,
        Field = 0x0100,
        Event = 0x0200,
        Interface = 0x0400,
        Parameter = 0x0800,
        Delegate = 0x1000,
        ReturnValue = 0x2000,
        GenericParameter = 0x4000,
        All = Assembly | Module | Class | Struct | Enum | Constructor
            | Method | Property | Field | Event | Interface | Parameter
            | Delegate | ReturnValue | GenericParameter
    }

    public sealed class AttributeUsageAttribute : Attribute
    {
        public AttributeUsageAttribute(AttributeTargets validOn)
        {
            Inherited = true;
        }

        public bool AllowMultiple { get; set; }

        public bool Inherited { get; set; }
    }

    public struct Void
    {
    }

    public struct Boolean
    {
    }

    public struct Char
    {
    }

    public struct SByte
    {
    }

    public struct Byte
    {
    }

    public struct Int16
    {
    }

    public struct UInt16
    {
    }

    public struct Int32
    {
    }

    public struct UInt32
    {
    }

    public struct Int64
    {
    }

    public struct UInt64
    {
    }

    public struct Single
    {
    }

    public struct Double
    {
    }

    public sealed class String
    {
        private String()
        {
        }

        public int Length
        {
            get { return 0; }
        }

        public static string Concat(string str0, string str1)
        {
            return null;
        }

        public static string Concat(string str0, string str1, string str2)
        {
            return null;
        }

        public static string Concat(string str0, string str1, string str2, string str3)
        {
            return null;
        }

        public static string Concat(object arg0, object arg1)
        {
            return null;
        }

        public static string Concat(object arg0, object arg1, object arg2)
        {
            return null;
        }

        public static string Concat(object arg0, object arg1, object arg2, object arg3)
        {
            return null;
        }

        public static string Concat(string str0, object arg1)
        {
            return null;
        }

        public static string Concat(object arg0, string str1)
        {
            return null;
        }

        public static string Concat(string[] values)
        {
            return null;
        }

        public static string Concat(object[] args)
        {
            return null;
        }
    }

    public abstract class Array
    {
        public int Length
        {
            get { return 0; }
        }
    }

    public sealed class WeakReference<T> where T : class
    {
        public WeakReference(T target)
        {
        }

        public bool TryGetTarget(out T target)
        {
            target = null;
            return false;
        }

        public void SetTarget(T target)
        {
        }
    }

    public interface IDisposable
    {
        void Dispose();
    }

    public static class Console
    {
        public static void Write()
        {
        }

        public static void Write(string value)
        {
        }

        public static void Write(bool value)
        {
        }

        public static void Write(char value)
        {
        }

        public static void Write(byte value)
        {
        }

        public static void Write(sbyte value)
        {
        }

        public static void Write(short value)
        {
        }

        public static void Write(ushort value)
        {
        }

        public static void Write(int value)
        {
        }

        public static void Write(uint value)
        {
        }

        public static void Write(long value)
        {
        }

        public static void Write(ulong value)
        {
        }

        public static void Write(float value)
        {
        }

        public static void Write(double value)
        {
        }

        public static void WriteLine()
        {
        }

        public static void WriteLine(string value)
        {
        }

        public static void WriteLine(bool value)
        {
        }

        public static void WriteLine(char value)
        {
        }

        public static void WriteLine(byte value)
        {
        }

        public static void WriteLine(sbyte value)
        {
        }

        public static void WriteLine(short value)
        {
        }

        public static void WriteLine(ushort value)
        {
        }

        public static void WriteLine(int value)
        {
        }

        public static void WriteLine(uint value)
        {
        }

        public static void WriteLine(long value)
        {
        }

        public static void WriteLine(ulong value)
        {
        }

        public static void WriteLine(float value)
        {
        }

        public static void WriteLine(double value)
        {
        }
    }
}

namespace ArcSharp.Runtime
{
    // The legacy `weak` field modifier is rewritten to this attribute before
    // Roslyn sees the file. It is not a supported attribute to write by hand.
    public sealed class WeakAttribute : System.Attribute
    {
        public WeakAttribute()
        {
        }
    }
}
