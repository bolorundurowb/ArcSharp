# Properties and indexers

Properties and indexers can have block or expression getter and setter
bodies. Virtual and interface accessors dispatch through the same tables as
methods. Indexer arguments follow the same evaluation and parameter ordering
as method arguments.

An auto-property uses a backing field. Its initialiser runs with other
field initialisers, and the declaring constructor can assign a get-only
auto-property. An assignment to a property or indexer yields the assigned
value after the setter completes. Reference results carry their own ARC
ownership, independent of anything the setter stores.

The verification harness checks a virtual expression getter, an interface
indexer, a get-only initialiser and a reference-valued assignment. Accessors
with bodies declared in interfaces and ref-returning accessors remain
unsupported, as do compound updates through user-written accessors.
