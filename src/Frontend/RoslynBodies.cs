using System.Collections.Immutable;
using ArcSharp.Binding;
using ArcSharp.Diagnostics;
using ArcSharp.Lexing;
using ArcSharp.Syntax;
using BinaryOperatorKind = Microsoft.CodeAnalysis.Operations.BinaryOperatorKind;
using BoundKind = ArcSharp.Binding.TypeKind;
using DiagnosticSeverity = ArcSharp.Lexing.DiagnosticSeverity;
using RefKind = ArcSharp.Syntax.RefKind;
using RoslynKind = Microsoft.CodeAnalysis.TypeKind;
using UnaryOperatorKind = Microsoft.CodeAnalysis.Operations.UnaryOperatorKind;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace ArcSharp.Frontend;

sealed partial class RoslynLowering
{
    private void LowerBody(PendingBody pending)
    {
        var method = pending.Method;
        var roslyn = pending.Roslyn.OriginalDefinition;
        _currentType = method.Owner;
        _currentMethod = method;
        _methodLocals = [];
        _locals.Clear();
        _localId = 0;
        var saved = new Dictionary<ITypeSymbol, TypeSymbol>(_substitution, SymbolEqualityComparer.Default);
        _substitution.Clear();
        var typeDefinition = roslyn.ContainingType.OriginalDefinition;
        for (var i = 0; i < typeDefinition.TypeParameters.Length && i < method.Owner.TypeArguments.Count; i++)
        {
            _substitution[typeDefinition.TypeParameters[i]] = method.Owner.TypeArguments[i];
        }

        for (var i = 0; i < roslyn.TypeParameters.Length && i < method.TypeArguments.Count; i++)
        {
            _substitution[roslyn.TypeParameters[i]] = method.TypeArguments[i];
        }

        var block = new BoundBlock();
        var syntax = roslyn.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (roslyn.MethodKind == MethodKind.StaticConstructor
            || (method.IsConstructor && syntax is not ConstructorDeclarationSyntax { Initializer.ThisOrBaseKeyword.RawKind: (int)SyntaxKind.ThisKeyword }))
        {
            AppendFieldInitialisers(block, method);
        }

        if (method.IsConstructor && roslyn.IsImplicitlyDeclared && method.Owner.BaseType is { } baseType)
        {
            var baseConstructor = baseType.Methods.Find(candidate => candidate.IsConstructor && candidate.Parameters.Count == 0);
            if (baseConstructor != null)
            {
                block.Statements.Add(new BoundExprStmt
                {
                    Expression = new BoundCall
                    {
                        Receiver = new BoundThis { Type = method.Owner },
                        Method = baseConstructor,
                        Arguments = [],
                        Type = _void
                    }
                });
            }
        }

        if (syntax != null)
        {
            var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
            var operation = model.GetOperation(syntax);
            switch (operation)
            {
                case IConstructorBodyOperation constructor:
                    var initialiser = LowerInitialiser(constructor.Initializer);
                    if (initialiser != null)
                    {
                        block.Statements.Add(new BoundExprStmt { Expression = initialiser });
                    }

                    AppendBlock(block, constructor.BlockBody ?? constructor.ExpressionBody);
                    break;
                case IMethodBodyOperation body:
                    AppendBlock(block, body.BlockBody ?? body.ExpressionBody);
                    break;
                case IBlockOperation blockOperation:
                    AppendBlock(block, blockOperation);
                    break;
                default:
                    if (roslyn.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet)
                    {
                        AppendAccessorBody(block, model, syntax);
                    }

                    break;
            }
        }

        _readyBodies.Add(new BoundMethodBody { Method = method, Body = block, AllLocals = _methodLocals });
        _substitution.Clear();
        foreach (var pair in saved)
        {
            _substitution[pair.Key] = pair.Value;
        }
    }

    private void AppendAccessorBody(BoundBlock block, SemanticModel model, SyntaxNode syntax)
    {
        var body = syntax switch
        {
            AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody,
            PropertyDeclarationSyntax property => property.ExpressionBody,
            IndexerDeclarationSyntax indexer => indexer.ExpressionBody,
            ArrowExpressionClauseSyntax arrow => arrow,
            _ => null
        };
        if (body != null && model.GetOperation(body) is IBlockOperation statements)
        {
            AppendBlock(block, statements);
            return;
        }

        if (body is ArrowExpressionClauseSyntax expression && model.GetOperation(expression.Expression) is { } operation)
        {
            while (operation.Parent is IConversionOperation conversion && conversion.IsImplicit)
            {
                operation = conversion;
            }

            var value = LowerExpr(operation);
            block.Statements.Add(_currentMethod.ReturnType == _void
                ? new BoundExprStmt { Expression = value }
                : new BoundReturn { Value = value });
            return;
        }

        Report(syntax, "could not lower the accessor body", "ARC2001");
    }

    private void AppendFieldInitialisers(BoundBlock block, MethodSymbol method)
    {
        if (!_fieldInitialisers.TryGetValue(method.Owner, out var initialisers))
        {
            return;
        }

        foreach (var (field, syntax) in initialisers)
        {
            if (field.IsStatic != method.IsStatic)
            {
                continue;
            }

            var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
            var value = model.GetOperation(syntax) switch
            {
                IFieldInitializerOperation fieldInitialiser => fieldInitialiser.Value,
                IPropertyInitializerOperation propertyInitialiser => propertyInitialiser.Value,
                _ => null
            };
            if (value == null)
            {
                Report(syntax, $"could not lower initialiser for '{field.Name}'", "ARC2001");
                continue;
            }

            var target = new BoundFieldAccess
            {
                Field = field,
                Type = field.Type,
                Receiver = field.IsStatic ? null : new BoundThis { Type = method.Owner }
            };
            block.Statements.Add(new BoundExprStmt
            {
                Expression = new BoundAssign { Target = target, Value = LowerExpr(value), Type = field.Type }
            });
        }
    }

    private BoundExpr? LowerInitialiser(IOperation? initialiser)
    {
        if (initialiser is IExpressionStatementOperation statement)
        {
            initialiser = statement.Operation;
        }

        if (initialiser is not IInvocationOperation call)
        {
            return null;
        }

        var target = call.TargetMethod;
        if (target.ContainingType.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType
            || (target.IsImplicitlyDeclared && target.ContainingType.TypeKind == RoslynKind.Struct))
        {
            return null;
        }

        var mapped = MapMethod(target, call.Syntax.GetLocation());
        if (mapped == null)
        {
            Report(call.Syntax, $"no matching constructor on '{target.ContainingType.Name}'", "ARC2001");
            return null;
        }

        return new BoundCall
        {
            Receiver = new BoundThis { Type = _currentType },
            Method = mapped,
            Arguments = LowerArguments(call.Arguments, mapped),
            Type = _void
        };
    }

    private void AppendBlock(BoundBlock block, IBlockOperation? body)
    {
        if (body == null)
        {
            return;
        }

        foreach (var operation in body.Operations)
        {
            block.Statements.Add(LowerStmt(operation));
        }
    }

