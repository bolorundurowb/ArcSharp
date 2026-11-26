// The only library the Roslyn frontend binds against. Method bodies exist so
// the C# compiler can emit the assembly; ArcSharp never executes them.
//
// Predefined type names (Object, String, Void, the numeric structs, Array,
// ValueType) must stay in namespace System. Roslyn looks them up by those
// names. String.Concat and Object.ToString also let Roslyn bind concatenation
// before ArcSharp lowers it.
//
// Every type a program names through `using System` is in namespace System,
// including Console. System.Console.WriteLine and a using System directive
// both bind. There is no global Console.

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

        // Roslyn binds the equality members that a record synthesises as
        // overrides of these two. A direct call is not a supported library
        // feature.
        public virtual bool Equals(object other)
        {
            return false;
        }

        public virtual int GetHashCode()
        {
            return 0;
        }

        // The C# compiler binds a '~T()' declaration as an override of this
        // method. It is not callable from user code, and ArcSharp never
        // dispatches through it: the generated teardown runs the finalisers.
#pragma warning disable CS0465
        protected virtual void Finalize()
        {
        }
#pragma warning restore CS0465
    }

    public abstract class ValueType
    {
    }

    public struct Nullable<T> where T : struct
    {
        public Nullable(T value)
        {
        }

        public bool HasValue => false;

        public T Value => default;

        public T GetValueOrDefault() => default;

        public T GetValueOrDefault(T defaultValue) => defaultValue;
    }

    // The C# compiler cannot emit this assembly without Attribute and
    // AttributeUsageAttribute. AttributeTargets is an enum, so System.Enum
    // has to exist as well. Enum declarations are not a supported language
    // feature; the Roslyn lowering has to reject them.
    public abstract class Enum : ValueType
    {
    }

    // Roslyn needs these predefined types to bind source delegates.
    public struct IntPtr
    {
    }

    // Every delegate type derives from MulticastDelegate. The compiler lowers a
    // delegate declaration itself. Roslyn names Combine and Remove when it emits
    // '+=' and '-=', so they exist here, but a program that calls them directly
    // is rejected, because the compiler lowers the operators on its own.
    public abstract class Delegate
    {
        public static Delegate Combine(Delegate a, Delegate b)
        {
            return a;
        }

        public static Delegate Remove(Delegate source, Delegate value)
        {
            return source;
        }
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

    // Roslyn attaches this attribute to the compiler-generated parameter array metadata.
    public sealed class ParamArrayAttribute : Attribute
    {
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
        public override string ToString() => null;
    }

    public struct Char
    {
        public override string ToString() => null;
    }

    public struct SByte
    {
        public override string ToString() => null;
        public string ToString(string format) => null;
    }

    public struct Byte
    {
        public override string ToString() => null;
        public string ToString(string format) => null;
    }

    public struct Int16
    {
        public override string ToString() => null;
        public string ToString(string format) => null;
    }

    public struct UInt16
    {
        public override string ToString() => null;
        public string ToString(string format) => null;
    }

    public struct Int32
    {
        public override string ToString() => null;
        public string ToString(string format) => null;
    }

    public struct UInt32
    {
        public override string ToString() => null;
        public string ToString(string format) => null;
    }

    public struct Int64
    {
        public override string ToString() => null;
        public string ToString(string format) => null;
    }

    public struct UInt64
    {
        public override string ToString() => null;
        public string ToString(string format) => null;
    }

    // Roslyn requires this predefined type when it emits a list pattern.
    public struct UIntPtr
    {
    }

    public struct Single
    {
        public override string ToString() => null;
        public string ToString(string format) => null;
    }

    public struct Double
    {
        public override string ToString() => null;
        public string ToString(string format) => null;
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

        public char this[int index] => default;
        public override string ToString() => this;
        public bool Equals(string value) => default;
        public static bool Equals(string a, string b) => default;
        public string Substring(int startIndex) => null;
        public string Substring(int startIndex, int length) => null;
        public bool Contains(string value) => default;
        public bool Contains(char value) => default;
        public int IndexOf(string value) => default;
        public int IndexOf(char value) => default;
        public bool StartsWith(string value) => default;
        public bool EndsWith(string value) => default;
        public static bool IsNullOrEmpty(string value) => default;
        public static bool IsNullOrWhiteSpace(string value) => default;
        public string Trim() => null;
        public string Replace(char oldChar, char newChar) => null;
        public string Replace(string oldValue, string newValue) => null;
        public static string Format(string format, params object[] args) => null;

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

    public static class Environment
    {
        public static string NewLine => null;
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

    // The finalisation surface. ArcSharp finalises at the last strong release,
    // so Collect and WaitForPendingFinalizers have nothing to do and
    // ReRegisterForFinalize is rejected by the lowering. There is no
    // GC.KeepAlive, and no way to read a generation or a memory total.
    public static class GC
    {
        public static void SuppressFinalize(object obj)
        {
        }

        public static void ReRegisterForFinalize(object obj)
        {
        }

        public static void Collect()
        {
        }

        public static void WaitForPendingFinalizers()
        {
        }
    }

    public static class Console
    {
        public static void Write(object value)
        {
        }

        public static void WriteLine(object value)
        {
        }

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
