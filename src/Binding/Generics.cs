using ArcSharp.Diagnostics;
using ArcSharp.Syntax;

namespace ArcSharp.Binding;

public sealed partial class Binder
{
    private readonly Dictionary<string, TypeDecl> _genericTypes = [];
    private readonly HashSet<TypeSymbol> _preparedTypes = [];
    private readonly HashSet<TypeSymbol> _resolvedBases = [];
    private readonly HashSet<MethodSymbol> _usedGenericMethods = [];
    private readonly List<(List<string> Parameters, List<GenericConstraint> Constraints, List<TypeSymbol> Arguments, int Line)> _constraintChecks = [];
    private int _instantiationDepth;
    private int _instantiationCount;

    private IEnumerable<TypeSymbol> UserTypes() => _types.Values.Where(t =>
        t.Kind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface);

    private static TypeDecl Declaration(TypeSymbol t) =>
        (TypeDecl?)t.ClassSyntax ?? (TypeDecl?)t.StructSyntax ?? t.InterfaceSyntax!;

    private void ResolveBases(TypeSymbol t)
    {
        if (!_resolvedBases.Add(t) || t.ClassSyntax is not { } declaration)
        {
            return;
        }
        foreach (var syntax in declaration.Bases)
        {
            var parent = ResolveType(syntax);
            if (parent.Kind == TypeKind.Class)
            {
                if (t.BaseType != null || parent == t || parent.IsSubclassOf(t))
                {
                    Report(syntax.Line, $"invalid base class for '{t}'");
                    continue;
                }
                t.BaseType = parent;
            }
            else if (parent.Kind == TypeKind.Interface)
            {
                t.Interfaces.Add(parent);
            }
            else if (parent != _error)
            {
                Report(syntax.Line, $"'{parent}' cannot be a base of a class");
            }
        }
    }

    private void PrepareType(TypeSymbol t)
    {
        if (!_preparedTypes.Add(t))
        {
            return;
        }
        ResolveBases(t);
        if (t.BaseType != null)
        {
            PrepareType(t.BaseType);
        }
        foreach (var iface in t.Interfaces)
        {
            PrepareType(iface);
        }
        if (t.Kind == TypeKind.Interface)
        {
            foreach (var m in Declaration(t).Members.OfType<MethodDecl>())
            {
                var key = SelectorKey(t.Name, m.Name, m.Parameters);
                if (!_selectors.ContainsKey(key))
                {
                    _selectors[key] = _selectors.Count;
                }
            }
        }
        ExpandProperties(t);
        LayoutType(t);
    }

