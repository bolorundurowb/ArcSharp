using ArcSharp.Binding;
using ArcSharp.Diagnostics;
using ArcSharp.Lexing;
using ArcSharp.Syntax;
using BoundKind = ArcSharp.Binding.TypeKind;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace ArcSharp.Frontend;

// An iterator method becomes a class derived from System.Runtime.CompilerServices
// .IteratorBase<T>. The method itself only builds an instance. The body is
// lowered once, like any other body, into the Step method of that class, and
// then made resumable:
//
//   - Step starts with a jump to the label after the yield it was suspended at.
//   - A yield stores the value, saves every local into a field, records its
//     state number and returns true without running any cleanup.
//   - The label after it restores the locals. If the iterator is being
//     disposed, it returns false instead, so the finally blocks and using
//     cleanups around the yield run on that return.
//
// Saving every local, instead of moving them into fields, keeps the locals
// ordinary locals of Step, so using, foreach, patterns and closures lower
// exactly as they do in any other method.
sealed partial class RoslynLowering
{
    private IteratorContext? _iterator;

    private sealed class IteratorContext
    {
        public required MethodSymbol Method;
        public required MethodSymbol Step;
        public required TypeSymbol Owner;
        public required FieldSymbol State;
        public required FieldSymbol Value;
        public required FieldSymbol Disposing;
        public Dictionary<ParamSymbol, LocalSymbol> Parameters { get; } = [];
        public List<BoundBlock> Saves { get; } = [];
        public List<BoundBlock> Restores { get; } = [];
        public int Yields;
    }

