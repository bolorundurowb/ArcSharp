using ArcSharp.Binding;
using ArcSharp.Diagnostics;
using ArcSharp.Lexing;
using ArcSharp.Syntax;
using BinaryOperatorKind = Microsoft.CodeAnalysis.Operations.BinaryOperatorKind;
using BoundKind = ArcSharp.Binding.TypeKind;
using RefKind = ArcSharp.Syntax.RefKind;
using RoslynKind = Microsoft.CodeAnalysis.TypeKind;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace ArcSharp.Frontend;

// Delegates. A delegate type is a class with three fields: the target, the
// function, and the next node of the invocation list. Combining builds a new
// list in the runtime. Invoke is generated here as a loop over the list.
//
// Every function a delegate can hold has one shape: it takes the target as an
// object first, then the delegate's parameters. A method group becomes a
// generated thunk of that shape, so an instance method, a static method, a
// virtual method and an interface method are all called the same way.
sealed partial class RoslynLowering
{
    private readonly Dictionary<(MethodSymbol Method, TypeSymbol Delegate), MethodSymbol> _thunks = [];
    private readonly Stack<Func<BoundExpr>> _accessReceivers = new();

    private void FillDelegate(TypeSymbol type, INamedTypeSymbol symbol)
    {
        type.IsDelegate = true;
        var location = symbol.Locations.FirstOrDefault();
        if (symbol.DelegateInvokeMethod is not { } signature)
        {
            Report(location, $"delegate '{symbol.Name}' has no Invoke method", "ARC2001");
            return;
        }

        if (signature.ReturnsByRef || signature.ReturnsByRefReadonly)
        {
            Report(location, "ref-returning delegates are not supported", DiagnosticCatalog.UnsupportedConstructId);
            return;
        }

        var target = new FieldSymbol { Name = "$target", Type = _object, Owner = type };
        var function = new FieldSymbol { Name = "$function", Type = _long, Owner = type };
        var next = new FieldSymbol { Name = "$next", Type = type, Owner = type };
        type.DelegateTarget = target;
        type.DelegateFunction = function;
        type.DelegateNext = next;

        var invoke = new MethodSymbol
        {
            Name = "Invoke",
            Owner = type,
            IsPublic = true,
            ReturnType = MapType(signature.ReturnType, location),
            DeclarationLine = Line(symbol)
        };
        foreach (var parameter in signature.Parameters)
        {
            invoke.Parameters.Add(new ParamSymbol
            {
                Name = parameter.Name,
                Type = MapType(parameter.Type, parameter.Locations.FirstOrDefault()),
                RefKind = MapRef(parameter.RefKind),
                Index = invoke.Parameters.Count
            });
        }

        type.DelegateInvoke = invoke;
        _methods[new MemberKey(signature.OriginalDefinition, type)] = invoke;

        var targetParameter = new ParamSymbol { Name = "target", Type = _object, Index = 0 };
        var functionParameter = new ParamSymbol { Name = "function", Type = _long, Index = 1 };
        var constructor = new MethodSymbol
        {
            Name = "<ctor>",
            Owner = type,
            IsConstructor = true,
            ReturnType = _void,
            Parameters = [targetParameter, functionParameter]
        };

        FinishLayout(type, [target, function, next], [constructor, invoke]);
        _readyBodies.Add(new BoundMethodBody
        {
            Method = constructor,
            Body = new BoundBlock
            {
                Statements = [StoreField(type, target, targetParameter), StoreField(type, function, functionParameter)]
            }
        });
        _readyBodies.Add(DelegateInvokeBody(type, invoke));
    }

    private static BoundStmt StoreField(TypeSymbol owner, FieldSymbol field, ParamSymbol parameter) =>
        new BoundExprStmt
        {
            Expression = new BoundAssign
            {
                Target = new BoundFieldAccess { Receiver = new BoundThis { Type = owner }, Field = field, Type = field.Type },
                Value = new BoundParam { Symbol = parameter, Type = parameter.Type },
                Type = field.Type
            }
        };

