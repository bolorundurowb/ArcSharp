using ArcSharp.Binding;
using ArcSharp.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using BoundKind = ArcSharp.Binding.TypeKind;

namespace ArcSharp.Frontend;

sealed partial class RoslynLowering
{
    private readonly Dictionary<INamedTypeSymbol, TypeSymbol> _lambdaDelegates = new(SymbolEqualityComparer.Default);
    private readonly List<TypeSymbol> _lambdaTypes = [];
    private Dictionary<ISymbol, CaptureCell> _captureCells = new(SymbolEqualityComparer.Default);
    private Dictionary<ISymbol, (FieldSymbol Cell, FieldSymbol Value)> _lambdaCaptures = new(SymbolEqualityComparer.Default);
    private FieldSymbol? _lambdaReceiver;
    private bool _inLambda;

    private sealed record CaptureCell(LocalSymbol Local, FieldSymbol Value, MethodSymbol Constructor);

    private TypeSymbol MapLambdaDelegate(INamedTypeSymbol symbol, Location? location)
    {
        if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, _compilation.Assembly)
            || symbol.IsGenericType || symbol.ContainingType?.IsGenericType == true
            || symbol.DelegateInvokeMethod is not { ReturnsByRef: false, ReturnsByRefReadonly: false } invoke
            || invoke.Parameters.Any(p => p.RefKind != Microsoft.CodeAnalysis.RefKind.None))
        {
            Report(location, "lambda delegates require a non-generic source declaration and a by-value signature",
                DiagnosticCatalog.UnsupportedConstructId);
            return _error;
        }

        if (_lambdaDelegates.TryGetValue(symbol, out var type))
        {
            return type;
        }

        type = new TypeSymbol
        {
            Name = "$delegate$" + _lambdaDelegates.Count,
            Kind = BoundKind.Class,
            IsAbstract = true,
            IsLambdaDelegate = true,
            DeclarationLine = location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
            SourceFile = location?.SourceTree?.FilePath ?? ""
        };
        _lambdaDelegates.Add(symbol, type);

        var method = new MethodSymbol
        {
            Name = "Invoke",
            Owner = type,
            IsAbstract = true,
            IsVirtual = true,
            ReturnType = MapType(invoke.ReturnType, location),
            Parameters = LambdaParameters(invoke, location)
        };
        Layout(type, [], [method]);
        return type;
    }

    private List<ParamSymbol> LambdaParameters(IMethodSymbol method, Location? location) =>
        method.Parameters.Select(p => new ParamSymbol
        {
            Name = p.Name,
            Index = p.Ordinal,
            Type = MapType(p.Type, location)
        }).ToList();

    // Discover cells before lowering the enclosing body so its earlier reads
    // and writes address the same storage as reads and writes in the lambda.
    private void PrepareCaptures(SyntaxNode syntax, BoundBlock prologue)
    {
        var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
        foreach (var node in syntax.DescendantNodesAndSelf().OfType<LambdaExpressionSyntax>())
        {
            if (model.GetOperation(node) is not IAnonymousFunctionOperation lambda)
            {
                continue;
            }

            foreach (var operation in lambda.Body.DescendantsAndSelf())
            {
                ISymbol? captured = operation switch
                {
                    ILocalReferenceOperation { Local: { IsConst: false } local } => local,
                    IParameterReferenceOperation parameter => parameter.Parameter,
                    _ => null
                };
                if (captured == null || _captureCells.ContainsKey(captured)
                    || captured.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction })
                {
                    continue;
                }

                switch (captured)
                {
                    case ILocalSymbol local when local.RefKind == Microsoft.CodeAnalysis.RefKind.None
                        && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                            is VariableDeclaratorSyntax
                            {
                                Initializer: not null,
                                Parent.Parent: LocalDeclarationStatementSyntax { UsingKeyword.RawKind: 0 } or ForStatementSyntax
                            }:
                        AddCaptureCell(local, MapType(local.Type, local.Locations.FirstOrDefault()));
                        break;
                    case IParameterSymbol parameter when parameter.RefKind == Microsoft.CodeAnalysis.RefKind.None:
                        var cell = AddCaptureCell(parameter, MapType(parameter.Type, parameter.Locations.FirstOrDefault()));
                        prologue.Statements.Add(InitialiseCapture(cell, ParameterRef(parameter, useCapture: false)));
                        break;
                }
            }
        }
    }

    private CaptureCell AddCaptureCell(ISymbol variable, TypeSymbol valueType)
    {
        var owner = NewLambdaType("capture", null, variable.Locations.FirstOrDefault());
        var value = new FieldSymbol { Owner = owner, Type = valueType, Name = "value" };
        var constructor = AddLambdaConstructor(owner, [value]);
        Layout(owner, [value], [constructor]);
        var local = new LocalSymbol { Name = variable.Name, Type = owner, Id = _localId++ };
        _methodLocals.Add(local);
        var cell = new CaptureCell(local, value, constructor);
        _captureCells.Add(variable, cell);
        return cell;
    }

    private static BoundLocalDecl InitialiseCapture(CaptureCell cell, BoundExpr value) => new()
    {
        Symbol = cell.Local,
        Initializer = new BoundNewObject
        {
            Type = cell.Local.Type, Ctor = cell.Constructor, Arguments = [value]
        }
    };

    private static BoundLocal CellReference(CaptureCell cell) =>
        new() { Symbol = cell.Local, Type = cell.Local.Type };

    private BoundExpr? CaptureReference(ISymbol variable)
    {
        if (_lambdaCaptures.TryGetValue(variable, out var capture))
        {
            return CaptureField(CaptureField(new BoundThis { Type = _currentType }, capture.Cell), capture.Value);
        }

        return !_inLambda && _captureCells.TryGetValue(variable, out var cell)
            ? CaptureField(CellReference(cell), cell.Value)
            : null;
    }

    private static BoundFieldAccess CaptureField(BoundExpr receiver, FieldSymbol field) =>
        new() { Type = field.Type, Field = field, Receiver = receiver };

    private BoundExpr SourceReceiver() => _lambdaReceiver is { } field
        ? CaptureField(new BoundThis { Type = _currentType }, field)
        : new BoundThis { Type = _currentType };

    private TypeSymbol NewLambdaType(string kind, TypeSymbol? baseType, Location? location)
    {
        var type = new TypeSymbol
        {
            Name = "$" + kind + "$" + _lambdaTypes.Count,
            Kind = BoundKind.Class,
            BaseType = baseType,
            DeclarationLine = location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
            SourceFile = location?.SourceTree?.FilePath ?? ""
        };
        _lambdaTypes.Add(type);
        return type;
    }

    private MethodSymbol AddLambdaConstructor(TypeSymbol owner, List<FieldSymbol> fields)
    {
        var constructor = new MethodSymbol { Name = "<ctor>", Owner = owner, IsConstructor = true, ReturnType = _void };
        var body = new BoundBlock();
        foreach (var field in fields)
        {
            var parameter = new ParamSymbol { Name = field.Name, Type = field.Type, Index = constructor.Parameters.Count };
            constructor.Parameters.Add(parameter);
            body.Statements.Add(new BoundExprStmt
            {
                Expression = new BoundAssign
                {
                    Target = CaptureField(new BoundThis { Type = owner }, field),
                    Value = new BoundParam { Symbol = parameter, Type = parameter.Type },
                    Type = field.Type
                }
            });
        }

        _readyBodies.Add(new BoundMethodBody { Method = constructor, Body = body, AllLocals = [] });
        return constructor;
    }

    private BoundExpr LowerLambda(IDelegateCreationOperation creation)
    {
        if (_inLambda || creation.Target is not IAnonymousFunctionOperation { Syntax: LambdaExpressionSyntax } lambda
            || lambda.Symbol.IsAsync || creation.Type is not INamedTypeSymbol delegateType)
        {
            return UnsupportedExpr(creation);
        }

        var location = creation.Syntax.GetLocation();
        var type = MapLambdaDelegate(delegateType, location);
        if (type == _error)
        {
            return ErrorExpr();
        }

        var closure = NewLambdaType("lambda", type, location);
        var fields = new List<FieldSymbol>();
        var arguments = new List<BoundExpr>();
        var captures = new Dictionary<ISymbol, (FieldSymbol Cell, FieldSymbol Value)>(SymbolEqualityComparer.Default);
        FieldSymbol? receiver = null;
        foreach (var operation in lambda.Body.DescendantsAndSelf())
        {
            ISymbol? variable = operation switch
            {
                ILocalReferenceOperation { Local: { IsConst: false } local } => local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                _ => null
            };
            if (variable != null && !SymbolEqualityComparer.Default.Equals(variable.ContainingSymbol, lambda.Symbol)
                && !captures.ContainsKey(variable))
            {
                if (!_captureCells.TryGetValue(variable, out var cell))
                {
                    return UnsupportedExpr(operation);
                }

                var field = AddField(CellReference(cell));
                captures.Add(variable, (field, cell.Value));
            }
            else if (operation is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }
                && receiver == null)
            {
                if (_currentType.Kind == BoundKind.Struct || _currentMethod.IsFinaliser)
                {
                    return UnsupportedExpr(operation);
                }

                receiver = AddField(SourceReceiver());
            }
        }

        var constructor = AddLambdaConstructor(closure, fields);
        var method = new MethodSymbol
        {
            Name = "Invoke",
            Owner = closure,
            IsOverride = true,
            ReturnType = type.Methods[0].ReturnType,
            Parameters = LambdaParameters(lambda.Symbol, location),
            DeclarationLine = closure.DeclarationLine
        };
        Layout(closure, fields, [constructor, method]);

        var previous = (_currentType, _currentMethod, _methodLocals, _localId);
        var outerLocals = _locals.ToArray();
        _locals.Clear();
        _currentType = closure;
        _currentMethod = method;
        _methodLocals = [];
        _localId = 0;
        _lambdaCaptures = captures;
        _lambdaReceiver = receiver;
        _inLambda = true;
        var body = LowerBlock(lambda.Body);
        _readyBodies.Add(new BoundMethodBody { Method = method, Body = body, AllLocals = _methodLocals });
        _inLambda = false;
        _lambdaReceiver = null;
        _lambdaCaptures.Clear();
        (_currentType, _currentMethod, _methodLocals, _localId) = previous;
        _locals.Clear();
        foreach (var pair in outerLocals)
        {
            _locals.Add(pair.Key, pair.Value);
        }

        return new BoundConversion
        {
            Operand = new BoundNewObject { Type = closure, Site = Site(creation.Syntax), Ctor = constructor, Arguments = arguments },
            Type = type
        };

        FieldSymbol AddField(BoundExpr value)
        {
            var field = new FieldSymbol { Name = "capture" + fields.Count, Owner = closure, Type = value.Type };
            fields.Add(field);
            arguments.Add(value);
            return field;
        }
    }

    private BoundExpr LowerLambdaInvocation(IInvocationOperation call)
    {
        var type = MapLambdaDelegate(call.TargetMethod.ContainingType, call.Syntax.GetLocation());
        if (type == _error || call.Instance == null)
        {
            return ErrorExpr();
        }

        var method = type.Methods[0];
        return BindMemberCall(LowerExpr(call.Instance), method, LowerArguments(call.Arguments, method));
    }
}