    private BoundStmt LowerStmt(IOperation operation) => operation switch
    {
        IBlockOperation block => LowerBlock(block),
        IVariableDeclarationGroupOperation group => LowerDeclarations(group),
        IExpressionStatementOperation statement => new BoundExprStmt { Expression = LowerExpr(statement.Operation) },
        IConditionalOperation ifOperation => new BoundIf
        {
            Condition = LowerExpr(ifOperation.Condition),
            Then = LowerStmt(ifOperation.WhenTrue),
            Else = ifOperation.WhenFalse == null ? null : LowerStmt(ifOperation.WhenFalse)
        },
        IWhileLoopOperation loop => new BoundWhile
        {
            Condition = loop.Condition == null
                ? new BoundLiteral { Type = _bool, LitKind = LiteralKind.Bool, BoolValue = true }
                : LowerExpr(loop.Condition),
            Body = LowerStmt(loop.Body),
            ConditionIsTop = loop.ConditionIsTop
        },
        IForLoopOperation loop => LowerFor(loop),
        IForEachLoopOperation loop => LowerArrayForEach(loop),
        ISwitchOperation selection => LowerSwitch(selection),
        IBranchOperation { BranchKind: BranchKind.Break or BranchKind.Continue } branch =>
            new BoundBranch { IsContinue = branch.BranchKind == BranchKind.Continue },
        IUsingOperation usingOperation => LowerUsing(usingOperation),
        ITryOperation tryOperation => LowerTry(tryOperation),
        IThrowOperation thrown => new BoundThrow
        {
            Value = thrown.Exception == null ? null : LowerExpr(thrown.Exception)
        },
        IReturnOperation returned => new BoundReturn
        {
            Value = returned.ReturnedValue == null ? null : LowerExpr(returned.ReturnedValue)
        },
        _ => UnsupportedStmt(operation)
    };

    private BoundStmt LowerTry(ITryOperation operation)
    {
        var bound = new BoundTry
        {
            Body = LowerBlock(operation.Body),
            Finally = operation.Finally == null ? null : LowerBlock(operation.Finally)
        };
        foreach (var clause in operation.Catches)
        {
            // A clause without a type is reported as System.Object, and every
            // thrown value derives from Exception, so Exception catches all.
            var type = MapType(clause.ExceptionType, clause.Syntax.GetLocation());
            var catchesAll = type == _object || type.Name == "System.Exception";
            LocalSymbol? variable = null;
            if (clause.ExceptionDeclarationOrExpression is IVariableDeclaratorOperation declarator)
            {
                variable = Local(declarator.Symbol);
            }

            bound.Catches.Add(new BoundCatch
            {
                ExceptionType = catchesAll ? null : type,
                Variable = variable,
                Body = LowerBlock(clause.Handler)
            });
        }

        return bound;
    }

    private BoundBlock LowerBlock(IBlockOperation block)
    {
        var bound = new BoundBlock();
        foreach (var operation in block.Operations)
        {
            bound.Statements.Add(LowerStmt(operation));
        }

        return bound;
    }

    private BoundStmt LowerDeclarations(IVariableDeclarationGroupOperation group)
    {
        var block = new BoundBlock();
        foreach (var declaration in group.Declarations)
        {
            foreach (var declarator in declaration.Declarators)
            {
                if (!declarator.Symbol.IsConst)
                {
                    block.Statements.Add(LowerDeclarator(declarator));
                }
            }
        }

        return block.Statements.Count == 1 ? block.Statements[0] : block;
    }

    private BoundLocalDecl LowerDeclarator(IVariableDeclaratorOperation declarator)
    {
        var initialiser = declarator.Initializer == null ? null : LowerExpr(declarator.Initializer.Value);
        var local = Local(declarator.Symbol);
        return new BoundLocalDecl { Symbol = local, Initializer = initialiser };
    }

    private BoundStmt LowerFor(IForLoopOperation loop)
    {
        BoundStmt? init = null;
        if (loop.Before.Length == 1)
        {
            init = LowerStmt(loop.Before[0]);
        }
        else if (loop.Before.Length > 1)
        {
            var block = new BoundBlock();
            foreach (var operation in loop.Before)
            {
                block.Statements.Add(LowerStmt(operation));
            }

            init = block;
        }

        BoundExpr? update = null;
        if (loop.AtLoopBottom.Length == 1 && loop.AtLoopBottom[0] is IExpressionStatementOperation statement)
        {
            update = LowerExpr(statement.Operation);
        }
        else if (loop.AtLoopBottom.Length > 0)
        {
            Report(loop.Syntax, "this for-loop update is not supported", DiagnosticCatalog.UnsupportedConstructId);
        }

        return new BoundFor
        {
            Init = init,
            Condition = loop.Condition == null ? null : LowerExpr(loop.Condition),
            Update = update,
            Body = LowerStmt(loop.Body)
        };
    }

    private BoundStmt LowerSwitch(ISwitchOperation operation)
    {
        var value = LowerExpr(operation.Value);
        if (value.Type.Kind != BoundKind.Primitive && value.Type != _string)
        {
            return UnsupportedStmt(operation);
        }

        var result = new BoundSwitch { Value = value };
        foreach (var arm in operation.Cases)
        {
            var section = new BoundSwitchSection { Body = new BoundBlock() };
            foreach (var clause in arm.Clauses)
            {
                switch (clause)
                {
                    case IDefaultCaseClauseOperation:
                        section.IsDefault = true;
                        break;
                    case ISingleValueCaseClauseOperation label:
                        section.Labels.Add(LowerExpr(label.Value));
                        break;
                    default:
                        return UnsupportedStmt(clause);
                }
            }

            section.Body.Statements.AddRange(arm.Body.Select(LowerStmt));
            result.Sections.Add(section);
        }

        return result;
    }

