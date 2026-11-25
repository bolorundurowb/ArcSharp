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
    private readonly Dictionary<IMethodSymbol, Dictionary<string, LocalFunction>> _localFunctions = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<(SyntaxTree Tree, Microsoft.CodeAnalysis.Text.TextSpan Span), TypeSymbol> _localTypeArguments = [];
    private int _localFunctionCount;
    private int _localFunctionDepth;
    private bool _canCaptureReceiver;

    private sealed record CaptureCell(LocalSymbol Local, FieldSymbol Value, MethodSymbol Constructor);
    private sealed record LocalFunction(MethodSymbol Method, MethodSymbol? Constructor, ClosureEnvironment Environment);

    private BoundStmt LowerLocalFunctionDeclaration(ILocalFunctionOperation function)
    {
        if (!function.Symbol.IsGenericMethod)
        {
            GetLocalFunction(function.Symbol);
        }
        return new BoundBlock();
    }

    private ILocalFunctionOperation? LocalFunctionOperation(IMethodSymbol symbol)
    {
        var syntax = symbol.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        return syntax == null ? null
            : _compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax) as ILocalFunctionOperation;
    }

    private LocalFunction? GetLocalFunction(IMethodSymbol symbol)
    {
        if (LocalFunctionOperation(symbol) is not { Body: { } body } operation)
        {
            return null;
        }

        var location = operation.Syntax.GetLocation();
        if (symbol.IsAsync || symbol.ReturnsByRef || symbol.ReturnsByRefReadonly)
        {
            UnsupportedExpr(operation);
            return null;
        }

        var arguments = symbol.TypeArguments.Select(argument => MapType(argument, location)).ToArray();
        var substitutions = new List<TypeSymbol>();
        for (var scope = operation.Symbol.ContainingSymbol as IMethodSymbol;
             scope?.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction;
             scope = scope.ContainingSymbol as IMethodSymbol)
        {
            foreach (var parameter in scope.TypeParameters)
            {
                substitutions.Add(MapType(parameter, location));
            }
        }

        substitutions.AddRange(arguments);
        // Each root method has its own cache. Within that method, enclosing
        // generic local functions may give the same declaration different cells.
        var specialisation = SpecialisedName("", substitutions);
        if (!_localFunctions.TryGetValue(operation.Symbol, out var functions))
        {
            functions = [];
            _localFunctions.Add(operation.Symbol, functions);
        }

        if (functions.TryGetValue(specialisation, out var existing))
        {
            return existing;
        }

        if (_localFunctionDepth >= 64 || _localFunctionCount >= 1024)
        {
            Report(location, "local function specialisation limit exceeded (64 nested or 1024 total instantiations)", "ARC2013");
            return null;
        }

        var owner = NewLambdaType("local", location);
        owner.IsLocalFunctionEnvironment = true;
        var previous = new TypeSymbol?[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            var parameter = LocalTypeParameterKey(operation.Symbol.TypeParameters[i]);
            previous[i] = _localTypeArguments.GetValueOrDefault(parameter);
            _localTypeArguments[parameter] = arguments[i];
        }
        var environment = CaptureEnvironment(owner, operation.Symbol, body);
        var constructor = environment.Fields.Count == 0 ? null : AddLambdaConstructor(owner, environment.Fields);
        var method = new MethodSymbol
        {
            Name = "Body",
            Owner = owner,
            IsStatic = constructor == null,
            ReturnType = MapType(operation.Symbol.ReturnType, location),
            Parameters = CallableParameters(operation.Symbol, location),
            DeclarationLine = owner.DeclarationLine
        };
        Layout(owner, environment.Fields, constructor == null ? [method] : [constructor, method]);
        var function = new LocalFunction(method, constructor, environment);
        // Publish the signature first so recursive calls reuse the same method.
        functions.Add(specialisation, function);
        _localFunctionCount++;
        _localFunctionDepth++;
        LowerCallableBody(method, operation.Symbol, operation.Syntax, body, environment);
        _localFunctionDepth--;
        for (var i = 0; i < arguments.Length; i++)
        {
            var parameter = LocalTypeParameterKey(operation.Symbol.TypeParameters[i]);
            if (previous[i] is { } type)
            {
                _localTypeArguments[parameter] = type;
            }
            else
            {
                _localTypeArguments.Remove(parameter);
            }
        }

        return function;
    }

    // Independently bound Roslyn operation trees can use distinct symbols
    // for the same local type parameter. Its declaration identifies it.
    private static (SyntaxTree, Microsoft.CodeAnalysis.Text.TextSpan) LocalTypeParameterKey(ITypeParameterSymbol parameter)
    {
        var declaration = parameter.DeclaringSyntaxReferences[0];
        return (declaration.SyntaxTree, declaration.Span);
    }

    private BoundExpr LowerLocalFunctionCall(IInvocationOperation call)
    {
        if (GetLocalFunction(call.TargetMethod) is not { } function)
        {
            return ErrorExpr();
        }

        return BindMemberCall(LocalFunctionTarget(function, call.Syntax), function.Method,
            LowerArguments(call.Arguments, function.Method));
    }

    private BoundExpr? LocalFunctionTarget(LocalFunction function, SyntaxNode syntax)
    {
        if (function.Constructor == null)
        {
            return null;
        }

        if (_currentMethod == function.Method)
        {
            return new BoundThis { Type = _currentType };
        }

        var arguments = new BoundExpr[function.Environment.Fields.Count];
        foreach (var (variable, (field, _)) in function.Environment.Captures)
        {
            arguments[function.Environment.Fields.IndexOf(field)] = CaptureCellReference(variable).Reference ?? ErrorExpr();
        }

        if (function.Environment.Receiver is { } receiver)
        {
            arguments[function.Environment.Fields.IndexOf(receiver)] = SourceReceiver();
        }

        return NewEnvironment(function.Constructor, [.. arguments], syntax);
    }

    // A callable also needs the outer variables used by any local function
    // it calls or converts to a delegate, including along recursive paths.
    private IEnumerable<IOperation> CaptureOperations(IBlockOperation body)
    {
        var pending = new Stack<IBlockOperation>();
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        pending.Push(body);
        while (pending.TryPop(out var block))
        {
            foreach (var operation in block.DescendantsAndSelf())
            {
                yield return operation;
                var target = operation switch
                {
                    IInvocationOperation invocation => invocation.TargetMethod,
                    IMethodReferenceOperation reference => reference.Method,
                    _ => null
                };
                if (target?.MethodKind == MethodKind.LocalFunction && seen.Add(target.OriginalDefinition)
                    && LocalFunctionOperation(target)?.Body is { } dependency)
                {
                    pending.Push(dependency);
                }
            }
        }
    }

    private List<ParamSymbol> CallableParameters(IMethodSymbol method, Location? location) =>
        method.Parameters.Select(p => new ParamSymbol
        {
            Name = p.Name,
            Index = p.Ordinal,
            Type = MapType(p.Type, location),
            RefKind = MapRef(p.RefKind)
        }).ToList();

    // Discover cells before lowering the enclosing body so its earlier reads
    // and writes address the same storage as reads and writes in the lambda.
    private void PrepareCaptures(IMethodSymbol owner, SyntaxNode syntax, BoundBlock prologue)
    {
        var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
        foreach (var node in syntax.DescendantNodes().Where(node => node is LambdaExpressionSyntax or LocalFunctionStatementSyntax))
        {
            if (model.GetOperation(node) is not { } callable)
            {
                continue;
            }

            foreach (var operation in callable.Descendants())
            {
                var captured = CaptureVariable(operation);
                if (captured == null || _captureCells.ContainsKey(captured)
                    || !SymbolEqualityComparer.Default.Equals(captured.ContainingSymbol, owner))
                {
                    continue;
                }

                switch (captured)
                {
                    case ILocalSymbol local when local.RefKind == Microsoft.CodeAnalysis.RefKind.None
                        && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is
                            (VariableDeclaratorSyntax
                            {
                                Parent.Parent: LocalDeclarationStatementSyntax { UsingKeyword.RawKind: 0 } or ForStatementSyntax
                            } or ForEachStatementSyntax):
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
            Type = cell.Local.Type, Ctor = cell.Constructor, Arguments = [value], CapturesArguments = true
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

        return _captureCells.TryGetValue(variable, out var cell)
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
        if (creation.Target is not IAnonymousFunctionOperation { Syntax: LambdaExpressionSyntax } lambda
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
        closure.LambdaDelegateTypes.Add(type);
        var environment = CaptureEnvironment(closure, lambda.Symbol, lambda.Body);
        var constructor = AddLambdaConstructor(closure, environment.Fields);
        var method = new MethodSymbol
        {
            Name = "Body",
            Owner = closure,
            ReturnType = invoke.ReturnType,
            Parameters = CallableParameters(lambda.Symbol, location),
            DeclarationLine = closure.DeclarationLine
        };
        Layout(closure, environment.Fields, [constructor, method]);
        LowerCallableBody(method, lambda.Symbol, lambda.Syntax, lambda.Body, environment);
        return NewDelegateObject(type, method, NewEnvironment(constructor, environment.Arguments, creation.Syntax), creation.Syntax);
    }

    private sealed record ClosureEnvironment(
        List<FieldSymbol> Fields, List<BoundExpr> Arguments,
        Dictionary<ISymbol, (FieldSymbol Cell, FieldSymbol Value)> Captures, FieldSymbol? Receiver);

    private static ISymbol? CaptureVariable(IOperation operation) => operation switch
    {
        ILocalReferenceOperation { Local: { IsConst: false } local } => local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        _ => null
    };

    private static bool DeclaredOutside(ISymbol variable, IMethodSymbol callable)
    {
        for (var scope = callable.ContainingSymbol as IMethodSymbol;
             scope != null; scope = scope.ContainingSymbol as IMethodSymbol)
        {
            if (SymbolEqualityComparer.Default.Equals(scope, variable.ContainingSymbol))
            {
                return true;
            }
        }

        return false;
    }

    private ClosureEnvironment CaptureEnvironment(TypeSymbol closure, IMethodSymbol callable, IBlockOperation body)
    {
        var fields = new List<FieldSymbol>();
        var arguments = new List<BoundExpr>();
        var captures = new Dictionary<ISymbol, (FieldSymbol Cell, FieldSymbol Value)>(SymbolEqualityComparer.Default);
        FieldSymbol? receiver = null;
        foreach (var operation in CaptureOperations(body))
        {
            var variable = CaptureVariable(operation);
            if (variable != null && DeclaredOutside(variable, callable) && !captures.ContainsKey(variable))
            {
                var (reference, value) = CaptureCellReference(variable);
                if (reference == null || value == null)
                {
                    UnsupportedExpr(operation);
                    continue;
                }

                captures.Add(variable, (AddField(reference), value));
            }
            else if (operation is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }
                && receiver == null)
            {
                if (!_canCaptureReceiver)
                {
                    UnsupportedExpr(operation);
                    continue;
                }

                receiver = AddField(SourceReceiver());
            }
        }

        return new ClosureEnvironment(fields, arguments, captures, receiver);

        FieldSymbol AddField(BoundExpr value)
        {
            var field = new FieldSymbol { Name = "capture" + fields.Count, Owner = closure, Type = value.Type };
            fields.Add(field);
            arguments.Add(value);
            return field;
        }
    }

    private (BoundExpr? Reference, FieldSymbol? Value) CaptureCellReference(ISymbol variable)
    {
        if (_captureCells.TryGetValue(variable, out var cell))
        {
            return (CellReference(cell), cell.Value);
        }

        return _lambdaCaptures.TryGetValue(variable, out var capture)
            ? (CaptureField(new BoundThis { Type = _currentType }, capture.Cell), capture.Value)
            : (null, null);
    }

    private void LowerCallableBody(MethodSymbol method, IMethodSymbol symbol, SyntaxNode syntax,
        IBlockOperation operation, ClosureEnvironment environment)
    {
        var previous = (_currentType, _currentMethod, _methodLocals, _localId, _captureCells, _lambdaCaptures, _lambdaReceiver, _canCaptureReceiver);
        var outerLocals = _locals.ToArray();
        _locals.Clear();
        _currentType = method.Owner;
        _currentMethod = method;
        _methodLocals = [];
        _localId = 0;
        _captureCells = new(SymbolEqualityComparer.Default);
        _lambdaCaptures = environment.Captures;
        _lambdaReceiver = environment.Receiver;
        _canCaptureReceiver = environment.Receiver != null;
        var body = new BoundBlock();
        PrepareCaptures(symbol, syntax, body);
        AppendBlock(body, operation);
        _readyBodies.Add(new BoundMethodBody { Method = method, Body = body, AllLocals = _methodLocals });
        (_currentType, _currentMethod, _methodLocals, _localId, _captureCells, _lambdaCaptures, _lambdaReceiver, _canCaptureReceiver) = previous;
        _locals.Clear();
        foreach (var pair in outerLocals)
        {
            _locals.Add(pair.Key, pair.Value);
        }
    }

    private BoundNewObject NewEnvironment(MethodSymbol constructor, List<BoundExpr> arguments, SyntaxNode syntax) =>
        new()
        {
            Type = constructor.Owner, Site = Site(syntax), Ctor = constructor, Arguments = arguments,
            CapturesArguments = true
        };

    // Lambda bodies take their parameters by value, so a by-reference parameter
    // or result would need a managed reference to outlive the call.
    private static bool HasByValueSignature(INamedTypeSymbol delegateType) =>
        delegateType.DelegateInvokeMethod is { ReturnsByRef: false, ReturnsByRefReadonly: false } invoke
        && invoke.Parameters.All(parameter => parameter.RefKind == Microsoft.CodeAnalysis.RefKind.None);
}
