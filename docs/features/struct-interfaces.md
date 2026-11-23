# Struct interfaces, boxing and printing

A struct can implement interfaces explicitly or implicitly. A conversion to
an interface or `object` allocates an ARC object with a copy of the struct
inside it. The interface table points to methods that call the struct
implementation on that copy. A constrained generic call on a known struct
can call the implementation directly.

Unboxing validates the stored type and copies the value into struct storage.
Null and invalid objects raise the corresponding run-time exceptions.
Struct copies retain and release their reference fields, including those
inside a box.

`Console.Write` and `Console.WriteLine` use a known struct value's
`ToString()` override, or print its ArcSharp type name. These overloads
support direct struct values. Formatting an arbitrary boxed object remains
outside the current console surface.

The verification harness checks a boxed mutation, an explicit implementation,
a constrained call, a failed unbox and struct output. A negative sample
checks Roslyn's diagnostic for a missing interface implementation.