    private BoundStmt LowerArrayForEach(IForEachLoopOperation loop)
    {
        if (loop.Syntax is not ForEachStatementSyntax syntax
            || loop.IsAsynchronous
            || loop.LoopControlVariable is not IVariableDeclaratorOperation { Symbol.RefKind: Microsoft.CodeAnalysis.RefKind.None } variable)
        {
            return UnsupportedStmt(loop);
        }

        var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
        if (model.GetTypeInfo(syntax.Expression).Type is not IArrayTypeSymbol { IsSZArray: true })
        {
            return UnsupportedStmt(loop);
        }

        var conversion = model.GetForEachStatementInfo(syntax).ElementConversion;
        if (conversion.IsUserDefined || conversion.IsBoxing || conversion.IsUnboxing)
        {
            return UnsupportedStmt(loop);
        }

        var collection = loop.Collection is IConversionOperation { IsImplicit: true } implicitConversion
            ? implicitConversion.Operand
            : loop.Collection;
        var receiver = LowerExpr(collection);
        if (receiver.Type.ElementType is not { } elementType)
        {
            return UnsupportedStmt(loop);
        }

        var array = new LocalSymbol { Name = "#foreach_array", Type = receiver.Type, Id = _localId++ };
        var index = new LocalSymbol { Name = "#foreach_index", Type = _int, Id = _localId++ };
        _methodLocals.Add(array);
        _methodLocals.Add(index);

        BoundLocal Read(LocalSymbol local) => new() { Symbol = local, Type = local.Type };
        BoundLiteral Number(int value) => new() { Type = _int, LitKind = LiteralKind.Int, IntValue = value };
        BoundTry ReleaseAfter(LocalSymbol local, BoundBlock body) => new()
        {
            Body = body,
            Finally = new BoundBlock
            {
                Statements = [new BoundExprStmt
                {
                    Expression = new BoundAssign
                    {
                        Target = Read(local), Value = DefaultValue(local.Type), Type = local.Type
                    }
                }]
            }
        };

        var iteration = Local(variable.Symbol);
        BoundExpr item = new BoundIndex { Receiver = Read(array), Index = Read(index), Type = elementType };
        if (iteration.Type != elementType)
        {
            item = new BoundConversion
            {
                Operand = item,
                Type = iteration.Type,
                CastCheck = conversion is { IsReference: true, IsImplicit: false }
                    && iteration.Type.Kind == BoundKind.Class && iteration.Type != _object
            };
        }

        var body = new BoundBlock
        {
            Statements = [
                new BoundLocalDecl { Symbol = iteration, Initializer = item },
                LowerStmt(loop.Body)
            ]
        };
        var lowered = new BoundFor
        {
            Init = new BoundLocalDecl { Symbol = index, Initializer = Number(0) },
            Condition = new BoundBinary
            {
                Op = TokenKind.Less, Kind = BinKind.IntCmp, Left = Read(index), Type = _bool,
                Right = new BoundLength { Receiver = Read(array), Type = _int }
            },
            Body = iteration.Type.IsReferenceType ? ReleaseAfter(iteration, body) : body,
            Update = new BoundAssign
            {
                Target = Read(index), Type = _int,
                Value = new BoundBinary
                {
                    Op = TokenKind.Plus, Kind = BinKind.IntArith, Left = Read(index), Right = Number(1), Type = _int
                }
            }
        };
        return new BoundBlock
        {
            Statements = [
                new BoundLocalDecl { Symbol = array, Initializer = receiver },
                ReleaseAfter(array, new BoundBlock { Statements = [lowered] })
            ]
        };
    }

    private BoundStmt LowerUsing(IUsingOperation operation)
    {
        if (operation.IsAsynchronous)
        {
            return UnsupportedStmt(operation);
        }

        BoundExpr? init;
        LocalSymbol resource;
        var line = LineOf(operation.Syntax);
        if (operation.Resources is IVariableDeclarationGroupOperation group)
        {
            var declarator = group.Declarations.SelectMany(declaration => declaration.Declarators).FirstOrDefault();
            if (declarator == null)
            {
                return UnsupportedStmt(operation);
            }

            init = declarator.Initializer == null ? null : LowerExpr(declarator.Initializer.Value);
            resource = Local(declarator.Symbol);
        }
        else
        {
            init = LowerExpr(operation.Resources);
            resource = new LocalSymbol { Name = "#using" + _usingId++, Type = init.Type, Id = _localId++ };
            _methodLocals.Add(resource);
        }

        var dispose = ResolveDispose(resource.Type, operation.Syntax);
        return new BoundUsing
        {
            Resource = resource,
            ResourceInit = init,
            Body = LowerStmt(operation.Body),
            Dispose = dispose.Method,
            DisposeInterface = dispose.Interface,
            DisposeVirtual = dispose.Virtual,
            DisposeSelector = dispose.Selector
        };
    }

    private (MethodSymbol? Method, bool Interface, bool Virtual, int Selector) ResolveDispose(TypeSymbol type, SyntaxNode syntax)
    {
        if (type == _error || type == _null)
        {
            return (null, false, false, -1);
        }

        if (!type.IsReferenceType)
        {
            Report(syntax, $"type '{type}' used in 'using' must be a reference type", "ARC2011");
            return (null, false, false, -1);
        }

        if (type.Kind == BoundKind.Interface)
        {
            var method = type.Methods.FirstOrDefault(candidate => candidate.Name == "Dispose" && candidate.Parameters.Count == 0);
            if (method == null || !_selectors.TryGetValue(method, out var selector))
            {
                Report(syntax, $"interface '{type}' has no parameterless 'Dispose' method", "ARC2011");
                return (null, false, false, -1);
            }

            return (method, true, false, selector);
        }

        var dispose = FindMethod(type, "Dispose", 0);
        if (dispose == null || dispose.IsStatic || dispose.ReturnType.Kind != BoundKind.Void)
        {
            Report(syntax, $"type '{type}' used in 'using' has no parameterless 'void Dispose()' method", "ARC2011");
            return (null, false, false, -1);
        }

        return (dispose, false, dispose.IsVirtual || dispose.IsOverride, -1);
    }

    private BoundExpr LowerExpr(IOperation operation) => operation switch
    {
        ILiteralOperation literal => LowerLiteral(literal),
        ILocalReferenceOperation { Local.HasConstantValue: true } local =>
            LowerConstant(local.Local.ConstantValue, local.Type, local.Syntax),
        ILocalReferenceOperation local => LocalRef(local.Local),
        IParameterReferenceOperation parameter => ParameterRef(parameter.Parameter),
        IInstanceReferenceOperation => new BoundThis { Type = _currentType },
        IFieldReferenceOperation field => LowerField(field),
        IPropertyReferenceOperation property => LowerProperty(property),
        IInvocationOperation invocation => LowerInvocation(invocation),
        IObjectCreationOperation creation => LowerCreation(creation),
        ITypeParameterObjectCreationOperation creation => LowerTypeParameterCreation(creation.Type, creation.Syntax),
        IArrayCreationOperation array => LowerArray(array),
        IArrayElementReferenceOperation element => LowerElement(element),
        IBinaryOperation binary => LowerBinary(binary),
        IUnaryOperation unary => LowerUnary(unary),
        IConversionOperation conversion => LowerConversion(conversion),
        IIsTypeOperation test => new BoundIs
        {
            Operand = LowerExpr(test.ValueOperand),
            TestType = MapType(test.TypeOperand, test.Syntax.GetLocation()),
            Type = _bool
        },
        ISimpleAssignmentOperation assignment => LowerAssignment(assignment),
        ICompoundAssignmentOperation assignment => LowerCompound(assignment),
        IIncrementOrDecrementOperation increment => LowerIncrement(increment),
        IDefaultValueOperation value => value.Type == null
            ? ErrorExpr()
            : DefaultValue(value.Type, value.Syntax.GetLocation()),
        IDeclarationExpressionOperation declaration => LowerExpr(declaration.Expression),
        _ => UnsupportedExpr(operation)
    };

    private BoundExpr LowerLiteral(ILiteralOperation literal) =>
        LowerConstant(literal.ConstantValue.HasValue ? literal.ConstantValue.Value : null, literal.Type, literal.Syntax);

