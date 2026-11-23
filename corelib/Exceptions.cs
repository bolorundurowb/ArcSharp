// The exception types every ArcSharp program can throw and catch. The compiler
// embeds this file and compiles it with the program, so these are ordinary
// classes: they have a layout, a vtable, and a type descriptor like any other.
//
// The runtime builds the exceptions that the language itself raises, such as an
// index out of range, without running a constructor. It stores the message in
// the first field, so 'Message' has to stay the first instance field of
// Exception. The emitter also hands the runtime the type descriptors of the
// classes marked below, so renaming one needs a change in
// Emitter.RuntimeExceptionTypes.
//
// There is no InnerException and no StackTrace. A field of type Exception would
// be a strong self-reference and would raise ARC3100 for every program.

namespace System
{
    public class Exception
    {
        public Exception()
        {
            Message = "Exception of type 'System.Exception' was thrown.";
        }

        public Exception(string message)
        {
            Message = message;
        }

        public string Message { get; }
    }

    public class SystemException : Exception
    {
        public SystemException() : base("System error.")
        {
        }

        public SystemException(string message) : base(message)
        {
        }
    }

    public class ArithmeticException : SystemException
    {
        public ArithmeticException() : base("Overflow or underflow in the arithmetic operation.")
        {
        }

        public ArithmeticException(string message) : base(message)
        {
        }
    }

    // Raised by the runtime.
    public class DivideByZeroException : ArithmeticException
    {
        public DivideByZeroException() : base("Attempted to divide by zero.")
        {
        }

        public DivideByZeroException(string message) : base(message)
        {
        }
    }

    // Raised by the runtime.
    public class OverflowException : ArithmeticException
    {
        public OverflowException() : base("Arithmetic operation resulted in an overflow.")
        {
        }

        public OverflowException(string message) : base(message)
        {
        }
    }

    // Raised by the runtime.
    public class IndexOutOfRangeException : SystemException
    {
        public IndexOutOfRangeException() : base("Index was outside the bounds of the array.")
        {
        }

        public IndexOutOfRangeException(string message) : base(message)
        {
        }
    }

    // Raised by the runtime.
    public class InvalidCastException : SystemException
    {
        public InvalidCastException() : base("Specified cast is not valid.")
        {
        }

        public InvalidCastException(string message) : base(message)
        {
        }
    }

    // Raised by the runtime.
    public class NullReferenceException : SystemException
    {
        public NullReferenceException() : base("Object reference not set to an instance of an object.")
        {
        }

        public NullReferenceException(string message) : base(message)
        {
        }
    }

    // Raised by the runtime.
    public class ArrayTypeMismatchException : SystemException
    {
        public ArrayTypeMismatchException() : base("Attempted to access an element as a type incompatible with the array.")
        {
        }

        public ArrayTypeMismatchException(string message) : base(message)
        {
        }
    }

    public class ArgumentException : SystemException
    {
        public ArgumentException() : base("Value does not fall within the expected range.")
        {
        }

        public ArgumentException(string message) : base(message)
        {
        }
    }

    public class ArgumentNullException : ArgumentException
    {
        public ArgumentNullException() : base("Value cannot be null.")
        {
        }

        public ArgumentNullException(string message) : base(message)
        {
        }
    }

    public class InvalidOperationException : SystemException
    {
        public InvalidOperationException() : base("Operation is not valid due to the current state of the object.")
        {
        }

        public InvalidOperationException(string message) : base(message)
        {
        }
    }

    public class NotSupportedException : SystemException
    {
        public NotSupportedException() : base("Specified method is not supported.")
        {
        }

        public NotSupportedException(string message) : base(message)
        {
        }
    }

    public class NotImplementedException : SystemException
    {
        public NotImplementedException() : base("The method or operation is not implemented.")
        {
        }

        public NotImplementedException(string message) : base(message)
        {
        }
    }
}