    // Invoke runs every node in order and returns the value of the last one:
    //   node = this; while (node != null) { result = node(args); node = node.next; }
    private BoundMethodBody DelegateInvokeBody(TypeSymbol type, MethodSymbol invoke)
    {
        var node = new LocalSymbol { Name = "#node", Type = type, Id = 0 };
        var locals = new List<LocalSymbol> { node };
        LocalSymbol? result = null;
        if (invoke.ReturnType != _void)
        {
            result = new LocalSymbol { Name = "#result", Type = invoke.ReturnType, Id = 1 };
            locals.Add(result);
        }

        BoundLocal Read(LocalSymbol local) => new() { Symbol = local, Type = local.Type };
        var call = new BoundCall
        {
            Receiver = Read(node),
            Method = invoke,
            Arguments = invoke.Parameters.Select(ParameterRead).ToList(),
            Type = invoke.ReturnType,
            ThroughDelegate = true
        };
        BoundExpr invocation = result == null
            ? call
            : new BoundAssign { Target = Read(result), Value = call, Type = result.Type };
        var advance = new BoundAssign
        {
            Target = Read(node),
            Value = new BoundFieldAccess { Receiver = Read(node), Field = type.DelegateNext!, Type = type },
            Type = type
        };

        var statements = new List<BoundStmt> { new BoundLocalDecl { Symbol = node, Initializer = new BoundThis { Type = type } } };
        if (result != null)
        {
            statements.Add(new BoundLocalDecl { Symbol = result, Initializer = DefaultValue(result.Type) });
        }

        statements.Add(new BoundWhile
        {
            ConditionIsTop = true,
            Condition = NotNull(Read(node)),
            Body = new BoundBlock
            {
                Statements = [new BoundExprStmt { Expression = invocation }, new BoundExprStmt { Expression = advance }]
            }
        });
        if (result != null)
        {
            statements.Add(new BoundReturn { Value = Read(result) });
        }

        return new BoundMethodBody { Method = invoke, Body = new BoundBlock { Statements = statements }, AllLocals = locals };
    }

    private BoundExpr NotNull(BoundExpr value) => new BoundBinary
    {
        Op = TokenKind.BangEquals,
        Kind = BinKind.RefEq,
        Left = value,
        Right = new BoundLiteral { Type = _null, LitKind = LiteralKind.Null },
        Type = _bool
    };

    private static BoundExpr ParameterRead(ParamSymbol parameter)
    {
        var read = new BoundParam { Symbol = parameter, Type = parameter.Type, ByRef = parameter.RefKind != RefKind.None };
        return read.ByRef
            ? new BoundByRefArg { Lvalue = read, Kind = parameter.RefKind, Type = parameter.Type }
            : read;
    }

    private BoundExpr LowerDelegateCreation(IDelegateCreationOperation creation)
    {
        if (creation.Target is IAnonymousFunctionOperation)
        {
            return LowerLambda(creation);
        }

        var type = MapType(creation.Type, creation.Syntax.GetLocation());
        if (type == _error)
        {
            return ErrorExpr();
        }

        if (creation.Target is IMethodReferenceOperation reference)
        {
            return NewDelegate(type, reference, creation.Syntax);
        }

        return UnsupportedExpr(creation);
    }

