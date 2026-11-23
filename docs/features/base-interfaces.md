# Base interfaces

An interface can inherit several base interfaces. A class implementing a
derived interface also supplies the slots for its transitive bases. Each
shared base in a diamond appears once. The compiler uses Roslyn's explicit
implementation links and signature matching for implicit implementations.

A derived class inherits the interface table from its base. If it overrides
a virtual implementation, its inherited slot targets the override. An
ordinary hidden method leaves the existing mapping intact unless the derived
class declares that interface again.

The verification harness checks a diamond, explicit methods and a property,
and an override called through more than one interface type. A negative
sample checks Roslyn's diagnostic for an omitted inherited interface member.