    private BoundExpr LowerConstant(object? value, ITypeSymbol? roslynType, SyntaxNode syntax)
    {
        if (value == null)
        {
            var nullType = roslynType == null || roslynType.TypeKind == RoslynKind.Error
                ? _null
                : MapType(roslynType, syntax.GetLocation());
            return new BoundLiteral { Type = nullType == _error ? _null : nullType, LitKind = LiteralKind.Null };
        }

        var type = MapType(roslynType, syntax.GetLocation());
        return value switch
        {
            bool boolean => new BoundLiteral { Type = _bool, LitKind = LiteralKind.Bool, BoolValue = boolean },
            char character => new BoundLiteral { Type = _char, LitKind = LiteralKind.Char, IntValue = character },
            string text => new BoundLiteral
            {
                Type = _string,
                LitKind = LiteralKind.String,
                StringValue = text,
                Site = Site(syntax)
            },
            float number => new BoundLiteral { Type = _float, LitKind = LiteralKind.Float, FloatValue = number },
            double number => new BoundLiteral { Type = _double, LitKind = LiteralKind.Double, FloatValue = number },
            ulong number => new BoundLiteral { Type = _ulong, LitKind = LiteralKind.ULong, IntValue = unchecked((long)number) },
            long number => new BoundLiteral { Type = _long, LitKind = LiteralKind.Long, IntValue = number },
            uint number => new BoundLiteral { Type = _uint, LitKind = LiteralKind.UInt, IntValue = number },
            _ => new BoundLiteral
            {
                Type = type == _error ? _int : type,
                LitKind = LiteralKind.Int,
                IntValue = Convert.ToInt64(value)
            }
        };
    }

    private BoundExpr LocalRef(ILocalSymbol symbol)
    {
        var local = Local(symbol);
        return new BoundLocal { Symbol = local, Type = local.Type };
    }

    private LocalSymbol Local(ILocalSymbol symbol)
    {
        if (_locals.TryGetValue(symbol, out var existing))
        {
            return existing;
        }

        var created = new LocalSymbol
        {
            Name = symbol.Name,
            Type = MapType(symbol.Type, symbol.Locations.FirstOrDefault()),
            Id = _localId++
        };
        _locals[symbol] = created;
        _methodLocals.Add(created);
        return created;
    }

    private BoundExpr ParameterRef(IParameterSymbol parameter)
    {
        var symbol = _currentMethod.Parameters.FirstOrDefault(candidate => candidate.Index == parameter.Ordinal)
            ?? _currentMethod.Parameters.FirstOrDefault(candidate => candidate.Name == parameter.Name);
        if (symbol == null)
        {
            Report(parameter.Locations.FirstOrDefault(), $"unknown parameter '{parameter.Name}'", "ARC2002");
            return ErrorExpr();
        }

        return new BoundParam { Symbol = symbol, Type = symbol.Type, ByRef = symbol.RefKind != RefKind.None };
    }

    private BoundExpr LowerField(IFieldReferenceOperation operation)
    {
        if (operation.Field.HasConstantValue)
        {
            return LowerConstant(operation.Field.ConstantValue, operation.Type, operation.Syntax);
        }

        var field = MapField(operation.Field, operation.Syntax.GetLocation());
        if (field == null)
        {
            Report(operation.Syntax, $"unknown field '{operation.Field.Name}'", "ARC2002");
            return ErrorExpr();
        }

        BoundExpr? receiver = null;
        if (!field.IsStatic)
        {
            receiver = operation.Instance == null
                ? new BoundThis { Type = _currentType }
                : LowerExpr(operation.Instance);
        }

        return new BoundFieldAccess { Receiver = receiver, Field = field, Type = field.Type };
    }

    private BoundExpr LowerProperty(IPropertyReferenceOperation operation)
    {
        var property = operation.Property;
        if (property.Name == "Length"
            && (property.ContainingType.SpecialType == SpecialType.System_String
                || operation.Instance?.Type is IArrayTypeSymbol))
        {
            var receiver = operation.Instance == null ? ErrorExpr() : LowerExpr(operation.Instance);
            return new BoundLength
            {
                Receiver = receiver,
                IsString = property.ContainingType.SpecialType == SpecialType.System_String,
                Type = _int
            };
        }

        if (property.GetMethod == null)
        {
            Report(operation.Syntax, $"'{property.Name}' has no getter", "ARC2001");
            return ErrorExpr();
        }

        var getter = MapMethod(property.GetMethod, operation.Syntax.GetLocation());
        if (getter == null)
        {
            Report(operation.Syntax, $"'{operation.Instance?.Type?.ToDisplayString() ?? property.ContainingType.Name}' has no member '{property.Name}'", "ARC2002");
            return ErrorExpr();
        }

        BoundExpr? instance = null;
        if (!getter.IsStatic)
        {
            instance = operation.Instance == null ? new BoundThis { Type = _currentType } : LowerExpr(operation.Instance);
        }

        return BindMemberCall(instance, getter, LowerArguments(operation.Arguments, getter));
    }

    private BoundExpr LowerInvocation(IInvocationOperation call)
    {
        var target = call.TargetMethod;
        if (IsConsole(target))
        {
            return LowerConsole(call);
        }

        if (IsConcat(target) && call.Syntax is BinaryExpressionSyntax)
        {
            return LowerConcat(call);
        }

        if (IsWeakReference(target.ContainingType))
        {
            return LowerWeakCall(call);
        }

        if (IsGc(target))
        {
            return LowerGc(call);
        }

        if (target.ContainingType.SpecialType == SpecialType.System_Object
            || IsStringMember(target) && target.Name != "Length")
        {
            Report(call.Syntax, $"'{target.ContainingType.Name}.{target.Name}' is not supported", DiagnosticCatalog.UnsupportedConstructId);
            return ErrorExpr();
        }

        var method = MapMethod(target, call.Syntax.GetLocation());
        if (method == null)
        {
            Report(call.Syntax, $"'{target.ContainingType.Name}' has no method '{target.Name}'", "ARC2002");
            return ErrorExpr();
        }

        BoundExpr? receiver = null;
        if (!method.IsStatic)
        {
            receiver = call.Instance == null ? new BoundThis { Type = _currentType } : LowerExpr(call.Instance);
        }

        return BindMemberCall(receiver, method, LowerArguments(call.Arguments, method));
    }

    private BoundCall BindMemberCall(BoundExpr? receiver, MethodSymbol method, List<BoundExpr> arguments)
    {
        var interfaceCall = false;
        var selector = 0;
        if (method.Owner.Kind == BoundKind.Interface)
        {
            if (receiver != null
                && receiver.Type.Kind == BoundKind.Class
                && _selectors.TryGetValue(method, out selector)
                && receiver.Type.InterfaceImpl.TryGetValue(selector, out var implementation))
            {
                // A constraint such as `where U : IValue<T>` binds the call to
                // the interface method. The class that satisfies it has the body.
                method = implementation;
            }
            else
            {
                interfaceCall = true;
                _selectors.TryGetValue(method, out selector);
            }
        }

        return new BoundCall
        {
            Receiver = receiver,
            Method = method,
            Arguments = arguments,
            Type = method.ReturnType,
            Interface = interfaceCall,
            Selector = selector,
            Virtual = !interfaceCall && (method.IsVirtual || method.IsOverride)
        };
    }