    private BoundExpr NewDelegate(TypeSymbol type, IMethodReferenceOperation reference, SyntaxNode syntax)
    {
        if (!type.IsDelegate || type.DelegateInvoke == null)
        {
            return ErrorExpr();
        }

        if (reference.Method.MethodKind == MethodKind.LocalFunction)
        {
            if (GetLocalFunction(reference.Method) is not { } function)
            {
                return ErrorExpr();
            }

            function.Method.Owner.LambdaDelegateTypes.Add(type);
            var environment = LocalFunctionTarget(function, syntax);
            return NewDelegateObject(type, function.Method,
                environment ?? new BoundLiteral { Type = _object, LitKind = LiteralKind.Null }, syntax);
        }

        var method = MapMethod(reference.Method, syntax.GetLocation());
        if (method == null)
        {
            Report(syntax, $"'{reference.Method.ContainingType.Name}' has no method '{reference.Method.Name}'", "ARC2002");
            return ErrorExpr();
        }

        if (method.IsConstructor || (method.IsStatic && reference.Instance != null))
        {
            Report(syntax, "a delegate to an extension method is not supported", DiagnosticCatalog.UnsupportedConstructId);
            return ErrorExpr();
        }

        if (!method.IsStatic && method.Owner.Kind == BoundKind.Struct)
        {
            Report(syntax, "a delegate to an instance method of a struct is not supported: the struct would have to be boxed",
                DiagnosticCatalog.UnsupportedConstructId);
            return ErrorExpr();
        }

        BoundExpr target;
        if (method.IsStatic)
        {
            target = new BoundLiteral { Type = _object, LitKind = LiteralKind.Null };
        }
        else
        {
            target = reference.Instance == null ? SourceReceiver() : LowerExpr(reference.Instance);
        }

        return NewDelegateObject(type, method, target, syntax);
    }

    // A delegate object that holds 'target' and calls 'method' through its thunk.
    private BoundExpr NewDelegateObject(TypeSymbol type, MethodSymbol method, BoundExpr target, SyntaxNode syntax) =>
        new BoundNewObject
        {
            Type = type,
            Ctor = type.Methods.Find(candidate => candidate.IsConstructor),
            Arguments = [target, new BoundFunctionAddress { Method = DelegateThunk(type, method), Type = _long }],
            Site = Site(syntax)
        };

    // The function a delegate of 'type' holds for 'method'. One thunk serves
    // every conversion of the same method to the same delegate type, so two
    // such delegates compare equal and removal finds the one that was added.
    private MethodSymbol DelegateThunk(TypeSymbol type, MethodSymbol method)
    {
        if (_thunks.TryGetValue((method, type), out var existing))
        {
            return existing;
        }

        var invoke = type.DelegateInvoke!;
        var thunk = new MethodSymbol
        {
            Name = "$thunk" + _thunks.Count,
            Owner = type,
            IsStatic = true,
            ReturnType = invoke.ReturnType,
            DeclarationLine = type.DeclarationLine
        };
        var targetParameter = new ParamSymbol { Name = "target", Type = _object, Index = 0 };
        thunk.Parameters.Add(targetParameter);
        foreach (var parameter in invoke.Parameters)
        {
            thunk.Parameters.Add(new ParamSymbol
            {
                Name = parameter.Name,
                Type = parameter.Type,
                RefKind = parameter.RefKind,
                Index = parameter.Index + 1
            });
        }

        BoundExpr? receiver = null;
        if (!method.IsStatic)
        {
            receiver = new BoundConversion
            {
                Operand = new BoundParam { Symbol = targetParameter, Type = _object },
                Type = method.Owner
            };
        }

        var call = BindMemberCall(receiver, method, thunk.Parameters.Skip(1).Select(ParameterRead).ToList());
        _thunks.Add((method, type), thunk);
        _readyBodies.Add(new BoundMethodBody
        {
            Method = thunk,
            Body = new BoundBlock
            {
                Statements = [invoke.ReturnType == _void
                    ? new BoundExprStmt { Expression = call }
                    : new BoundReturn { Value = call }]
            }
        });
        return thunk;
    }

    private static bool IsDelegateType(ITypeSymbol? type) => type?.TypeKind == RoslynKind.Delegate;

