using ArcSharp.Binding;
using Microsoft.CodeAnalysis;
using BoundKind = ArcSharp.Binding.TypeKind;

namespace ArcSharp.Frontend;

sealed partial class RoslynLowering
{
    private void MapVariantInterfaces(IEnumerable<TypeSymbol> types)
    {
        if (_variantInterfaces.Count == 0)
        {
            return;
        }

        foreach (var type in types.Where(type => type.Kind is BoundKind.Class or BoundKind.Struct))
        {
            var implemented = new HashSet<TypeSymbol>();
            for (var current = type; current != null; current = current.BaseType)
            {
                foreach (var source in DeclaredInterfaceClosure(current))
                {
                    if (!implemented.Add(source) || !_variantInterfaces.ContainsKey(source))
                    {
                        continue;
                    }

                    foreach (var target in _variantInterfaces.Keys)
                    {
                        if (target == source || !VariantConversion(source, target, []))
                        {
                            continue;
                        }

                        // Both interfaces specialise the same definition, so
                        // their declared methods keep the same ordinal.
                        for (var i = 0; i < source.Methods.Count; i++)
                        {
                            if (_selectors.TryGetValue(source.Methods[i], out var sourceSlot)
                                && type.InterfaceImpl.TryGetValue(sourceSlot, out var implementation)
                                && _selectors.TryGetValue(target.Methods[i], out var targetSlot))
                            {
                                type.InterfaceImpl.TryAdd(targetSlot, implementation);
                            }
                        }
                    }
                }
            }
        }
    }

    private bool VariantConversion(TypeSymbol from, TypeSymbol to, HashSet<(TypeSymbol From, TypeSymbol To)> visiting)
    {
        if (!_variantInterfaces.TryGetValue(from, out var source)
            || !_variantInterfaces.TryGetValue(to, out var target)
            || !SymbolEqualityComparer.Default.Equals(source, target))
        {
            return false;
        }

        for (var i = 0; i < source.TypeParameters.Length; i++)
        {
            var left = from.TypeArguments[i];
            var right = to.TypeArguments[i];
            if (left == right)
            {
                continue;
            }

            var compatible = source.TypeParameters[i].Variance switch
            {
                VarianceKind.Out => ReferenceAssignable(left, right, visiting),
                VarianceKind.In => ReferenceAssignable(right, left, visiting),
                _ => false
            };
            if (!compatible)
            {
                return false;
            }
        }

        return true;
    }

    private bool ReferenceAssignable(TypeSymbol from, TypeSymbol to, HashSet<(TypeSymbol From, TypeSymbol To)> visiting)
    {
        if (!from.IsReferenceType || !to.IsReferenceType)
        {
            return false;
        }

        if (from == to || to == _object)
        {
            return true;
        }

        if (!visiting.Add((from, to)))
        {
            return false;
        }

        var compatible = (from.BaseType != null && ReferenceAssignable(from.BaseType, to, visiting))
            || from.Interfaces.Any(parent => ReferenceAssignable(parent, to, visiting))
            || (from.Kind == BoundKind.Array && to.Kind == BoundKind.Array
                && from.ElementType is { } source && to.ElementType is { } target
                && ReferenceAssignable(source, target, visiting))
            || VariantConversion(from, to, visiting);
        visiting.Remove((from, to));
        return compatible;
    }
}