    private BoundExpr LowerConsole(IInvocationOperation call)
    {
        var which = call.TargetMethod.Name == "Write" ? ConsoleKind.Write : ConsoleKind.WriteLine;
        if (call.TargetMethod.Name is not ("Write" or "WriteLine"))
        {
            Report(call.Syntax, $"Console has no method '{call.TargetMethod.Name}'", "ARC2001");
            return ErrorExpr();
        }

        if (call.Arguments.Length == 0)
        {
            return new BoundConsoleCall { Which = which, Type = _void };
        }

        if (call.Arguments.Length != 1)
        {
            Report(call.Syntax, "Console.Write/WriteLine takes 0 or 1 argument", "ARC2001");
        }

        var argument = call.Arguments.Length > 0 ? LowerExpr(UnwrapBoxing(call.Arguments[0].Value)) : null;
        if (argument != null && argument.Type.Kind == BoundKind.Struct)
        {
            Report(call.Syntax, "struct values cannot be printed: print their fields", DiagnosticCatalog.UnsupportedConstructId);
            return ErrorExpr();
        }

        return new BoundConsoleCall { Which = which, Argument = argument, Type = _void };
    }

    private BoundExpr LowerConcat(IInvocationOperation call)
    {
        var parts = new List<BoundExpr>();
        foreach (var argument in call.Arguments)
        {
            if (argument.Value is IArrayCreationOperation array && array.Initializer != null)
            {
                foreach (var element in array.Initializer.ElementValues)
                {
                    parts.Add(LowerExpr(UnwrapBoxing(element)));
                }
            }
            else
            {
                parts.Add(LowerExpr(UnwrapBoxing(argument.Value)));
            }
        }

        if (parts.Count == 0)
        {
            return new BoundLiteral { Type = _string, LitKind = LiteralKind.String, StringValue = "", Site = Site(call.Syntax) };
        }

        var folded = parts[0];
        for (var i = 1; i < parts.Count; i++)
        {
            folded = new BoundBinary
            {
                Op = TokenKind.Plus,
                Kind = BinKind.StrConcat,
                Left = folded,
                Right = parts[i],
                Type = _string
            };
        }

        return folded;
    }

    private BoundExpr LowerWeakCall(IInvocationOperation call)
    {
        var receiver = call.Instance == null ? ErrorExpr() : LowerExpr(call.Instance);
        if (call.TargetMethod.Name == "TryGetTarget")
        {
            if (call.Arguments.Length != 1)
            {
                Report(call.Syntax, "TryGetTarget expects a single 'out' argument", "ARC2001");
                return ErrorExpr();
            }

            var target = LValue(call.Arguments[0].Value);
            return new BoundWeakRefTryGet { WeakRef = receiver, OutTarget = target, Type = _bool };
        }

        if (call.TargetMethod.Name == "SetTarget")
        {
            if (call.Arguments.Length != 1)
            {
                Report(call.Syntax, "SetTarget(target) takes one argument", "ARC2001");
                return ErrorExpr();
            }

            return new BoundWeakRefSet
            {
                WeakRef = receiver,
                Target = LowerExpr(call.Arguments[0].Value),
                Type = _void
            };
        }

        Report(call.Syntax, $"WeakReference<T> has no method '{call.TargetMethod.Name}'", "ARC2001");
        return ErrorExpr();
    }

    private BoundExpr LowerGc(IInvocationOperation call)
    {
        switch (call.TargetMethod.Name)
        {
            case "SuppressFinalize" when call.Arguments.Length == 1:
            {
                var target = LowerExpr(call.Arguments[0].Value);
                if (!target.Type.IsReferenceType && target.Type != _error)
                {
                    Report(call.Syntax, "GC.SuppressFinalize takes a reference: a value type would have to be boxed", DiagnosticCatalog.UnsupportedConstructId);
                    return ErrorExpr();
                }

                return new BoundGcCall { Which = GcKind.SuppressFinalize, Argument = target, Type = _void };
            }
            case "Collect" when call.Arguments.Length == 0:
                Report(call.Syntax, DiagnosticCatalog.GcCollectMessage, DiagnosticCatalog.ArcClrDisagreementId, DiagnosticSeverity.Info);
                return new BoundGcCall { Which = GcKind.Collect, Type = _void };
            case "WaitForPendingFinalizers" when call.Arguments.Length == 0:
                Report(call.Syntax, DiagnosticCatalog.GcWaitMessage, DiagnosticCatalog.ArcClrDisagreementId, DiagnosticSeverity.Info);
                return new BoundGcCall { Which = GcKind.WaitForPendingFinalizers, Type = _void };
            case "ReRegisterForFinalize":
                Report(call.Syntax, DiagnosticCatalog.GcReRegisterMessage, DiagnosticCatalog.UnsupportedConstructId);
                return ErrorExpr();
        }

        Report(call.Syntax, $"GC.{call.TargetMethod.Name} is not supported", DiagnosticCatalog.UnsupportedConstructId);
        return ErrorExpr();
    }

    private BoundExpr LowerTypeParameterCreation(ITypeSymbol? type, SyntaxNode syntax)
    {
        var concrete = MapType(type, syntax.GetLocation());
        if (concrete.Kind == BoundKind.Primitive || concrete.Kind == BoundKind.String)
        {
            return DefaultValue(concrete);
        }

        var parameterless = concrete.Methods.FirstOrDefault(method => method.IsConstructor && method.Parameters.Count == 0);
        return new BoundNewObject
        {
            Ctor = parameterless,
            Type = concrete,
            Site = Site(syntax)
        };
    }

    private BoundExpr LowerCreation(IObjectCreationOperation creation)
    {
        if (creation.Type is ITypeParameterSymbol || creation.Constructor?.ContainingType.TypeKind == RoslynKind.TypeParameter)
        {
            return LowerTypeParameterCreation(creation.Type, creation.Syntax);
        }

        var type = MapType(creation.Type, creation.Syntax.GetLocation());
        if (type.Kind == BoundKind.Primitive)
        {
            return DefaultValue(type);
        }

        if (type.Kind == BoundKind.WeakRef)
        {
            var target = creation.Arguments.Length == 1 ? LowerExpr(creation.Arguments[0].Value) : ErrorExpr();
            return new BoundWeakRefNew { Target = target, Type = type, Site = Site(creation.Syntax) };
        }

        if (type.IsAbstract)
        {
            Report(creation.Syntax, $"cannot instantiate abstract class '{type.Name}'", "ARC2012");
            return ErrorExpr();
        }

        if (type.Kind is not (BoundKind.Class or BoundKind.Struct))
        {
            Report(creation.Syntax, $"cannot instantiate '{type.Name}'", "ARC2001");
            return ErrorExpr();
        }

        MethodSymbol? ctor = null;
        if (creation.Constructor != null
            && (!creation.Constructor.IsImplicitlyDeclared || type.Kind == BoundKind.Class)
            && creation.Constructor.ContainingType.SpecialType != SpecialType.System_Object)
        {
            ctor = MapMethod(creation.Constructor, creation.Syntax.GetLocation());
        }

        return new BoundNewObject
        {
            Ctor = ctor,
            Arguments = ctor == null ? [] : LowerArguments(creation.Arguments, ctor),
            Type = type,
            Site = Site(creation.Syntax)
        };
    }

