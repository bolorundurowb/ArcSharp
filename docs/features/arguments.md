# Named, optional and parameter-array arguments

Named arguments evaluate in source order. The emitter then places their
values into parameter order for the call. Constructors use the same ordering
as methods. For an omitted optional argument, the compiler uses the default
value supplied by Roslyn. An `in` argument that requires a temporary gets a
slot valid for the call.

Expanded `params T[]` arguments build an array and evaluate its elements in
order. The compiler tracks the array during element evaluation, so an
exception releases both the array and any elements already stored. When a
caller supplies an array directly, the call uses that array.

The verification harness checks argument order, defaults, existing and
expanded arrays, and the ARC counts after a throwing element. It also checks
Roslyn diagnostics for invalid parameter names and declarations.
