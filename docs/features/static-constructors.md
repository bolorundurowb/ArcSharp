# Static constructors

An explicit static constructor runs once for each closed type before the
first relevant access. Static field and auto-property initialisers run
before its body. Access from the initialising thread can see the fields
already set by that body. Constructor arguments evaluate before
initialising the type and allocating the instance.

A thrown exception becomes a `TypeInitializationException`. The wrapper's
`TypeName` and `InnerException` identify the failed type and original cause.
The compiler retains one wrapper in the type's failure slot and rethrows it
on later accesses. The entry point releases those cached references before
printing ARC accounting.

The verification harness checks field initialisation, recursive access, the
order of constructor arguments and static execution, and failed
initialisation across repeated calls. Roslyn rejects parameters on a
static constructor.
