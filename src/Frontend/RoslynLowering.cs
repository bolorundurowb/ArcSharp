using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ArcSharp.Binding;
using ArcSharp.Diagnostics;
using ArcSharp.Syntax;
using BoundKind = ArcSharp.Binding.TypeKind;
using Diagnostic = ArcSharp.Lexing.Diagnostic;
using DiagnosticSeverity = ArcSharp.Lexing.DiagnosticSeverity;
using RefKind = ArcSharp.Syntax.RefKind;
using RoslynKind = Microsoft.CodeAnalysis.TypeKind;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArcSharp.Frontend;

// Lowers a Roslyn compilation into the bound tree the emitter already consumes.
// Layout, vtables, and interface selectors stay on that tree. Open generic
// definitions are not emitted; each closed use becomes its own type.
sealed partial class RoslynLowering
{
    private readonly CSharpCompilation _compilation;
    private readonly List<(string file, Diagnostic d)> _diags;
    private readonly HashSet<(string file, int line, int column, string id)> _seen = [];
    private readonly Dictionary<ITypeSymbol, TypeSymbol> _substitution = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<INamedTypeSymbol, TypeSymbol> _named = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, TypeSymbol> _generic = [];
    private readonly Dictionary<MemberKey, FieldSymbol> _fields = [];
    private readonly Dictionary<TypeSymbol, List<(FieldSymbol Field, EqualsValueClauseSyntax Syntax)>> _fieldInitialisers = [];
    private readonly Dictionary<MemberKey, MethodSymbol> _methods = [];
    private readonly Dictionary<MethodSymbol, int> _selectors = [];
    private readonly Dictionary<string, TypeSymbol> _arrays = [];
    private readonly Dictionary<string, TypeSymbol> _weakRefs = [];
    private readonly HashSet<TypeSymbol> _filled = [];
    private readonly HashSet<MethodSymbol> _queued = [];
    private readonly Queue<PendingBody> _bodies = [];
    private readonly HashSet<ISymbol> _reported = new(SymbolEqualityComparer.Default);

    private readonly TypeSymbol _int, _long, _bool, _char, _float, _double;
    private readonly TypeSymbol _byte, _sbyte, _short, _ushort, _uint, _ulong;
    private readonly TypeSymbol _void, _string, _null, _error, _object, _disposable;
    private int _depth;
    private int _count;
    private int _localId;
    private int _usingId;
    private TypeSymbol _currentType = null!;
    private MethodSymbol _currentMethod = null!;
    private List<LocalSymbol> _methodLocals = [];
    private readonly Dictionary<ILocalSymbol, LocalSymbol> _locals = new(SymbolEqualityComparer.Default);

    internal RoslynLowering(CSharpCompilation compilation, List<(string file, Diagnostic d)> diags)
    {
        _compilation = compilation;
        _diags = diags;
        _int = Prim("int");
        _long = Prim("long");
        _bool = Prim("bool");
        _char = Prim("char");
        _float = Prim("float");
        _double = Prim("double");
        _byte = Prim("byte");
        _sbyte = Prim("sbyte");
        _short = Prim("short");
        _ushort = Prim("ushort");
        _uint = Prim("uint");
        _ulong = Prim("ulong");
        _void = new TypeSymbol { Name = "void", Kind = BoundKind.Void };
        _string = new TypeSymbol { Name = "string", Kind = BoundKind.String };
        _null = new TypeSymbol { Name = "<null>", Kind = BoundKind.Class };
        _error = new TypeSymbol { Name = "<error>", Kind = BoundKind.Error };
        _object = new TypeSymbol { Name = "object", Kind = BoundKind.Class, HeapSize = 24, LayoutDone = true };
        _disposable = new TypeSymbol { Name = "IDisposable", Kind = BoundKind.Interface };
        var dispose = compilation.GetTypeByMetadataName("System.IDisposable")
            ?.GetMembers("Dispose").OfType<IMethodSymbol>().FirstOrDefault();
        if (dispose != null)
        {
            var method = new MethodSymbol
            {
                Name = "Dispose",
                Owner = _disposable,
                ReturnType = _void,
                IsAbstract = true
            };
            _disposable.Methods.Add(method);
            _methods[new MemberKey(dispose, _disposable)] = method;
            _selectors[method] = _selectors.Count;
        }

        static TypeSymbol Prim(string name) => new() { Name = name, Kind = BoundKind.Primitive };
    }

    public void Lower(BoundProgram program)
    {
        if (HasRoslynError())
        {
            return;
        }

        program.Types.Add(_disposable);
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol)
                {
                    continue;
                }

                if (symbol.ContainingType?.IsGenericType == true)
                {
                    Report(declaration, "types nested in generic types are not supported", DiagnosticCatalog.UnsupportedConstructId);
                    continue;
                }

                if (symbol.IsGenericType)
                {
                    continue;
                }