    // UTF-8 hex is injective and contains only LLVM identifier
    // characters. Source identifiers cannot contain '$', so user types cannot
    // collide with generated specialisations.
    private static string SpecialisedName(string name, IEnumerable<TypeSymbol> arguments) =>
        "$g$" + System.Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(
            name + "<" + string.Join(",", arguments.Select(t => t.ToString())) + ">"));

    private bool BeginInstantiation(int line)
    {
        // Expanding G<T> into G<G<T>> must produce a diagnostic, not overflow
        // the compiler stack or exhaust memory. Ordinary recursive G<T> reuses
        // the cache before reaching this guard.
        if (_instantiationDepth >= 64 || _instantiationCount >= 1024)
        {
            Report(line, "generic specialisation limit exceeded (64 nested or 1024 total instantiations)", "ARC2013");
            return false;
        }
        _instantiationDepth++;
        _instantiationCount++;
        return true;
    }

    private TypeSymbol InstantiateType(TypeSyntax syntax)
    {
        var args = syntax.TypeArgs.Select(ResolveType).ToList();
        if (args.Any(t => t == _error || t == _void))
        {
            Report(syntax.Line, "generic arguments must be valid non-void types", "ARC2013");
            return _error;
        }
        if (!_genericTypes.TryGetValue(syntax.Name + "`" + args.Count, out var template))
        {
            Report(syntax.Line, $"generic type '{syntax.Name}' with {args.Count} type arguments is not declared", "ARC2013");
            return _error;
        }
        var name = SpecialisedName(syntax.Name, args);
        if (_types.TryGetValue(name, out var cached))
        {
            return cached;
        }
        if (!BeginInstantiation(syntax.Line))
        {
            return _error;
        }
        try
        {
            var substitution = Substitution(template.TypeParameters, args);
            var declaration = substitution.Declaration(template, name);
            var type = new TypeSymbol
            {
                Name = name, GenericDefinition = syntax.Name, TypeArguments = args,
                Kind = template switch { ClassDecl => TypeKind.Class, StructDecl => TypeKind.Struct, _ => TypeKind.Interface },
                ClassSyntax = declaration as ClassDecl, StructSyntax = declaration as StructDecl,
                InterfaceSyntax = declaration as InterfaceDecl
            };
            _types.Add(name, type);
            _constraintChecks.Add((template.TypeParameters, template.Constraints, args, syntax.Line));
            PrepareType(type);
            return type;
        }
        finally
        {
            _instantiationDepth--;
        }
    }

    private static TypeSubstitution Substitution(List<string> parameters, List<TypeSymbol> args) =>
        new(parameters.Select((p, i) => (p, t: TypeReference(args[i])))
            .ToDictionary(x => x.p, x => x.t));

    private static TypeSyntax TypeReference(TypeSymbol type)
    {
        if (type.Kind == TypeKind.Array)
        {
            var syntax = TypeReference(type.ElementType!);
            syntax.ArrayRank++;
            return syntax;
        }
        if (type.Kind == TypeKind.WeakRef)
        {
            return new TypeSyntax { Name = "WeakReference", TypeArgs = [TypeReference(type.ElementType!)] };
        }
        return new TypeSyntax { Name = type.Name };
    }

    private bool TryResolveStaticType(NameExpr name, out TypeSymbol type)
    {
        if (name.TypeArgs.Count > 0)
        {
            type = ResolveType(new TypeSyntax { Name = name.Name, TypeArgs = name.TypeArgs, Line = name.Line });
            return true;
        }
        return _types.TryGetValue(name.Name, out type!);
    }

    private void ValidateConstraints(List<string> parameters, List<GenericConstraint> constraints)
    {
        var seen = new HashSet<string>();
        foreach (var constraint in constraints)
        {
            if (!parameters.Contains(constraint.Parameter) || !seen.Add(constraint.Parameter)
                || (constraint.ReferenceType && constraint.ValueType))
            {
                Report(constraint.Line, $"invalid constraints for '{constraint.Parameter}'", "ARC2013");
            }
        }
    }

    private void CheckConstraints(List<string> parameters, List<GenericConstraint> constraints, List<TypeSymbol> args, int line)
    {
        var substitution = Substitution(parameters, args);
        foreach (var constraint in constraints)
        {
            var index = parameters.IndexOf(constraint.Parameter);
            if (index < 0)
            {
                continue;
            }
            var arg = args[index];
            var valid = (!constraint.ReferenceType || arg.IsReferenceType)
                && (!constraint.ValueType || arg.Kind is TypeKind.Struct or TypeKind.Primitive);
            foreach (var syntax in constraint.Types)
            {
                var required = ResolveType(substitution.Type(syntax));
                valid &= arg == required || arg.IsSubclassOf(required) || arg.Implements(required);
            }
            if (constraint.Constructor && arg.IsReferenceType)
            {
                valid &= arg.Kind == TypeKind.Class && arg.ClassSyntax is { IsAbstract: false }
                    && (!arg.Methods.Any(m => m.IsConstructor)
                        || arg.Methods.Any(m => m.IsConstructor && m.Parameters.Count == 0 && m.Syntax?.IsPublic == true));
            }
            if (!valid)
            {
                Report(line, $"type '{arg}' does not satisfy constraints for '{constraint.Parameter}'", "ARC2014");
            }
        }
    }

    private MethodSymbol? SelectInvocationMethod(TypeSymbol type, string name, List<BoundExpr> args,
        List<TypeSyntax> explicitArguments, bool staticOnly, int line, string noneMessage)
    {
        var candidates = explicitArguments.Count == 0 ? CollectMethods(type, name, args.Count, staticOnly) : [];
        var sawGeneric = false;
        for (TypeSymbol? owner = type; owner != null; owner = owner.BaseType)
        {
            if (owner.ClassSyntax == null && owner.StructSyntax == null && owner.InterfaceSyntax == null)
            {
                continue;
            }
            foreach (var template in Declaration(owner).Members.OfType<MethodDecl>().Where(m =>
                m.Name == name && m.TypeParameters.Count > 0 && m.Parameters.Count == args.Count && (!staticOnly || m.IsStatic)).ToList())
            {
                sawGeneric = true;
                List<TypeSymbol>? typeArgs;
                if (explicitArguments.Count > 0)
                {
                    typeArgs = explicitArguments.Select(ResolveType).ToList();
                    if (typeArgs.Count != template.TypeParameters.Count)
                    {
                        continue;
                    }
                }
                else
                {
                    typeArgs = InferTypeArguments(template, args);
                }
                if (typeArgs == null || typeArgs.Any(t => t == _error || t == _null || t == _void))
                {
                    continue;
                }
                var method = InstantiateMethod(owner, template, typeArgs, line);
                if (method != null && Applicable(method, args))
                {
                    candidates.Add(method);
                }
            }
        }
        if (!sawGeneric && explicitArguments.Count == 0)
        {
            return SelectMethod(type, name, args, staticOnly, line, noneMessage);
        }
        if (candidates.Count == 0)
        {
            Report(line, noneMessage + " (no applicable overload or generic type arguments)", "ARC2013");
            return null;
        }
        var applicable = ApplicableOnes(candidates, args);
        var best = applicable.Count == 0 ? null : PickBest(applicable, args, name, line);
        if (best == null)
        {
            if (applicable.Count == 0)
            {
                Report(line, $"no overload of '{name}' matches the arguments");
            }
            return null;
        }
        if (best.GenericDefinition is { } definition && _usedGenericMethods.Add(best))
        {
            _constraintChecks.Add((definition.TypeParameters, definition.Constraints, best.TypeArguments, line));
        }
        return best;
    }

    private List<TypeSymbol>? InferTypeArguments(MethodDecl template, List<BoundExpr> args)
    {
        var inferred = new Dictionary<string, TypeSymbol>();
        bool Infer(TypeSyntax syntax, TypeSymbol actual)
        {
            if (syntax.ArrayRank > 0)
            {
                if (actual.Kind != TypeKind.Array)
                {
                    return false;
                }
                return Infer(new TypeSyntax { Name = syntax.Name, TypeArgs = syntax.TypeArgs }, actual.ElementType!);
            }
            if (template.TypeParameters.Contains(syntax.Name))
            {
                if (inferred.TryGetValue(syntax.Name, out var existing))
                {
                    return existing == actual;
                }
                inferred[syntax.Name] = actual;
                return true;
            }
            if (syntax.TypeArgs.Count > 0)
            {
                var actualArgs = actual.Kind == TypeKind.WeakRef ? new List<TypeSymbol> { actual.ElementType! } : actual.TypeArguments;
                if ((actual.GenericDefinition ?? (actual.Kind == TypeKind.WeakRef ? "WeakReference" : actual.Name)) != syntax.Name
                    || actualArgs.Count != syntax.TypeArgs.Count)
                {
                    return false;
                }
                for (var i = 0; i < actualArgs.Count; i++)
                {
                    if (!Infer(syntax.TypeArgs[i], actualArgs[i]))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        for (var i = 0; i < args.Count; i++)
        {
            if (!Infer(template.Parameters[i].Type, args[i].Type))
            {
                return null;
            }
        }
        return template.TypeParameters.All(inferred.ContainsKey)
            ? template.TypeParameters.Select(p => inferred[p]).ToList() : null;
    }

    private MethodSymbol? InstantiateMethod(TypeSymbol owner, MethodDecl template, List<TypeSymbol> args, int line)
    {
        var cached = owner.Methods.FirstOrDefault(m => m.GenericDefinition == template && m.TypeArguments.SequenceEqual(args));
        if (cached != null)
        {
            return cached;
        }
        if (!BeginInstantiation(line))
        {
            return null;
        }
        try
        {
            var syntax = Substitution(template.TypeParameters, args).Method(template);
            syntax.TypeParameters.Clear();
            var method = new MethodSymbol
            {
                Name = SpecialisedName(template.Name + "`" + Declaration(owner).Members.IndexOf(template), args),
                Owner = owner, ReturnType = ResolveType(syntax.ReturnType!), IsStatic = template.IsStatic,
                Syntax = syntax, GenericDefinition = template, TypeArguments = args
            };
            for (var i = 0; i < syntax.Parameters.Count; i++)
            {
                var p = syntax.Parameters[i];
                var type = ResolveType(p.Type);
                if (type.Kind == TypeKind.Struct && p.RefKind == RefKind.None)
                {
                    Report(line, "generic struct arguments must be passed with 'ref', 'out', or 'in'", DiagnosticCatalog.UnsupportedConstructId);
                }
                method.Parameters.Add(new ParamSymbol { Name = p.Name, Type = type, RefKind = p.RefKind, Index = i });
            }
            if (method.ReturnType.Kind == TypeKind.Struct)
            {
                Report(line, "struct return values are not supported yet", DiagnosticCatalog.UnsupportedConstructId);
            }
            owner.Methods.Add(method);
            return method;
        }
        finally
        {
            _instantiationDepth--;
        }
    }
}
