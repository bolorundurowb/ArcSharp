// Roslyn gives `decimal` its operators and conversions by looking up the members
// of System.Decimal. ArcSharp never executes these bodies. The compiler lowers
// each operator to a method it writes itself, and each conversion to a runtime
// call, so only what a program can name or reach is declared here.
namespace System
{
    public struct Decimal
    {
        public const decimal Zero = 0m;
        public const decimal One = 1m;
        public const decimal MinusOne = -1m;
        public const decimal MaxValue = 79228162514264337593543950335m;
        public const decimal MinValue = -79228162514264337593543950335m;

        // Roslyn writes a decimal constant in this assembly through these
        // constructors. ArcSharp does not lower a direct call.
        public Decimal(int value)
        {
        }

        public Decimal(uint value)
        {
        }

        public Decimal(long value)
        {
        }

        public Decimal(ulong value)
        {
        }

        public Decimal(int lo, int mid, int hi, bool isNegative, byte scale)
        {
        }

        public override bool Equals(object o)
        {
            return false;
        }

        public override int GetHashCode()
        {
            return 0;
        }

        public override string ToString()
        {
            return null;
        }

        public static decimal operator +(decimal d1, decimal d2)
        {
            return default;
        }

        public static decimal operator -(decimal d1, decimal d2)
        {
            return default;
        }

        public static decimal operator *(decimal d1, decimal d2)
        {
            return default;
        }

        public static decimal operator /(decimal d1, decimal d2)
        {
            return default;
        }

        public static decimal operator %(decimal d1, decimal d2)
        {
            return default;
        }

        public static decimal operator -(decimal d)
        {
            return default;
        }

        public static decimal operator ++(decimal d)
        {
            return default;
        }

        public static decimal operator --(decimal d)
        {
            return default;
        }

        public static bool operator ==(decimal d1, decimal d2)
        {
            return false;
        }

        public static bool operator !=(decimal d1, decimal d2)
        {
            return false;
        }

        public static bool operator <(decimal d1, decimal d2)
        {
            return false;
        }

        public static bool operator <=(decimal d1, decimal d2)
        {
            return false;
        }

        public static bool operator >(decimal d1, decimal d2)
        {
            return false;
        }

        public static bool operator >=(decimal d1, decimal d2)
        {
            return false;
        }

        public static implicit operator decimal(sbyte value)
        {
            return default;
        }

        public static implicit operator decimal(byte value)
        {
            return default;
        }

        public static implicit operator decimal(short value)
        {
            return default;
        }

        public static implicit operator decimal(ushort value)
        {
            return default;
        }

        public static implicit operator decimal(char value)
        {
            return default;
        }

        public static implicit operator decimal(int value)
        {
            return default;
        }

        public static implicit operator decimal(uint value)
        {
            return default;
        }

        public static implicit operator decimal(long value)
        {
            return default;
        }

        public static implicit operator decimal(ulong value)
        {
            return default;
        }

        public static explicit operator decimal(float value)
        {
            return default;
        }

        public static explicit operator decimal(double value)
        {
            return default;
        }

        public static explicit operator sbyte(decimal value)
        {
            return default;
        }

        public static explicit operator byte(decimal value)
        {
            return default;
        }

        public static explicit operator short(decimal value)
        {
            return default;
        }

        public static explicit operator ushort(decimal value)
        {
            return default;
        }

        public static explicit operator char(decimal value)
        {
            return default;
        }

        public static explicit operator int(decimal value)
        {
            return default;
        }

        public static explicit operator uint(decimal value)
        {
            return default;
        }

        public static explicit operator long(decimal value)
        {
            return default;
        }

        public static explicit operator ulong(decimal value)
        {
            return default;
        }

        public static explicit operator float(decimal value)
        {
            return default;
        }

        public static explicit operator double(decimal value)
        {
            return default;
        }
    }
}

namespace System.Runtime.CompilerServices
{
    // Roslyn stores the value of a const decimal field in this attribute.
    public sealed class DecimalConstantAttribute : Attribute
    {
        public DecimalConstantAttribute(byte scale, byte sign, uint hi, uint mid, uint low)
        {
        }
    }
}