                EnsureFilled(symbol);
            }
        }

        while (_bodies.Count > 0)
        {
            LowerBody(_bodies.Dequeue());
        }

        program.Types.AddRange(_named.Values.Where(t => t.Kind is BoundKind.Class or BoundKind.Struct or BoundKind.Interface));
        program.Types.AddRange(_generic.Values);
        program.InterfaceSelectorCount = _selectors.Count;
        program.MethodBodies.AddRange(_readyBodies);

        DefiniteAssignment.Analyze(program, (line, msg) => Report(line, 0, msg, "ARC2100", DiagnosticSeverity.Warning));
        CycleAnalysis.Analyze(program, (line, msg) =>
            Report(line, 0, msg, DiagnosticCatalog.PossibleStrongCycleId, DiagnosticSeverity.Warning));

        program.Entry = FindEntry(program.Types);
        if (program.Entry == null)
        {
            Report(0, 0, "no static 'Main()' entry point found", "ARC2050");
        }
    }

    private readonly List<BoundMethodBody> _readyBodies = [];

    private bool HasRoslynError() =>
        _diags.Exists(d => d.d.Severity == DiagnosticSeverity.Error && d.d.Id.StartsWith("CS", StringComparison.Ordinal));

    private TypeSymbol EnsureFilled(INamedTypeSymbol symbol)
    {
        var mapped = MapNamed(symbol, symbol.Locations.FirstOrDefault());
        return mapped;
    }

    private TypeSymbol MapType(ITypeSymbol? type, Location? location = null)
    {
        if (type == null || type.TypeKind == RoslynKind.Error)
        {
            return _error;
        }

        location ??= type.Locations.FirstOrDefault();
        if (type is ITypeParameterSymbol parameter)
        {
            if (_substitution.TryGetValue(parameter, out var substituted))
            {
                return substituted;
            }

            Report(location, $"type parameter '{parameter.Name}' is not instantiated", "ARC2013");
            return _error;
        }

        if (type is IArrayTypeSymbol array)
        {
            if (array.Rank != 1 || array.ElementType is IArrayTypeSymbol)
            {
                Report(location, "jagged and multidimensional arrays are not supported", DiagnosticCatalog.UnsupportedConstructId);
                return _error;
            }

            var element = MapType(array.ElementType, location);
            var key = element.Name + "[]";
            if (!_arrays.TryGetValue(key, out var arrayType))
            {
                arrayType = new TypeSymbol { Name = key, Kind = BoundKind.Array, ElementType = element };
                _arrays[key] = arrayType;
            }

            return arrayType;
        }

        if (type is IPointerTypeSymbol or IFunctionPointerTypeSymbol or IDynamicTypeSymbol)
        {
            Report(location, $"type '{type.ToDisplayString()}' is not supported", DiagnosticCatalog.UnsupportedConstructId);
            return _error;
        }

        if (type is not INamedTypeSymbol named)
        {
            Report(location, $"type '{type.ToDisplayString()}' is not supported", DiagnosticCatalog.UnsupportedConstructId);
            return _error;
        }

        return MapNamed(named, location);
    }

    private TypeSymbol MapNamed(INamedTypeSymbol named, Location? location)
    {
        var primitive = Primitive(named);
        if (primitive != null)
        {
            return primitive;
        }

        if (named.TypeKind == RoslynKind.Error)
        {
            return _error;
        }

        if (IsWeakReference(named))
        {
            var argument = named.TypeArguments.Length == 1 ? MapType(named.TypeArguments[0], location) : _error;
            var key = "WeakReference<" + argument.Name + ">";
            if (!_weakRefs.TryGetValue(key, out var weak))
            {
                weak = new TypeSymbol { Name = key, Kind = BoundKind.WeakRef, ElementType = argument };
                _weakRefs[key] = weak;
            }

            return weak;
        }

        if (IsDisposable(named))
        {
            return _disposable;
        }

        if (named.SpecialType == SpecialType.System_Object)
        {
            return _object;
        }

        if (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, _compilation.Assembly))
        {
            Report(location, $"library type '{named.ToDisplayString()}' is not supported", DiagnosticCatalog.UnsupportedConstructId);
            return _error;
        }

        if (named.TypeKind == RoslynKind.Enum && named.ContainingType?.IsGenericType == true)
        {
            Report(location, "types nested in generic types are not supported", DiagnosticCatalog.UnsupportedConstructId);
            return _error;
        }

        if (named.IsGenericType)
        {
            var arguments = named.TypeArguments.Select(argument => MapType(argument, location)).ToList();
            if (arguments.Any(argument => argument == _error || argument == _void))
            {
                Report(location, "generic arguments must be valid non-void types", "ARC2013");
                return _error;
            }

            return Instantiate(named.OriginalDefinition, arguments, location);
        }

        if (_named.TryGetValue(named, out var existing))
        {
            return existing;
        }

        var created = CreateShell(named);
        _named.Add(named, created);
        if (named.EnumUnderlyingType is { } underlying)
        {
            created.EnumUnderlyingType = MapType(underlying, location);
        }
        else
        {
            Fill(created, named);
        }

        return created;
    }

    private TypeSymbol Instantiate(INamedTypeSymbol definition, List<TypeSymbol> arguments, Location? location)
    {
        var key = definition.ToDisplayString() + "<" + string.Join(",", arguments.Select(argument => argument.ToString())) + ">";
        if (_generic.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (_depth >= 64 || _count >= 1024)
        {
            Report(location, "generic specialisation limit exceeded (64 nested or 1024 total instantiations)", "ARC2013");
            return _error;
        }

        _depth++;
        _count++;
        var saved = new Dictionary<ITypeSymbol, TypeSymbol>(_substitution, SymbolEqualityComparer.Default);
        try
        {
            var created = CreateShell(definition);
            created.Name = SpecialisedName(created.Name, arguments);
            created.GenericDefinition = definition.Name;
            created.TypeArguments = arguments;
            _generic.Add(key, created);
            _substitution.Clear();
            for (var i = 0; i < definition.TypeParameters.Length && i < arguments.Count; i++)
            {
                _substitution[definition.TypeParameters[i]] = arguments[i];
            }

            Fill(created, definition);
            return created;
        }
        finally
        {
            _substitution.Clear();
            foreach (var pair in saved)
            {
                _substitution[pair.Key] = pair.Value;
            }

            _depth--;
        }
    }

    private TypeSymbol CreateShell(INamedTypeSymbol symbol)
    {
        var kind = symbol.TypeKind switch
        {
            RoslynKind.Struct => BoundKind.Struct,
            RoslynKind.Enum => BoundKind.Enum,
            RoslynKind.Interface => BoundKind.Interface,
            _ => BoundKind.Class
        };
        var location = symbol.Locations.FirstOrDefault(item => item.IsInSource);
        var line = location == null ? 0 : location.GetLineSpan().StartLinePosition.Line + 1;
        var file = location?.SourceTree == null ? "" : Path.GetFileName(location.SourceTree.FilePath);
        return new TypeSymbol
        {
            Name = QualifiedTypeName(symbol),
            Kind = kind,
            IsAbstract = symbol.IsAbstract,
            DeclarationLine = line,
            SourceFile = file
        };
    }

    private static string QualifiedTypeName(INamedTypeSymbol symbol)
    {
        if (symbol.ContainingType != null)
        {
            return QualifiedTypeName(symbol.ContainingType) + "." + symbol.Name;
        }

        return symbol.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "." + symbol.Name
            : symbol.Name;
    }

    private int _fillDepth;
    private readonly List<(TypeSymbol type, List<FieldSymbol> fields, List<MethodSymbol> methods)> _deferredLayouts = [];

    private void Fill(TypeSymbol type, INamedTypeSymbol symbol)
    {
        if (!_filled.Add(type))
        {
            return;
        }

        _fillDepth++;
        try
        {
            FillMembers(type, symbol);
        }
        finally
        {
            _fillDepth--;
            if (_fillDepth == 0)
            {
                DrainLayouts();
            }
        }
    }

    private void FillMembers(TypeSymbol type, INamedTypeSymbol symbol)
    {

        if (symbol.BaseType != null
            && symbol.BaseType.SpecialType != SpecialType.System_Object
            && symbol.BaseType.SpecialType != SpecialType.System_ValueType
            && symbol.BaseType.SpecialType != SpecialType.System_Enum)
        {
            var baseType = MapType(symbol.BaseType, symbol.Locations.FirstOrDefault());
            if (baseType.Kind == BoundKind.Class)
            {
                type.BaseType = baseType;
            }
        }

        foreach (var iface in symbol.Interfaces)
        {
            if (iface.SpecialType == SpecialType.System_Object)
            {
                continue;
            }

            var mapped = MapType(iface, symbol.Locations.FirstOrDefault());
            if (mapped.Kind == BoundKind.Interface && !type.Interfaces.Contains(mapped))
            {
                type.Interfaces.Add(mapped);
            }
        }

        var declaredFields = new List<FieldSymbol>();
        var declaredMethods = new List<MethodSymbol>();
        foreach (var member in symbol.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol field when !field.IsImplicitlyDeclared && !field.IsConst:
                    declaredFields.Add(CreateField(type, field));
                    break;
                case IPropertySymbol property:
                    CreateProperty(type, property, declaredFields, declaredMethods);
                    break;
                case IMethodSymbol method when method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor
                    or MethodKind.ExplicitInterfaceImplementation or MethodKind.UserDefinedOperator or MethodKind.Conversion:
                    CreateMethod(type, method, declaredMethods);
                    break;
                case IMethodSymbol method when method.MethodKind is MethodKind.Destructor:
                    CreateFinaliser(type, method, declaredMethods);
                    break;
                case IMethodSymbol { MethodKind: MethodKind.StaticConstructor } method:
                    var initialiser = new MethodSymbol
                    {
                        Name = "<field_init>",
                        Owner = type,
                        IsStatic = true,
                        ReturnType = _void
                    };
                    type.StaticFieldInitialiser = initialiser;
                    type.HasExplicitStaticConstructor = !method.IsImplicitlyDeclared;
                    Queue(initialiser, method);
                    break;
                case IEventSymbol:
                    Report(member.Locations.FirstOrDefault(), "events are not supported yet", DiagnosticCatalog.UnsupportedConstructId);
                    break;
            }
        }

        if (StructFieldsPending(type, declaredFields))
        {
            _deferredLayouts.Add((type, declaredFields, declaredMethods));
        }
        else
        {
            FinishLayout(type, declaredFields, declaredMethods);
        }
    }

    private static bool StructFieldsPending(TypeSymbol type, List<FieldSymbol> fields) =>
        fields.Exists(field => field.Type.Kind == BoundKind.Struct
            && !field.Type.LayoutDone
            && !ReferenceEquals(field.Type, type));

    private void FinishLayout(TypeSymbol type, List<FieldSymbol> fields, List<MethodSymbol> methods)
    {
        Layout(type, fields, methods);
        MapInterfaces(type);
    }

    private void DrainLayouts()
    {
        var guard = 0;
        while (_deferredLayouts.Count > 0 && guard++ < 64)
        {
            var progressed = false;
            for (var i = 0; i < _deferredLayouts.Count;)
            {
                var pending = _deferredLayouts[i];
                if (StructFieldsPending(pending.type, pending.fields))
                {
                    i++;
                    continue;
                }

                _deferredLayouts.RemoveAt(i);
                FinishLayout(pending.type, pending.fields, pending.methods);
                progressed = true;
            }

            if (!progressed)
            {
                break;
            }
        }

        foreach (var pending in _deferredLayouts)
        {
            FinishLayout(pending.type, pending.fields, pending.methods);
        }

        _deferredLayouts.Clear();
    }

    private static bool HasAccessorBody(IMethodSymbol? accessor)
    {
        if (accessor == null)
        {
            return false;
        }

        foreach (var reference in accessor.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is AccessorDeclarationSyntax { Body: not null }
                or AccessorDeclarationSyntax { ExpressionBody: not null }
                or PropertyDeclarationSyntax { ExpressionBody: not null }
                or IndexerDeclarationSyntax { ExpressionBody: not null }
                or ArrowExpressionClauseSyntax)
            {
                return true;
            }
        }

        return false;
    }

    private FieldSymbol CreateField(TypeSymbol owner, IFieldSymbol field)
    {
        var mapped = MapType(field.Type, field.Locations.FirstOrDefault());
        var created = new FieldSymbol
        {
            Name = field.Name,
            Type = mapped,
            Owner = owner,
            IsStatic = field.IsStatic
        };
        _fields[new MemberKey(field.OriginalDefinition, owner)] = created;
        if (field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer: { } syntax })
        {
            RegisterInitialiser(created, syntax);
        }

        return created;
    }

    private void RegisterInitialiser(FieldSymbol field, EqualsValueClauseSyntax syntax)
    {
        if (!_fieldInitialisers.TryGetValue(field.Owner, out var initialisers))
        {
            initialisers = [];
            _fieldInitialisers.Add(field.Owner, initialisers);
        }

        initialisers.Add((field, syntax));
    }

    private void CreateProperty(TypeSymbol owner, IPropertySymbol property, List<FieldSymbol> fields, List<MethodSymbol> methods)
    {
        if (property.ReturnsByRef || property.ReturnsByRefReadonly)
        {
            ReportOnce(property, "ref-returning properties and indexers are not supported");
            return;
        }

        var location = property.Locations.FirstOrDefault();
        var hasBody = HasAccessorBody(property.GetMethod) || HasAccessorBody(property.SetMethod);
        if (owner.Kind == BoundKind.Interface && hasBody)
        {
            ReportOnce(property, "default interface accessors are not supported");
            return;
        }

        var mapped = MapType(property.Type, location);
        FieldSymbol? backing = null;
        if (!hasBody && !property.IsAbstract && !property.IsIndexer && owner.Kind != BoundKind.Interface)
        {
            backing = new FieldSymbol
            {
                Name = $"<{property.Name}>k__BackingField",
                Type = mapped,
                Owner = owner,
                IsStatic = property.IsStatic
            };
            fields.Add(backing);
            if (property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is PropertyDeclarationSyntax { Initializer: { } initialiser })
            {
                RegisterInitialiser(backing, initialiser);
            }
        }
        var prop = new PropertySymbol
        {
            Name = property.Name,
            Type = mapped,
            Owner = owner,
            IsStatic = property.IsStatic,
            HasGetter = property.GetMethod != null,
            HasSetter = property.SetMethod != null,
            BackingField = backing
        };
        owner.Properties.Add(prop);
        if (backing == null)
        {
            if (property.GetMethod is { } declaredGetter)
            {
                CreateMethod(owner, declaredGetter, methods);
                prop.Getter = _methods.GetValueOrDefault(new MemberKey(declaredGetter.OriginalDefinition, owner));
            }

            if (property.SetMethod is { } declaredSetter)
            {
                CreateMethod(owner, declaredSetter, methods);
                prop.Setter = _methods.GetValueOrDefault(new MemberKey(declaredSetter.OriginalDefinition, owner));
            }

            return;
        }

        if (property.GetMethod != null)
        {
            var getter = Accessor(owner, property.GetMethod, "get_" + property.Name, mapped, []);
            prop.Getter = getter;
            methods.Add(getter);
            _readyBodies.Add(GetterBody(owner, getter, backing));
        }

        if (property.SetMethod != null)
        {
            var value = new ParamSymbol { Name = "value", Type = mapped, Index = 0 };
            var setter = Accessor(owner, property.SetMethod, "set_" + property.Name, _void, [value]);
            prop.Setter = setter;
            methods.Add(setter);
            _readyBodies.Add(SetterBody(owner, setter, backing, value));
        }
    }

    private MethodSymbol Accessor(TypeSymbol owner, IMethodSymbol roslyn, string name, TypeSymbol returnType, List<ParamSymbol> parameters)
    {
        var method = new MethodSymbol
        {
            Name = name,
            Owner = owner,
            ReturnType = returnType,
            IsPublic = roslyn.DeclaredAccessibility == Accessibility.Public,
            IsStatic = roslyn.IsStatic,
            IsVirtual = roslyn.IsVirtual,
            IsOverride = roslyn.IsOverride,
            DeclarationLine = Line(roslyn),
            Parameters = parameters
        };
        _methods[new MemberKey(roslyn.OriginalDefinition, owner)] = method;
        RecordExplicitTargets(method, roslyn);
        return method;
    }

    private static BoundMethodBody GetterBody(TypeSymbol owner, MethodSymbol getter, FieldSymbol field)
    {
        var read = new BoundFieldAccess
        {
            Receiver = getter.IsStatic ? null : new BoundThis { Type = owner },
            Field = field,
            Type = field.Type
        };
        return new BoundMethodBody
        {
            Method = getter,
            Body = new BoundBlock { Statements = [new BoundReturn { Value = read }] }
        };
    }

    private static BoundMethodBody SetterBody(TypeSymbol owner, MethodSymbol setter, FieldSymbol field, ParamSymbol value)
    {
        var assign = new BoundAssign
        {
            Target = new BoundFieldAccess
            {
                Receiver = setter.IsStatic ? null : new BoundThis { Type = owner },
                Field = field,
                Type = field.Type
            },
            Value = new BoundParam { Symbol = value, Type = value.Type },
            Type = field.Type
        };
        return new BoundMethodBody
        {
            Method = setter,
            Body = new BoundBlock { Statements = [new BoundExprStmt { Expression = assign }] }
        };
    }

    private void CreateMethod(TypeSymbol owner, IMethodSymbol method, List<MethodSymbol> methods)
    {
        if (method.IsImplicitlyDeclared && (method.MethodKind != MethodKind.Constructor || owner.Kind != BoundKind.Class)
            && !HasAccessorBody(method))
        {
            return;
        }

        if (method.IsGenericMethod)
        {
            if (method.IsVirtual || method.IsOverride || method.IsAbstract || owner.Kind == BoundKind.Interface)
            {
                ReportOnce(method, "virtual and interface generic methods are not supported yet");
            }

            return;
        }

        if (owner.Kind == BoundKind.Struct && (method.IsVirtual || method.IsOverride))
        {
            ReportOnce(method, "virtual members are not supported on structs");
        }

        var returnType = method.MethodKind == MethodKind.Constructor
            ? _void
            : MapType(method.ReturnType, method.Locations.FirstOrDefault());
        var created = new MethodSymbol
        {
            Name = method.MethodKind switch
            {
                MethodKind.Constructor => "<ctor>",
                MethodKind.Conversion => method.Name + "$" + returnType.Name,
                _ => method.Name
            },
            Owner = owner,
            ReturnType = returnType,
            IsPublic = method.DeclaredAccessibility == Accessibility.Public,
            IsStatic = method.IsStatic,
            IsVirtual = method.IsVirtual || method.IsAbstract,
            IsOverride = method.IsOverride,
            IsAbstract = method.IsAbstract,
            IsConstructor = method.MethodKind == MethodKind.Constructor,
            DeclarationLine = Line(method)
        };
        var index = 0;
        foreach (var parameter in method.Parameters)
        {
            var parameterType = MapType(parameter.Type, parameter.Locations.FirstOrDefault());
            var refKind = MapRef(parameter.RefKind);
            created.Parameters.Add(new ParamSymbol
            {
                Name = parameter.Name,
                Type = parameterType,
                RefKind = refKind,
                Index = index++
            });
        }

        methods.Add(created);
        _methods[new MemberKey(method.OriginalDefinition, owner)] = created;
        RecordExplicitTargets(created, method);
        if (owner.Kind == BoundKind.Interface)
        {
            _selectors[created] = _selectors.Count;
            return;
        }

        if (!method.IsAbstract && (method.IsImplicitlyDeclared || HasBody(method)))
        {
            Queue(created, method);
        }
    }

    private void RecordExplicitTargets(MethodSymbol method, IMethodSymbol declaration)
    {
        foreach (var target in declaration.ExplicitInterfaceImplementations)
        {
            if (MapMethod(target, declaration.Locations.FirstOrDefault()) is { } mapped)
            {
                method.ExplicitInterfaceTargets.Add(mapped);
            }
        }
    }

    // The generated teardown runs the body, so nothing dispatches through it:
    // it is neither virtual nor part of the vtable.
    private void CreateFinaliser(TypeSymbol owner, IMethodSymbol method, List<MethodSymbol> methods)
    {
        var created = new MethodSymbol
        {
            Name = "<finaliser>",
            Owner = owner,
            ReturnType = _void,
            IsFinaliser = true,
            DeclarationLine = Line(method)
        };
        owner.Finaliser = created;
        methods.Add(created);
        _methods[new MemberKey(method.OriginalDefinition, owner)] = created;
        Queue(created, method);
    }

    private void Queue(MethodSymbol method, IMethodSymbol roslyn)
    {
        if (_queued.Add(method))
        {
            _bodies.Enqueue(new PendingBody(method, roslyn));
        }
    }

    private static bool HasBody(IMethodSymbol method)
    {
        if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet)
        {
            return HasAccessorBody(method);
        }

        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            switch (reference.GetSyntax())
            {
                case MethodDeclarationSyntax { Body: not null }:
                case MethodDeclarationSyntax { ExpressionBody: not null }:
                case ConstructorDeclarationSyntax { Body: not null }:
                case ConstructorDeclarationSyntax { ExpressionBody: not null }:
                case OperatorDeclarationSyntax { Body: not null }:
                case OperatorDeclarationSyntax { ExpressionBody: not null }:
                case ConversionOperatorDeclarationSyntax { Body: not null }:
                case ConversionOperatorDeclarationSyntax { ExpressionBody: not null }:
                    return true;
            }
        }

        return false;
    }

    private void Layout(TypeSymbol type, List<FieldSymbol> fields, List<MethodSymbol> methods)
    {
        if (type.LayoutDone)
        {
            return;
        }

        if (type.LayoutInProgress)
        {
            Report(type.DeclarationLine, 0, $"struct '{type.Name}' contains itself", DiagnosticCatalog.UnsupportedConstructId);
            return;
        }

        type.LayoutInProgress = true;
        if (type.BaseType != null)
        {
            if (!type.BaseType.LayoutDone)
            {
                // The base shell was filled, including its layout, before this
                // type listed it. A cycle here is a struct that contains itself.
                Report(type.DeclarationLine, 0, $"struct '{type.Name}' contains itself", DiagnosticCatalog.UnsupportedConstructId);
            }

            type.InstanceFields.AddRange(type.BaseType.InstanceFields);
            type.Vtable.AddRange(type.BaseType.Vtable);
        }

        foreach (var field in fields)
        {
            if (field.Type.Kind == BoundKind.Struct && !field.Type.LayoutDone)
            {
                Report(type.DeclarationLine, 0, $"struct '{type.Name}' contains itself", DiagnosticCatalog.UnsupportedConstructId);
            }
        }

        var offset = type.BaseType != null ? type.BaseType.HeapSize : type.Kind == BoundKind.Struct ? 0 : 24;
        foreach (var field in fields)
        {
            if (field.IsStatic)
            {
                type.StaticFields.Add(field);
                continue;
            }

            field.Index = type.InstanceFields.Count;
            field.ByteOffset = offset;
            offset += field.Type.SlotSize;
            type.InstanceFields.Add(field);
        }

        type.HeapSize = offset;
        foreach (var method in methods)
        {
            type.Methods.Add(method);
            if (method.IsOverride)
            {
                var found = FindVirtual(type.BaseType, method);
                if (found != null)
                {
                    method.VtableSlot = found.VtableSlot;
                    if (method.VtableSlot >= 0 && method.VtableSlot < type.Vtable.Count)
                    {
                        type.Vtable[method.VtableSlot] = method;
                    }
                }
                else
                {
                    method.VtableSlot = type.Vtable.Count;
                    type.Vtable.Add(method);
                }
            }
            else if (method.IsVirtual)
            {
                method.VtableSlot = type.Vtable.Count;
                type.Vtable.Add(method);
            }
        }

        type.LayoutInProgress = false;
        type.LayoutDone = true;
    }

    private void MapInterfaces(TypeSymbol type)
    {
        if (type.BaseType != null)
        {
            foreach (var pair in type.BaseType.InterfaceImpl)
            {
                var implementation = pair.Value;
                type.InterfaceImpl[pair.Key] = implementation.IsVirtual || implementation.IsOverride
                    ? FindVirtual(type, implementation) ?? implementation
                    : implementation;
            }
        }

        foreach (var iface in DeclaredInterfaceClosure(type))
        {
            foreach (var method in iface.Methods)
            {
                if (!_selectors.TryGetValue(method, out var selector))
                {
                    continue;
                }

                var implementation = FindInterfaceImplementation(type, method);
                if (implementation != null)
                {
                    type.InterfaceImpl[selector] = implementation;
                }
                else if (type.Kind == BoundKind.Class && !type.IsAbstract)
                {
                    Report(type.DeclarationLine, 0, $"'{type.Name}' does not implement '{iface.Name}.{method.Name}'", "ARC2001");
                }
            }
        }
    }

    private static IEnumerable<TypeSymbol> DeclaredInterfaceClosure(TypeSymbol type)
    {
        var seen = new HashSet<TypeSymbol>();
        var pending = new Queue<TypeSymbol>(type.Interfaces);
        while (pending.TryDequeue(out var iface))
        {
            if (!seen.Add(iface))
            {
                continue;
            }

            yield return iface;
            foreach (var parent in iface.Interfaces)
            {
                pending.Enqueue(parent);
            }
        }
    }

    private static MethodSymbol? FindVirtual(TypeSymbol? type, MethodSymbol method)
    {
        while (type != null)
        {
            foreach (var candidate in type.Methods)
            {
                if (candidate.Name == method.Name && SameSignature(candidate, method)
                    && (candidate.IsVirtual || candidate.IsOverride))
                {
                    return candidate;
                }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static MethodSymbol? FindInterfaceImplementation(TypeSymbol? type, MethodSymbol method)
    {
        while (type != null)
        {
            var explicitImplementation = type.Methods.Find(candidate => candidate.ExplicitInterfaceTargets.Contains(method));
            if (explicitImplementation != null)
            {
                return explicitImplementation;
            }

            var implicitImplementation = type.Methods.Find(candidate => candidate.IsPublic && !candidate.IsStatic
                && !candidate.IsConstructor && candidate.Name == method.Name && SameSignature(candidate, method));
            if (implicitImplementation != null)
            {
                return implicitImplementation;
            }

            type = type.BaseType;
        }

        return null;
    }

    private MethodSymbol? FindMethod(TypeSymbol? type, string name, int arity)
    {
        while (type != null)
        {
            foreach (var method in type.Methods)
            {
                if (!method.IsConstructor && method.Name == name && method.Parameters.Count == arity)
                {
                    return method;
                }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static bool SameSignature(MethodSymbol left, MethodSymbol right)
    {
        if (left.Parameters.Count != right.Parameters.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Parameters.Count; i++)
        {
            if (left.Parameters[i].Type != right.Parameters[i].Type
                || left.Parameters[i].RefKind != right.Parameters[i].RefKind)
            {
                return false;
            }
        }

        return true;
    }

    private MethodSymbol? MapMethod(IMethodSymbol method, Location? location = null)
    {
        if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet)
        {
            var owner = MapType(method.ContainingType, location);
            if (owner == _error)
            {
                return null;
            }

            return _methods.GetValueOrDefault(new MemberKey(method.OriginalDefinition, owner));
        }

        if (method.ContainingType.TypeKind == RoslynKind.TypeParameter || method.IsGenericMethod)
        {
            var owner = MapType(method.ContainingType, location);
            if (owner == _error)
            {
                return null;
            }

            if (!method.OriginalDefinition.IsGenericMethod)
            {
                return _methods.GetValueOrDefault(new MemberKey(method.OriginalDefinition, owner));
            }

            var arguments = method.TypeArguments.Select(argument => MapType(argument, location)).ToList();
            return Specialise(owner, method.OriginalDefinition, arguments, location);
        }

        var mappedOwner = MapType(method.ContainingType, location);
        if (mappedOwner == _error)
        {
            return null;
        }

        return _methods.GetValueOrDefault(new MemberKey(method.OriginalDefinition, mappedOwner));
    }

    private readonly List<(TypeSymbol Owner, IMethodSymbol Definition, List<TypeSymbol> Arguments, MethodSymbol Method)> _specialisations = [];

    private MethodSymbol? Specialise(TypeSymbol owner, IMethodSymbol definition, List<TypeSymbol> arguments, Location? location)
    {
        var cached = _specialisations.FirstOrDefault(item =>
            ReferenceEquals(item.Owner, owner)
            && SymbolEqualityComparer.Default.Equals(item.Definition, definition)
            && item.Arguments.SequenceEqual(arguments)).Method;
        if (cached != null)
        {
            return cached;
        }

        if (_depth >= 64 || _count >= 1024)
        {
            Report(location, "generic specialisation limit exceeded (64 nested or 1024 total instantiations)", "ARC2013");
            return null;
        }

        var saved = new Dictionary<ITypeSymbol, TypeSymbol>(_substitution, SymbolEqualityComparer.Default);
        _substitution.Clear();
        var typeDefinition = definition.ContainingType.OriginalDefinition;
        for (var i = 0; i < typeDefinition.TypeParameters.Length && i < owner.TypeArguments.Count; i++)
        {
            _substitution[typeDefinition.TypeParameters[i]] = owner.TypeArguments[i];
        }

        for (var i = 0; i < definition.TypeParameters.Length && i < arguments.Count; i++)
        {
            _substitution[definition.TypeParameters[i]] = arguments[i];
        }

        var created = new MethodSymbol
        {
            // A closed generic method must not share an LLVM name with a
            // non-generic overload of the same parameter types.
            Name = SpecialisedName(definition.ToDisplayString(), arguments),
            Owner = owner,
            ReturnType = MapType(definition.ReturnType, location),
            IsStatic = definition.IsStatic,
            DeclarationLine = Line(definition),
            TypeArguments = arguments
        };
        var index = 0;
        foreach (var parameter in definition.Parameters)
        {
            var parameterType = MapType(parameter.Type, location);
            var refKind = MapRef(parameter.RefKind);
            created.Parameters.Add(new ParamSymbol
            {
                Name = parameter.Name,
                Type = parameterType,
                RefKind = refKind,
                Index = index++
            });
        }

        owner.Methods.Add(created);
        _specialisations.Add((owner, definition, arguments, created));
        _substitution.Clear();
        foreach (var pair in saved)
        {
            _substitution[pair.Key] = pair.Value;
        }

        Queue(created, definition);
        return created;
    }

    private FieldSymbol? MapField(IFieldSymbol field, Location? location = null)
    {
        var owner = MapType(field.ContainingType, location);
        if (owner == _error)
        {
            return null;
        }

        return _fields.GetValueOrDefault(new MemberKey(field.OriginalDefinition, owner));
    }

    private TypeSymbol? Primitive(INamedTypeSymbol named) => named.SpecialType switch
    {
        SpecialType.System_Int32 => _int,
        SpecialType.System_Int64 => _long,
        SpecialType.System_Boolean => _bool,
        SpecialType.System_Char => _char,
        SpecialType.System_Single => _float,
        SpecialType.System_Double => _double,
        SpecialType.System_Byte => _byte,
        SpecialType.System_SByte => _sbyte,
        SpecialType.System_Int16 => _short,
        SpecialType.System_UInt16 => _ushort,
        SpecialType.System_UInt32 => _uint,
        SpecialType.System_UInt64 => _ulong,
        SpecialType.System_String => _string,
        SpecialType.System_Void => _void,
        _ => null
    };

    private static bool IsWeakReference(INamedTypeSymbol named) =>
        named.Name == "WeakReference" && named.ContainingNamespace?.ToDisplayString() == "System";

    private static bool IsDisposable(INamedTypeSymbol named) =>
        named.SpecialType == SpecialType.System_IDisposable
        || (named.Name == "IDisposable" && named.ContainingNamespace?.ToDisplayString() == "System");

    private static RefKind MapRef(Microsoft.CodeAnalysis.RefKind kind) => kind switch
    {
        Microsoft.CodeAnalysis.RefKind.Ref => RefKind.Ref,
        Microsoft.CodeAnalysis.RefKind.Out => RefKind.Out,
        Microsoft.CodeAnalysis.RefKind.In => RefKind.In,
        _ => RefKind.None
    };

    private static string SpecialisedName(string name, IEnumerable<TypeSymbol> arguments) =>
        "$g$" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(
            name + "<" + string.Join(",", arguments.Select(argument => argument.ToString())) + ">"));

    private static int Line(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(item => item.IsInSource);
        return location == null ? 0 : location.GetLineSpan().StartLinePosition.Line + 1;
    }

    private MethodSymbol? FindEntry(List<TypeSymbol> types)
    {
        foreach (var type in types)
        {
            foreach (var method in type.Methods)
            {
                if (method.IsStatic && method.Name == "Main" && method.Parameters.Count == 0)
                {
                    return method;
                }
            }
        }

        return null;
    }

    private void ReportOnce(ISymbol symbol, string message)
    {
        if (!_reported.Add(symbol))
        {
            return;
        }

        Report(symbol.Locations.FirstOrDefault(), message, DiagnosticCatalog.UnsupportedConstructId);
    }

    private void Report(Location? location, string message, string id, DiagnosticSeverity severity = DiagnosticSeverity.Error)
    {
        var line = 0;
        var column = 0;
        var file = "";
        if (location != null && location.IsInSource)
        {
            var span = location.GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            column = span.StartLinePosition.Character + 1;
            file = span.Path;
        }

        Report(line, column, message, id, severity, file);
    }

    private void Report(SyntaxNode node, string message, string id, DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        Report(node.GetLocation(), message, id, severity);

    private void Report(int line, int column, string message, string id, DiagnosticSeverity severity = DiagnosticSeverity.Error, string file = "")
    {
        if (!_seen.Add((file, line, column, id)))
        {
            return;
        }

        _diags.Add((file, new Diagnostic
        {
            Line = line,
            Column = column,
            Id = id,
            Message = message,
            Severity = severity
        }));
    }

    private readonly record struct MemberKey(ISymbol Symbol, TypeSymbol Owner)
    {
        public bool Equals(MemberKey other) =>
            ReferenceEquals(Owner, other.Owner) && SymbolEqualityComparer.Default.Equals(Symbol, other.Symbol);

        public override int GetHashCode() =>
            HashCode.Combine(SymbolEqualityComparer.Default.GetHashCode(Symbol), RuntimeHelpers.GetHashCode(Owner));
    }

    private readonly record struct PendingBody(MethodSymbol Method, IMethodSymbol Roslyn);
}