    // '+', '-', '==' and '!=' on two delegates of one type. The built-in
    // operators have no method symbol in Roslyn's operation tree.
    private BoundExpr LowerDelegateBinary(IBinaryOperation binary)
    {
        var left = LowerExpr(binary.LeftOperand);
        var right = LowerExpr(binary.RightOperand);
        var type = MapType(binary.Type, binary.Syntax.GetLocation());
        switch (binary.OperatorKind)
        {
            case BinaryOperatorKind.Add:
                return new BoundBinary { Op = TokenKind.Plus, Kind = BinKind.DelegateCombine, Left = left, Right = right, Type = type };
            case BinaryOperatorKind.Subtract:
                return new BoundBinary { Op = TokenKind.Minus, Kind = BinKind.DelegateRemove, Left = left, Right = right, Type = type };
            case BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals:
            {
                var op = binary.OperatorKind == BinaryOperatorKind.Equals ? TokenKind.EqualsEquals : TokenKind.BangEquals;
                var againstNull = left is BoundLiteral { LitKind: LiteralKind.Null } || right is BoundLiteral { LitKind: LiteralKind.Null };
                return new BoundBinary
                {
                    Op = op, Kind = againstNull ? BinKind.RefEq : BinKind.DelegateEq, Left = left, Right = right, Type = _bool
                };
            }
        }

        return UnsupportedExpr(binary);
    }

    private BoundExpr LowerDelegateCompound(ICompoundAssignmentOperation assignment, BoundExpr target)
    {
        if (target is BoundParam { Symbol: { RefKind: RefKind.None } })
        {
            Report(assignment.Syntax, "reassigning a reference-typed parameter is not supported", "ARC2001");
            return ErrorExpr();
        }

        return new BoundDelegateCompound
        {
            Target = target,
            Right = LowerExpr(assignment.Value),
            IsRemove = assignment.OperatorKind == BinaryOperatorKind.Subtract,
            Type = target.Type
        };
    }

    // 'receiver?.member' with a reference receiver and a reference result. The
    // receiver is evaluated once. When it is null the whole expression is null.
    private BoundExpr LowerConditionalAccess(IConditionalAccessOperation access)
    {
        var type = MapType(access.Type, access.Syntax.GetLocation());
        var receiver = LowerExpr(access.Operation);
        if (!receiver.Type.IsReferenceType || !type.IsReferenceType)
        {
            return UnsupportedExpr(access);
        }

        var input = new BoundPatternInput { Type = receiver.Type };
        _accessReceivers.Push(() => input);
        var present = LowerExpr(access.WhenNotNull);
        _accessReceivers.Pop();
        return Capture(receiver, input, new BoundChoice
        {
            Type = type,
            Condition = NotNull(input),
            WhenTrue = present,
            WhenFalse = new BoundLiteral { Type = type, LitKind = LiteralKind.Null }
        });
    }

    // 'receiver?.member;' as a statement, whatever the member returns. The
    // receiver waits in a hidden local that is cleared before the statement ends.
    private BoundStmt LowerConditionalAccessStatement(IConditionalAccessOperation access)
    {
        var receiver = LowerExpr(access.Operation);
        if (!receiver.Type.IsReferenceType)
        {
            return UnsupportedStmt(access);
        }

        var held = new LocalSymbol { Name = "#access", Type = receiver.Type, Id = _localId++ };
        _methodLocals.Add(held);
        BoundLocal Read() => new() { Symbol = held, Type = held.Type };
        _accessReceivers.Push(Read);
        BoundStmt present = access.WhenNotNull is IConditionalAccessOperation nested
            ? LowerConditionalAccessStatement(nested)
            : new BoundExprStmt { Expression = LowerExpr(access.WhenNotNull) };
        _accessReceivers.Pop();
        return new BoundBlock
        {
            Statements =
            [
                new BoundLocalDecl { Symbol = held, Initializer = receiver },
                new BoundIf { Condition = NotNull(Read()), Then = present },
                new BoundExprStmt
                {
                    Expression = new BoundAssign
                    {
                        Target = Read(), Value = new BoundLiteral { Type = held.Type, LitKind = LiteralKind.Null }, Type = held.Type
                    }
                }
            ]
        };
    }
}
