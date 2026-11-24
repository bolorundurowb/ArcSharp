using ArcSharp.Binding;
using ArcSharp.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using BoundKind = ArcSharp.Binding.TypeKind;

namespace ArcSharp.Frontend;

// A lambda is a closure object that owns its captured variables and receiver,
// plus a body method on that closure. The delegate it creates holds the closure
// as its target and a thunk as its function, so it is invoked, combined and
// compared exactly like a method-group delegate.
sealed partial class RoslynLowering
{
    private readonly List<TypeSymbol> _lambdaTypes = [];
    private Dictionary<ISymbol, CaptureCell> _captureCells = new(SymbolEqualityComparer.Default);
    private Dictionary<ISymbol, (FieldSymbol Cell, FieldSymbol Value)> _lambdaCaptures = new(SymbolEqualityComparer.Default);
    private FieldSymbol? _lambdaReceiver;
    private bool _inLambda;

    private sealed record CaptureCell(LocalSymbol Local, FieldSymbol Value, MethodSymbol Constructor);

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
        var owner = NewLambdaType("capture", variable.Locations.FirstOrDefault());
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

    private TypeSymbol NewLambdaType(string kind, Location? location)
    {
        var type = new TypeSymbol
        {
            Name = "$" + kind + "$" + _lambdaTypes.Count,
            Kind = BoundKind.Class,
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
        if (!HasByValueSignature(delegateType))
        {
            Report(location, "lambdas require a delegate with by-value parameters and a by-value result",
                DiagnosticCatalog.UnsupportedConstructId);
            return ErrorExpr();
        }

        var type = MapType(delegateType, location);
        if (type == _error || type.DelegateInvoke is not { } invoke)
        {
            return ErrorExpr();
        }

        var closure = NewLambdaType("lambda", location);
        closure.LambdaDelegate = type;
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
            Name = "Body",
            Owner = closure,
            ReturnType = invoke.ReturnType,
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

        var closureObject = new BoundNewObject
        {
            Type = closure, Site = Site(creation.Syntax), Ctor = constructor, Arguments = arguments
        };
        return NewDelegateObject(type, method, closureObject, creation.Syntax);

        FieldSymbol AddField(BoundExpr value)
        {
            var field = new FieldSymbol { Name = "capture" + fields.Count, Owner = closure, Type = value.Type };
            fields.Add(field);
            arguments.Add(value);
            return field;
        }
    }

    // Lambda bodies take their parameters by value, so a by-reference parameter
    // or result would need a managed reference to outlive the call.
    private static bool HasByValueSignature(INamedTypeSymbol delegateType) =>
        delegateType.DelegateInvokeMethod is { ReturnsByRef: false, ReturnsByRefReadonly: false } invoke
        && invoke.Parameters.All(parameter => parameter.RefKind == Microsoft.CodeAnalysis.RefKind.None);
}