    private BoundExpr LowerArray(IArrayCreationOperation array) => LowerArray(array, isParams: false);

    private BoundExpr LowerArray(IArrayCreationOperation array, bool isParams)
    {
        if (array.DimensionSizes.Length != 1)
        {
            Report(array.Syntax, "jagged and multidimensional arrays are not supported", DiagnosticCatalog.UnsupportedConstructId);
            return ErrorExpr();
        }

        if (array.Initializer != null && !isParams)
        {
            Report(array.Syntax, "array initialisers are not supported", DiagnosticCatalog.UnsupportedConstructId);
            return ErrorExpr();
        }

        var type = MapType(array.Type, array.Syntax.GetLocation());
        var element = type.ElementType ?? _error;
        return new BoundNewArray
        {
            ElementType = element,
            Size = LowerExpr(array.DimensionSizes[0]),
            Initializers = array.Initializer?.ElementValues.Select(LowerExpr).ToList() ?? [],
            Type = type,
            Site = Site(array.Syntax)
        };
    }

    private BoundExpr LowerElement(IArrayElementReferenceOperation element)
    {
        var receiver = LowerExpr(element.ArrayReference);
        if (element.Indices.Length != 1)
        {
            Report(element.Syntax, "jagged and multidimensional arrays are not supported", DiagnosticCatalog.UnsupportedConstructId);
            return ErrorExpr();
        }

        return new BoundIndex
        {
            Receiver = receiver,
            Index = LowerExpr(element.Indices[0]),
            Type = receiver.Type.ElementType ?? _error
        };
    }