    private static bool ContainsYield(SyntaxNode syntax) =>
        syntax.DescendantNodes(node => node is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax
            or LocalFunctionStatementSyntax)).OfType<YieldStatementSyntax>().Any();

    private IBlockOperation? IteratorBody(SyntaxNode syntax)
    {
        var body = syntax switch
        {
            MethodDeclarationSyntax method => method.Body,
            AccessorDeclarationSyntax accessor => accessor.Body,
            _ => null
        };
        return body == null ? null
            : _compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(body) as IBlockOperation;
    }

    // The element type is object for the non-generic interfaces.
    private ITypeSymbol? IteratorElementType(ITypeSymbol returnType) =>
        returnType is INamedTypeSymbol named ? named.OriginalDefinition.SpecialType switch
        {
            SpecialType.System_Collections_Generic_IEnumerable_T
                or SpecialType.System_Collections_Generic_IEnumerator_T => named.TypeArguments[0],
            SpecialType.System_Collections_IEnumerable or SpecialType.System_Collections_IEnumerator =>
                _compilation.GetSpecialType(SpecialType.System_Object),
            _ => null
        } : null;

    // 'factory' becomes the body of the method as callers see it.
    private void LowerIterator(MethodSymbol method, IMethodSymbol roslyn, SyntaxNode syntax,
        IBlockOperation operation, BoundBlock factory)
    {
        var location = syntax.GetLocation();
        if (method.Owner.Kind == BoundKind.Struct)
        {
            Report(syntax, "an iterator in a struct is not supported", DiagnosticCatalog.UnsupportedConstructId);
            return;
        }

        var elementSymbol = IteratorElementType(roslyn.ReturnType);
        var baseDefinition = _compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.IteratorBase`1");
        if (elementSymbol == null || baseDefinition == null)
        {
            Report(syntax, "an iterator must return IEnumerable, IEnumerator or their generic forms", "ARC2001");
            return;
        }

        var baseType = MapType(baseDefinition.Construct(elementSymbol), location);
        if (baseType == _error)
        {
            return;
        }

        var element = baseType.TypeArguments[0];
        var owner = NewLambdaType("iterator", location);
        owner.BaseType = baseType;

        var inputs = new List<FieldSymbol>();
        FieldSymbol? receiver = null;
        if (!method.IsStatic)
        {
            receiver = new FieldSymbol { Name = "this", Owner = owner, Type = method.Owner };
            inputs.Add(receiver);
        }

        foreach (var parameter in method.Parameters)
        {
            inputs.Add(new FieldSymbol { Name = "arg_" + parameter.Name, Owner = owner, Type = parameter.Type });
        }

        var constructor = AddLambdaConstructor(owner, inputs);
        var step = new MethodSymbol
        {
            Name = "Step", Owner = owner, ReturnType = _bool, IsPublic = true, IsOverride = true,
            DeclarationLine = owner.DeclarationLine
        };
        var clone = new MethodSymbol
        {
            Name = "Clone", Owner = owner, ReturnType = baseType, IsPublic = true, IsOverride = true,
            DeclarationLine = owner.DeclarationLine
        };

        var arguments = new List<BoundExpr>();
        if (receiver != null)
        {
            arguments.Add(new BoundThis { Type = method.Owner });
        }

        arguments.AddRange(method.Parameters.Select(parameter =>
            (BoundExpr)new BoundParam { Symbol = parameter, Type = parameter.Type }));
        factory.Statements.Add(new BoundReturn
        {
            Value = new BoundConversion
            {
                Operand = NewEnvironment(constructor, arguments, syntax), Type = method.ReturnType
            }
        });

        var context = new IteratorContext
        {
            Method = method, Step = step, Owner = owner,
            State = BaseField(baseType, "State"), Value = BaseField(baseType, "Value"),
            Disposing = BaseField(baseType, "Disposing")
        };
        var stepBody = LowerStep(context, roslyn, syntax, operation, inputs, receiver, element, out var stepLocals,
            out var spills);

        var fields = new List<FieldSymbol>(inputs);
        fields.AddRange(spills.Select(spill => spill.Field));
        Layout(owner, fields, [constructor, step, clone]);
        MapInterfaces(owner);

        _readyBodies.Add(new BoundMethodBody
        {
            Method = step, Body = stepBody, AllLocals = stepLocals, SkipDefiniteAssignment = true
        });
        _readyBodies.Add(new BoundMethodBody
        {
            Method = clone,
            Body = new BoundBlock
            {
                Statements = [new BoundReturn
                {
                    Value = new BoundConversion
                    {
                        Operand = NewEnvironment(constructor,
                            inputs.Select(field => (BoundExpr)CaptureField(This(owner), field)).ToList(), syntax),
                        Type = baseType
                    }
                }]
            }
        });
    }

    private static FieldSymbol BaseField(TypeSymbol baseType, string name) =>
        baseType.InstanceFields.First(field => field.Name == name);

    private static BoundThis This(TypeSymbol type) => new() { Type = type };

    private BoundBlock LowerStep(IteratorContext context, IMethodSymbol roslyn, SyntaxNode syntax,
        IBlockOperation operation, List<FieldSymbol> inputs, FieldSymbol? receiver, TypeSymbol element,
        out List<LocalSymbol> locals, out List<(LocalSymbol Local, FieldSymbol Field)> spills)
    {
        var previous = (_currentType, _currentMethod, _methodLocals, _localId, _captureCells, _lambdaCaptures,
            _lambdaReceiver, _canCaptureReceiver, _iterator);
        var outerLocals = _locals.ToArray();
        _locals.Clear();
        _currentType = context.Owner;
        _currentMethod = context.Step;
        _methodLocals = [];
        _localId = 0;
        _captureCells = new(SymbolEqualityComparer.Default);
        _lambdaCaptures = new(SymbolEqualityComparer.Default);
        _lambdaReceiver = receiver;
        _canCaptureReceiver = receiver != null;
        _iterator = context;

        var start = new BoundBlock();
        var firstParameter = receiver == null ? 0 : 1;
        for (var i = 0; i < context.Method.Parameters.Count; i++)
        {
            var parameter = context.Method.Parameters[i];
            var local = new LocalSymbol { Name = parameter.Name, Type = parameter.Type, Id = _localId++ };
            _methodLocals.Add(local);
            context.Parameters[parameter] = local;
            start.Statements.Add(Store(new BoundLocal { Symbol = local, Type = local.Type },
                CaptureField(This(context.Owner), inputs[firstParameter + i])));
        }

        PrepareCaptures(roslyn, syntax, start);
        AppendBlock(start, operation);
        start.Statements.Add(Store(CaptureField(This(context.Owner), context.Value), DefaultValue(element)));
        start.Statements.Add(new BoundReturn { Value = BoolLiteral(false) });

        locals = _methodLocals;
        spills = _methodLocals.Where(local => local.Type.Kind is not (BoundKind.Error or BoundKind.Void))
            .Select(local => (local, new FieldSymbol { Name = "local" + local.Id, Owner = context.Owner, Type = local.Type }))
            .ToList();
        foreach (var save in context.Saves)
        {
            save.Statements.AddRange(spills.Select(spill => Store(
                CaptureField(This(context.Owner), spill.Field), new BoundLocal { Symbol = spill.Local, Type = spill.Local.Type })));
        }

        foreach (var restore in context.Restores)
        {
            restore.Statements.AddRange(spills.Select(spill => Store(
                new BoundLocal { Symbol = spill.Local, Type = spill.Local.Type }, CaptureField(This(context.Owner), spill.Field))));
        }

        var body = new BoundBlock();
        for (var site = 1; site <= context.Yields; site++)
        {
            body.Statements.Add(new BoundIf
            {
                Condition = StateIs(context, site, equal: true),
                Then = new BoundGoto { Name = ResumeLabel(site) }
            });
        }

        // Anything but the start state means the iterator has finished or is running.
        body.Statements.Add(new BoundIf
        {
            Condition = StateIs(context, 0, equal: false),
            Then = new BoundReturn { Value = BoolLiteral(false) }
        });
        body.Statements.Add(Store(CaptureField(This(context.Owner), context.State), IntLiteral(-1)));
        body.Statements.Add(start);

        (_currentType, _currentMethod, _methodLocals, _localId, _captureCells, _lambdaCaptures,
            _lambdaReceiver, _canCaptureReceiver, _iterator) = previous;
        _locals.Clear();
        foreach (var pair in outerLocals)
        {
            _locals.Add(pair.Key, pair.Value);
        }

        return body;
    }

    private BoundStmt LowerYieldReturn(IReturnOperation operation)
    {
        if (_iterator is not { } iterator || _currentMethod != iterator.Step || operation.ReturnedValue == null)
        {
            return UnsupportedYield(operation);
        }

        var site = ++iterator.Yields;
        var save = new BoundBlock();
        var restore = new BoundBlock();
        iterator.Saves.Add(save);
        iterator.Restores.Add(restore);
        BoundFieldAccess Field(FieldSymbol field) => CaptureField(This(iterator.Owner), field);
        return new BoundBlock
        {
            Statements = [
                Store(Field(iterator.Value), LowerExpr(operation.ReturnedValue)),
                save,
                Store(Field(iterator.State), IntLiteral(site)),
                new BoundReturn { Value = BoolLiteral(true), SkipCleanups = true },
                new BoundLabel { Name = ResumeLabel(site) },
                Store(Field(iterator.State), IntLiteral(-1)),
                restore,
                new BoundIf { Condition = Field(iterator.Disposing), Then = new BoundReturn { Value = BoolLiteral(false) } }
            ]
        };
    }

    private BoundStmt LowerYieldBreak(IReturnOperation operation)
    {
        if (_iterator is not { } iterator || _currentMethod != iterator.Step)
        {
            return UnsupportedYield(operation);
        }

        return new BoundBlock
        {
            Statements = [
                Store(CaptureField(This(iterator.Owner), iterator.Value), DefaultValue(iterator.Value.Type)),
                new BoundReturn { Value = BoolLiteral(false) }
            ]
        };
    }

    private BoundStmt UnsupportedYield(IOperation operation)
    {
        Report(operation.Syntax, "'yield' is supported only in the body of a method or a property getter",
            DiagnosticCatalog.UnsupportedConstructId);
        return new BoundBlock();
    }

    private static string ResumeLabel(int site) => "resume" + site;

    private BoundExpr StateIs(IteratorContext context, int state, bool equal) => new BoundBinary
    {
        Op = equal ? TokenKind.EqualsEquals : TokenKind.BangEquals,
        Kind = BinKind.IntCmp,
        Left = CaptureField(This(context.Owner), context.State),
        Right = IntLiteral(state),
        Type = _bool
    };

    private BoundExprStmt Store(BoundExpr target, BoundExpr value) =>
        new() { Expression = new BoundAssign { Target = target, Value = value, Type = target.Type } };

    private BoundLiteral IntLiteral(int value) => new() { Type = _int, LitKind = LiteralKind.Int, IntValue = value };

    private BoundLiteral BoolLiteral(bool value) => new() { Type = _bool, LitKind = LiteralKind.Bool, BoolValue = value };
}
