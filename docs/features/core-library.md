# Core library sources

ArcSharp embeds the C# files under `corelib/` and compiles them with every
program. This gives their types the same lowering and ARC ownership rules as
user code. Resource names preserve paths relative to `corelib/`, so library
files in different directories can share a filename.

`ArcSharp.References.dll` builds those same files so Roslyn clients using the
assembly alone can bind to the exception and delegate types. Sharing source
keeps the public declarations in sync. ArcSharp's own Roslyn compilation
prefers the embedded source declarations when both are available. The frontend
suppresses Roslyn's `CS0436` source-versus-metadata conflict warning for the
compilation because this overlap is expected.

The verification harness checks exception and delegate APIs against the
reference assembly alone. Its native samples exercise both sets of types
through the compiler and check their ARC accounting.