    private BoundExpr LowerBinary(IBinaryOperation binary)
    {
        if (binary.OperatorMethod is { } method)
        {
            return binary.IsLifted || binary.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr
                ? UnsupportedExpr(binary)
                : LowerOperator(binary, method, binary.LeftOperand, binary.RightOperand);
        }

        if (binary.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
        {
            return new BoundBinary
            {
                Op = binary.OperatorKind == BinaryOperatorKind.ConditionalAnd ? TokenKind.AmpAmp : TokenKind.PipePipe,
                Kind = BinKind.BoolLogic,
                Left = LowerExpr(binary.LeftOperand),
                Right = LowerExpr(binary.RightOperand),
                Type = _bool
            };
        }

        if (binary.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Concatenate
            && (IsStringSide(binary.LeftOperand) || IsStringSide(binary.RightOperand)))
        {
            return new BoundBinary
            {
                Op = TokenKind.Plus,
                Kind = BinKind.StrConcat,
                Left = LowerExpr(UnwrapBoxing(binary.LeftOperand)),
                Right = LowerExpr(UnwrapBoxing(binary.RightOperand)),
                Type = _string
            };
        }

        var left = LowerExpr(binary.LeftOperand);
        var right = LowerExpr(binary.RightOperand);
        var op = BinaryToken(binary.OperatorKind);
        if (op is TokenKind.EqualsEquals or TokenKind.BangEquals
            && binary.LeftOperand.Type?.SpecialType == SpecialType.System_String
            && binary.RightOperand.Type?.SpecialType == SpecialType.System_String)
        {
            return new BoundBinary { Op = op, Kind = BinKind.StrEq, Left = left, Right = right, Type = _bool };
        }

        if (op is TokenKind.EqualsEquals or TokenKind.BangEquals
            && (left.Type.IsReferenceType || right.Type.IsReferenceType || left.Type == _null || right.Type == _null))
        {
            return new BoundBinary { Op = op, Kind = BinKind.RefEq, Left = left, Right = right, Type = _bool };
        }

        var operand = left.Type.Kind == BoundKind.Error ? right.Type : left.Type;
        var compare = op is TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater
            or TokenKind.GreaterEquals or TokenKind.EqualsEquals or TokenKind.BangEquals;
        var (kind, unsigned) = operand switch
        {
            _ when operand == _bool => (compare ? BinKind.BoolCmp : BinKind.BoolArith, false),
            _ when operand == _double && compare => (BinKind.DoubleCmp, false),
            _ when operand == _float && compare => (BinKind.FloatCmp, false),
            _ when operand == _long || operand == _ulong => compare
                ? (BinKind.LongCmp, operand.IsUnsignedInteger)
                : (BinKind.LongArith, operand.IsUnsignedInteger),
            _ when operand == _double => (BinKind.DoubleArith, false),
            _ when operand == _float => (BinKind.FloatArith, false),
            _ => compare ? (BinKind.IntCmp, operand.IsUnsignedInteger) : (BinKind.IntArith, operand.IsUnsignedInteger)
        };
        return new BoundBinary
        {
            Op = op,
            Kind = kind,
            IsUnsigned = unsigned,
            IsChecked = binary.IsChecked,
            Left = left,
            Right = right,
            Type = compare ? _bool : operand
        };
    }

    private BoundExpr LowerUnary(IUnaryOperation unary)
    {
        if (unary.OperatorMethod is { } method)
        {
            return unary.IsLifted ? UnsupportedExpr(unary) : LowerOperator(unary, method, unary.Operand);
        }

        var operand = LowerExpr(unary.Operand);
        if (unary.OperatorKind == UnaryOperatorKind.Plus)
        {
            return operand;
        }

        if (unary.OperatorKind == UnaryOperatorKind.Not)
        {
            return new BoundUnary { Op = TokenKind.Bang, Operand = operand, Type = _bool };
        }

        if (unary.OperatorKind == UnaryOperatorKind.BitwiseNegation)
        {
            return new BoundUnary { Op = TokenKind.Tilde, Operand = operand, Type = operand.Type };
        }

        if (unary.OperatorKind == UnaryOperatorKind.Minus)
        {
            return new BoundUnary { Op = TokenKind.Minus, Operand = operand, Type = operand.Type, IsChecked = unary.IsChecked };
        }

        return UnsupportedExpr(unary);
    }

    private BoundExpr LowerConversion(IConversionOperation conversion)
    {
        if (conversion.OperatorMethod is { } method)
        {
            return LowerOperator(conversion, method, conversion.Operand);
        }

        if (conversion.IsTryCast)
        {
            var operand = LowerExpr(conversion.Operand);
            var target = MapType(conversion.Type, conversion.Syntax.GetLocation());
            return new BoundAs { Operand = operand, TestType = target, Type = target };
        }

        if (IsBoxing(conversion) || conversion.Conversion.IsIdentity)
        {
            return LowerExpr(conversion.Operand);
        }

        var value = LowerExpr(conversion.Operand);
        var type = MapType(conversion.Type, conversion.Syntax.GetLocation());
        if (value.Type == type || type == _error || value.Type == _error)
        {
            return value;
        }

        // An explicit reference conversion can fail at run time. A cast to an
        // interface stays unchecked, because only classes have a type descriptor
        // the runtime can test against.
        var castCheck = conversion.Conversion is { IsReference: true, IsImplicit: false }
            && type.Kind == BoundKind.Class
            && type != _object;
        return new BoundConversion { Operand = value, Type = type, CastCheck = castCheck, IsChecked = conversion.IsChecked };
    }

    private BoundExpr LowerOperator(IOperation operation, IMethodSymbol method, params IOperation[] operands)
    {
        var mapped = MapMethod(method, operation.Syntax.GetLocation());
        if (mapped == null || mapped.IsAbstract || !mapped.IsStatic
            || mapped.Parameters.Any(parameter => parameter.RefKind != RefKind.None))
        {
            return UnsupportedExpr(operation);
        }

        return new BoundCall
        {
            Method = mapped,
            Arguments = operands.Select(LowerExpr).ToList(),
            Type = mapped.ReturnType
        };
    }

    private BoundExpr LowerAssignment(ISimpleAssignmentOperation assignment)
    {
        if (assignment.Target is IPropertyReferenceOperation property)
        {
            return AssignProperty(property, LowerExpr(assignment.Value), assignment.Syntax);
        }

        var target = LowerExpr(assignment.Target);
        var value = LowerExpr(assignment.Value);
        if (target is BoundParam { Symbol: { Type.IsReferenceType: true, RefKind: RefKind.None } parameter })
        {
            Report(assignment.Syntax, "reassigning a reference-typed parameter is not supported in this first pass", "ARC2001");
            _ = parameter;
        }

        return new BoundAssign { Target = target, Value = value, Type = target.Type == _error ? value.Type : target.Type };
    }

    private BoundExpr? LowerUpdateTarget(IOperation operation)
    {
        if (operation is ILocalReferenceOperation or IParameterReferenceOperation
            or IFieldReferenceOperation or IArrayElementReferenceOperation)
        {
            return LowerExpr(operation);
        }

        if (operation is not IPropertyReferenceOperation property || property.Property.IsVirtual
            || property.Property.IsOverride || property.Property.IsAbstract || property.Property.IsIndexer
            || property.Property.GetMethod is not { } getter
            || property.Property.SetMethod is not { } setter
            || HasAccessorBody(getter) || HasAccessorBody(setter))
        {
            return null;
        }

        var method = MapMethod(getter, operation.Syntax.GetLocation());
        var field = method?.Owner.Properties.Find(candidate => candidate.Getter == method)?.BackingField;
        if (field == null)
        {
            return null;
        }

        return new BoundFieldAccess
        {
            Type = field.Type,
            Field = field,
            Receiver = field.IsStatic ? null : property.Instance == null
                ? new BoundThis { Type = _currentType }
                : LowerExpr(property.Instance)
        };
    }

    private BoundExpr LowerIncrement(IIncrementOrDecrementOperation operation)
    {
        if (operation.OperatorMethod != null || operation.IsLifted
            || LowerUpdateTarget(operation.Target) is not { } target
            || !(target.Type.IsInteger || target.Type.IsFloat))
        {
            return UnsupportedExpr(operation);
        }

        return new BoundIncrement
        {
            Target = target,
            Type = target.Type,
            IsDecrement = operation.Kind == OperationKind.Decrement,
            IsPostfix = operation.IsPostfix,
            IsChecked = operation.IsChecked
        };
    }

    private BoundExpr LowerCompound(ICompoundAssignmentOperation assignment)
    {
        if (assignment.OperatorMethod != null || assignment.IsLifted
            || LowerUpdateTarget(assignment.Target) is not { } target)
        {
            return UnsupportedExpr(assignment);
        }

        if (target.Type == _string && assignment.OperatorKind == BinaryOperatorKind.Add)
        {
            if (target is BoundParam { Symbol.RefKind: RefKind.None })
            {
                Report(assignment.Syntax, "reassigning a reference-typed parameter is not supported", "ARC2001");
                return ErrorExpr();
            }

            var value = LowerExpr(UnwrapBoxing(assignment.Value));
            if (value.Type != _string && !(value.Type.IsInteger || value.Type.IsFloat || value.Type == _bool)
                && value is not BoundLiteral { LitKind: LiteralKind.Null })
            {
                return UnsupportedExpr(assignment);
            }

            return new BoundStringCompound { Target = target, Right = value, Type = _string };
        }

        if (assignment.OperatorKind is not (BinaryOperatorKind.Add or BinaryOperatorKind.Subtract
            or BinaryOperatorKind.Multiply or BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder
            or BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr
            or BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift or BinaryOperatorKind.UnsignedRightShift))
        {
            return UnsupportedExpr(assignment);
        }

        var operand = LowerExpr(assignment.Value);
        if (!(target.Type.IsInteger || target.Type.IsFloat || target.Type == _bool)
            || !(operand.Type.IsInteger || operand.Type.IsFloat || operand.Type == _bool))
        {
            return UnsupportedExpr(assignment);
        }

        var isShift = assignment.OperatorKind is BinaryOperatorKind.LeftShift
            or BinaryOperatorKind.RightShift or BinaryOperatorKind.UnsignedRightShift;
        var operationType = isShift
            ? (target.Type == _uint || target.Type == _long || target.Type == _ulong ? target.Type : _int)
            : ArithmeticType(target.Type, operand.Type);
        return new BoundNumericCompound
        {
            Target = target,
            Right = operand,
            Type = target.Type,
            OperationType = operationType,
            Op = BinaryToken(assignment.OperatorKind),
            IsChecked = assignment.IsChecked
        };
    }

    private TypeSymbol ArithmeticType(TypeSymbol left, TypeSymbol right)
    {
        if (left == _bool)
        {
            return _bool;
        }

        if (left == _double || right == _double)
        {
            return _double;
        }

        if (left == _float || right == _float)
        {
            return _float;
        }

        if (left == _ulong || right == _ulong)
        {
            return _ulong;
        }

        if (left == _long || right == _long)
        {
            return _long;
        }

        if (left == _uint || right == _uint)
        {
            return left.IsUnsignedInteger && right.IsUnsignedInteger ? _uint : _long;
        }

        return _int;
    }

    private BoundExpr AssignProperty(IPropertyReferenceOperation property, BoundExpr value, SyntaxNode syntax)
    {
        if (property.Property.SetMethod == null)
        {
            var getter = property.Property.GetMethod == null
                ? null
                : MapMethod(property.Property.GetMethod, syntax.GetLocation());
            var backing = getter?.Owner.Properties.Find(candidate => candidate.Getter == getter)?.BackingField;
            if (_currentMethod.IsConstructor && backing != null && backing.Owner == _currentType
                && property.Instance is IInstanceReferenceOperation instance)
            {
                var target = new BoundFieldAccess
                {
                    Receiver = LowerExpr(instance),
                    Field = backing,
                    Type = backing.Type
                };
                return new BoundAssign { Target = target, Value = value, Type = backing.Type };
            }

            Report(syntax, $"get-only property '{property.Property.Name}' cannot be assigned", "ARC2001");
            return ErrorExpr();
        }

        var setter = MapMethod(property.Property.SetMethod, syntax.GetLocation());
        if (setter == null)
        {
            Report(syntax, $"'{property.Property.Name}' has no setter", "ARC2002");
            return ErrorExpr();
        }

        BoundExpr? receiver = null;
        if (!setter.IsStatic)
        {
            receiver = property.Instance == null ? new BoundThis { Type = _currentType } : LowerExpr(property.Instance);
        }

        var arguments = LowerArguments(property.Arguments, setter);
        arguments.Add(value);
        var call = BindMemberCall(receiver, setter, arguments);
        call.Type = setter.Parameters[^1].Type;
        call.ReturnsAssignedValue = true;
        return call;
    }

    private List<BoundExpr> LowerArguments(ImmutableArray<IArgumentOperation> arguments, MethodSymbol method)
    {
        var bound = new List<BoundExpr>();
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            var ordinal = argument.Parameter?.Ordinal ?? i;
            var parameter = ordinal < method.Parameters.Count ? method.Parameters[ordinal] : null;
            BoundExpr value;
            if (parameter != null && parameter.RefKind != RefKind.None)
            {
                var lvalue = parameter.RefKind == RefKind.In
                    ? LowerExpr(argument.Value)
                    : LValue(argument.Value);
                value = new BoundByRefArg { Lvalue = lvalue, Kind = parameter.RefKind, Type = lvalue.Type };
            }
            else
            {
                value = argument is { ArgumentKind: ArgumentKind.ParamArray, Value: IArrayCreationOperation array }
                    ? LowerArray(array, isParams: true)
                    : LowerExpr(argument.Value);
            }

            bound.Add(ordinal == i ? value : new BoundNamedArgument { Ordinal = ordinal, Value = value, Type = value.Type });
        }

        return bound;
    }

    private BoundExpr LValue(IOperation operation)
    {
        if (operation is IDeclarationExpressionOperation declaration)
        {
            operation = declaration.Expression;
        }

        if (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return LowerExpr(operation);
    }

    private static bool IsBoxing(IConversionOperation conversion) =>
        conversion.Operand.Type is { IsValueType: true } && conversion.Type is { IsReferenceType: true };

    private static IOperation UnwrapBoxing(IOperation operation)
    {
        while (operation is IConversionOperation conversion
               && (IsBoxing(conversion) || conversion.Conversion.IsIdentity
                   || conversion.Type?.SpecialType == SpecialType.System_Object))
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private BoundExpr DefaultValue(ITypeSymbol type, Location location) => DefaultValue(MapType(type, location));

    private BoundExpr DefaultValue(TypeSymbol type)
    {
        if (type.IsReferenceType || type == _null)
        {
            return new BoundLiteral { Type = type == _null ? _null : type, LitKind = LiteralKind.Null };
        }

        if (type == _bool)
        {
            return new BoundLiteral { Type = _bool, LitKind = LiteralKind.Bool };
        }

        if (type == _float)
        {
            return new BoundLiteral { Type = _float, LitKind = LiteralKind.Float };
        }

        if (type == _double)
        {
            return new BoundLiteral { Type = _double, LitKind = LiteralKind.Double };
        }

        if (type == _long)
        {
            return new BoundLiteral { Type = _long, LitKind = LiteralKind.Long };
        }

        return new BoundLiteral { Type = type == _error ? _int : type, LitKind = LiteralKind.Int };
    }

    private static TokenKind BinaryToken(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Add => TokenKind.Plus,
        BinaryOperatorKind.Subtract => TokenKind.Minus,
        BinaryOperatorKind.Multiply => TokenKind.Star,
        BinaryOperatorKind.Divide => TokenKind.Slash,
        BinaryOperatorKind.Remainder => TokenKind.Percent,
        BinaryOperatorKind.And => TokenKind.Amp,
        BinaryOperatorKind.Or => TokenKind.Pipe,
        BinaryOperatorKind.ExclusiveOr => TokenKind.Caret,
        BinaryOperatorKind.LeftShift => TokenKind.LeftShift,
        BinaryOperatorKind.RightShift => TokenKind.RightShift,
        BinaryOperatorKind.UnsignedRightShift => TokenKind.UnsignedRightShift,
        BinaryOperatorKind.Equals => TokenKind.EqualsEquals,
        BinaryOperatorKind.NotEquals => TokenKind.BangEquals,
        BinaryOperatorKind.LessThan => TokenKind.Less,
        BinaryOperatorKind.LessThanOrEqual => TokenKind.LessEquals,
        BinaryOperatorKind.GreaterThan => TokenKind.Greater,
        BinaryOperatorKind.GreaterThanOrEqual => TokenKind.GreaterEquals,
        _ => TokenKind.Plus
    };

    private static bool IsStringSide(IOperation operation)
    {
        operation = UnwrapBoxing(operation);
        return operation.Type?.SpecialType == SpecialType.System_String;
    }

    private static bool IsConsole(IMethodSymbol method) =>
        method.ContainingType.Name == "Console" && method.ContainingType.ContainingNamespace?.ToDisplayString() == "System";

    private static bool IsGc(IMethodSymbol method) =>
        method.ContainingType.Name == "GC" && method.ContainingType.ContainingNamespace?.ToDisplayString() == "System";

    private static bool IsConcat(IMethodSymbol method) =>
        method.Name == "Concat" && method.ContainingType.SpecialType == SpecialType.System_String;

    private static bool IsStringMember(IMethodSymbol method) =>
        method.ContainingType.SpecialType == SpecialType.System_String;

    private BoundStmt UnsupportedStmt(IOperation operation)
    {
        Report(operation.Syntax, $"'{Describe(operation)}' is not supported", DiagnosticCatalog.UnsupportedConstructId);
        return new BoundBlock();
    }

    private BoundExpr UnsupportedExpr(IOperation operation)
    {
        Report(operation.Syntax, $"'{Describe(operation)}' is not supported", DiagnosticCatalog.UnsupportedConstructId);
        return ErrorExpr();
    }

    private static string Describe(IOperation operation) => operation.Syntax.Kind().ToString();

    private BoundExpr ErrorExpr() => new BoundLiteral { Type = _error, LitKind = LiteralKind.Null };

    private string Site(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        var file = string.IsNullOrEmpty(_currentType.SourceFile)
            ? Path.GetFileName(span.Path)
            : _currentType.SourceFile;
        if (string.IsNullOrEmpty(file))
        {
            file = Path.GetFileName(span.Path);
        }

        return file + ":" + (span.StartLinePosition.Line + 1);
    }

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
