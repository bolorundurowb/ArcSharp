using ArcSharp.Binding;
using ArcSharp.Lexing;
using ArcSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using BoundKind = ArcSharp.Binding.TypeKind;
using RefKind = ArcSharp.Syntax.RefKind;

namespace ArcSharp.Frontend;

sealed partial class RoslynLowering
{
    private BoundExpr LowerPatternTest(IIsPatternOperation operation)
    {
        var value = LowerExpr(operation.Value);
        var input = new BoundPatternInput { Type = value.Type };
        return new BoundPatternTest
        {
            Value = value, Input = input, Test = LowerPattern(operation.Pattern, input), Type = _bool
        };
    }

    private BoundExpr LowerSwitchExpression(ISwitchExpressionOperation operation)
    {
        var value = LowerExpr(operation.Value);
        var input = new BoundPatternInput { Type = value.Type };
        var resultType = MapType(operation.Type, operation.Syntax.GetLocation());
        var exceptionSymbol = _compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.SwitchExpressionException");
        var exceptionType = MapType(exceptionSymbol, operation.Syntax.GetLocation());
        var constructor = exceptionType.Methods.Find(method => method.IsConstructor && method.Parameters.Count == 0);
        if (constructor == null)
        {
            return UnsupportedExpr(operation);
        }

        BoundExpr body = new BoundSwitchFailure
        {
            Type = resultType,
            Exception = new BoundNewObject { Type = exceptionType, Ctor = constructor, Site = Site(operation.Syntax) }
        };
        for (var i = operation.Arms.Length - 1; i >= 0; i--)
        {
            var arm = operation.Arms[i];
            body = new BoundChoice
            {
                Condition = LowerGuardedPattern(arm.Pattern, arm.Guard, input),
                WhenTrue = LowerExpr(arm.Value),
                WhenFalse = body,
                Type = resultType
            };
        }

        return new BoundPatternTest { Value = value, Input = input, Test = body, Type = resultType };
    }

    private BoundExpr LowerConditionalOperator(IBinaryOperation operation, IMethodSymbol method)
    {
        var name = operation.OperatorKind == BinaryOperatorKind.ConditionalAnd ? "op_False" : "op_True";
        var tester = method.ContainingType.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(candidate =>
            candidate.Parameters.Length == 1
            && SymbolEqualityComparer.Default.Equals(candidate.Parameters[0].Type, method.Parameters[0].Type));
        var test = tester == null ? null : MapMethod(tester, operation.Syntax.GetLocation());
        var combine = MapMethod(method, operation.Syntax.GetLocation());
        if (test == null || combine == null || test.IsAbstract || combine.IsAbstract)
        {
            return UnsupportedExpr(operation);
        }

        var value = LowerExpr(operation.LeftOperand);
        var input = new BoundPatternInput { Type = value.Type };
        return new BoundPatternTest
        {
            Type = combine.ReturnType,
            Value = value,
            Input = input,
            Test = new BoundChoice
            {
                Type = combine.ReturnType,
                Condition = new BoundCall { Type = _bool, Method = test, Arguments = [input] },
                WhenTrue = input,
                WhenFalse = new BoundCall
                {
                    Type = combine.ReturnType, Method = combine, Arguments = [input, LowerExpr(operation.RightOperand)]
                }
            }
        };
    }

    private BoundExpr LowerGuardedPattern(IPatternOperation pattern, IOperation? guard, BoundPatternInput input)
    {
        var test = LowerPattern(pattern, input);
        return guard == null ? test : new BoundBinary
        {
            Type = _bool,
            Kind = BinKind.BoolLogic,
            Op = TokenKind.AmpAmp,
            Left = test,
            Right = LowerExpr(guard)
        };
    }

    private BoundExpr LowerPattern(IPatternOperation pattern, BoundPatternInput input) => pattern switch
    {
        IDiscardPatternOperation => PatternResult(true),
        IConstantPatternOperation constant => LowerPatternComparison(pattern, input, constant.Value, TokenKind.EqualsEquals),
        IRelationalPatternOperation relational => LowerPatternComparison(pattern, input, relational.Value, BinaryToken(relational.OperatorKind)),
        ITypePatternOperation type => LowerPatternTypeCheck(type, input, MapType(type.MatchedType, type.Syntax.GetLocation())),
        IDeclarationPatternOperation declaration => LowerPatternDeclaration(declaration, input),
        IRecursivePatternOperation recursive => LowerRecursivePattern(recursive, input),
        IListPatternOperation list => LowerListPattern(list, input),
        INegatedPatternOperation negated => new BoundUnary
        {
            Type = _bool, Op = TokenKind.Bang, Operand = LowerPattern(negated.Pattern, input)
        },
        IBinaryPatternOperation binary => new BoundBinary
        {
            Type = _bool,
            Kind = BinKind.BoolLogic,
            Op = binary.OperatorKind == BinaryOperatorKind.And ? TokenKind.AmpAmp : TokenKind.PipePipe,
            Left = LowerPattern(binary.LeftPattern, input),
            Right = LowerPattern(binary.RightPattern, input)
        },
        _ => UnsupportedExpr(pattern)
    };

    private BoundExpr LowerListPattern(IListPatternOperation pattern, BoundPatternInput input)
    {
        if (input.Type.Kind != BoundKind.Array || input.Type.ElementType is not { } element)
        {
            return UnsupportedExpr(pattern);
        }

        var length = new BoundPatternInput { Type = _int };
        BoundLiteral Number(int number) => new() { Type = _int, LitKind = LiteralKind.Int, IntValue = number };
        BoundBinary Difference(int number) => new()
        {
            Op = TokenKind.Minus, Kind = BinKind.IntArith, Left = length, Right = Number(number), Type = _int
        };
        var count = pattern.Patterns.Length;
        var hasSlice = pattern.Patterns.Any(item => item is ISlicePatternOperation);
        var fixedCount = hasSlice ? count - 1 : count;
        BoundExpr test = new BoundBinary
        {
            Op = hasSlice ? TokenKind.GreaterEquals : TokenKind.EqualsEquals,
            Kind = BinKind.IntCmp, Left = length, Right = Number(fixedCount), Type = _bool
        };

        var afterSlice = false;
        for (var i = 0; i < count; i++)
        {
            var item = pattern.Patterns[i];
            BoundExpr value;
            if (item is ISlicePatternOperation slice)
            {
                afterSlice = true;
                if (slice.Pattern == null)
                {
                    continue;
                }

                item = slice.Pattern;
                value = new BoundArraySlice
                {
                    Type = input.Type, Receiver = input, Start = Number(i),
                    Length = Difference(fixedCount), ElementType = element, Site = Site(slice.Syntax)
                };
            }
            else
            {
                value = new BoundIndex
                {
                    Type = element, Receiver = input,
                    Index = afterSlice ? Difference(count - i) : Number(i)
                };
            }

            var captured = new BoundPatternInput { Type = value.Type };
            test = BoolAnd(test, Capture(value, captured, LowerPattern(item, captured)));
        }

        if (pattern.DeclaredSymbol is ILocalSymbol declaration)
        {
            test = new BoundPatternBinding { Type = _bool, Test = test, Local = Local(declaration), Value = input };
        }

        return BoolAnd(PatternNullCheck(input, TokenKind.BangEquals),
            Capture(new BoundLength { Type = _int, Receiver = input }, length, test));
    }

    private BoundExpr LowerRecursivePattern(IRecursivePatternOperation pattern, BoundPatternInput input)
    {
        var type = MapType(pattern.MatchedType, pattern.Syntax.GetLocation());
        var check = LowerPatternTypeCheck(pattern, input, type);
        BoundExpr narrowed = input;
        if (input.Type != type)
        {
            if (input.Type.NullableElementType == type)
            {
                narrowed = ReadNullableValue(input);
            }
            else if (type.Kind == BoundKind.Struct && input.Type.IsReferenceType)
            {
                narrowed = new BoundUnbox { Operand = input, BoxedValue = StructBox(type), Type = type };
            }
            else
            {
                narrowed = NumericConversion(input, type);
            }
        }

        var receiver = new BoundPatternInput { Type = type };
        BoundExpr properties = PatternResult(true);
        foreach (var property in pattern.PropertySubpatterns)
        {
            var value = LowerRecursiveMember(property.Member, receiver);
            if (value == null)
            {
                return UnsupportedExpr(property);
            }

            var captured = new BoundPatternInput { Type = value.Type };
            properties = BoolAnd(properties, Capture(value, captured, LowerPattern(property.Pattern, captured)));
        }

        if (pattern.DeclaredSymbol is ILocalSymbol declaration)
        {
            properties = new BoundPatternBinding
            {
                Type = _bool, Test = properties, Local = Local(declaration), Value = receiver
            };
        }

        if (pattern.DeconstructionSubpatterns.Length > 0)
        {
            if (pattern.DeconstructSymbol is not IMethodSymbol deconstruct
                || MapMethod(deconstruct, pattern.Syntax.GetLocation()) is not { IsStatic: false } method
                || method.Parameters.Count != pattern.DeconstructionSubpatterns.Length)
            {
                return UnsupportedExpr(pattern);
            }

            var arguments = new List<BoundExpr>();
            BoundExpr tests = PatternResult(true);
            for (var i = 0; i < method.Parameters.Count; i++)
            {
                var parameter = method.Parameters[i];
                var local = new LocalSymbol { Name = "#deconstruct", Id = _localId++, Type = parameter.Type };
                _methodLocals.Add(local);
                arguments.Add(new BoundByRefArg
                {
                    Kind = RefKind.Out, Type = local.Type, Lvalue = new BoundLocal { Symbol = local, Type = local.Type }
                });
                var captured = new BoundPatternInput { Type = local.Type };
                tests = BoolAnd(tests, Capture(new BoundLocal { Symbol = local, Type = local.Type },
                    captured, LowerPattern(pattern.DeconstructionSubpatterns[i], captured)));
            }

            var call = BindMemberCall(receiver, method, arguments);
            properties = Capture(call, new BoundPatternInput { Type = _void }, BoolAnd(tests, properties));
        }

        return BoolAnd(check, Capture(narrowed, receiver, properties));
    }

    private BoundExpr? LowerRecursiveMember(IOperation member, BoundPatternInput receiver)
    {
        if (member is IFieldReferenceOperation { Instance: IInstanceReferenceOperation } field
            && MapField(field.Field, field.Syntax.GetLocation()) is { } fieldSymbol)
        {
            return new BoundFieldAccess { Receiver = receiver, Field = fieldSymbol, Type = fieldSymbol.Type };
        }

        if (member is IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Arguments.Length: 0 } property
            && property.Property.GetMethod is { } getter
            && MapMethod(getter, property.Syntax.GetLocation()) is { } method)
        {
            return BindMemberCall(receiver, method, []);
        }

        return null;
    }

    private BoundExpr LowerPatternComparison(IOperation pattern, BoundExpr input, IOperation value, TokenKind op)
    {
        var type = input.Type;
        if (!value.ConstantValue.HasValue)
        {
            return UnsupportedExpr(pattern);
        }

        var constantValue = value.ConstantValue.Value;
        if (type.NullableElementType != null)
        {
            var hasValue = HasNullableValue(input);
            return constantValue == null
                ? Not(hasValue)
                : BoolAnd(hasValue, LowerPatternComparison(pattern, ReadNullableValue(input), value, op));
        }

        if (constantValue == null)
        {
            return type.IsReferenceType ? PatternNullCheck(input, op) : PatternResult(false);
        }

        if (type == _decimal && constantValue is decimal amount)
        {
            return DecimalComparison(op, [input, DecimalConstant(amount)]);
        }

        var kind = type switch
        {
            _ when type == _string => BinKind.StrEq,
            _ when type == _bool => BinKind.BoolCmp,
            _ when type == _float => BinKind.FloatCmp,
            _ when type == _double => BinKind.DoubleCmp,
            _ when type.IsInteger => BinKind.IntCmp,
            _ => (BinKind?)null
        };
        if (kind == null)
        {
            return UnsupportedExpr(pattern);
        }

        if (constantValue is double real && double.IsNaN(real) || constantValue is float single && float.IsNaN(single))
        {
            return new BoundUnary
            {
                Type = _bool,
                Op = TokenKind.Bang,
                Operand = new BoundBinary { Kind = kind.Value, Op = TokenKind.EqualsEquals, Left = input, Right = input, Type = _bool }
            };
        }

        var right = LowerConstant(constantValue, value.Type, value.Syntax);
        if (right.Type != type && (type.IsInteger || type.IsFloat))
        {
            right = new BoundConversion { Operand = right, Type = type };
        }

        return new BoundBinary
        {
            Kind = kind.Value, Op = op, IsUnsigned = type.IsUnsignedInteger, Left = input, Right = right, Type = _bool
        };
    }

    private BoundExpr LowerPatternDeclaration(IDeclarationPatternOperation pattern, BoundPatternInput input)
    {
        var symbol = pattern.DeclaredSymbol as ILocalSymbol;
        var local = symbol == null ? null : Local(symbol);
        var type = local?.Type ?? (pattern.MatchedType == null ? input.Type : MapType(pattern.MatchedType, pattern.Syntax.GetLocation()));
        var check = pattern.MatchesNull ? PatternResult(true) : LowerPatternTypeCheck(pattern, input, type);
        if (local == null || type == _error)
        {
            return check;
        }

        BoundExpr source = input;
        if (type != input.Type)
        {
            if (input.Type.NullableElementType == type)
            {
                source = ReadNullableValue(input);
            }
            else if (type.Kind == BoundKind.Struct && input.Type.IsReferenceType)
            {
                source = new BoundUnbox { Operand = input, BoxedValue = StructBox(type), Type = type };
            }
            else if (input.Type.Kind == BoundKind.Struct && type.IsReferenceType)
            {
                source = new BoundConversion { Operand = BoxValue(input, pattern.Syntax), Type = type };
            }
            else
            {
                source = new BoundConversion { Operand = input, Type = type };
            }
        }

        return new BoundPatternBinding { Test = check, Value = source, Local = local, Type = _bool };
    }

    private BoundExpr LowerPatternTypeCheck(IPatternOperation pattern, BoundPatternInput input, TypeSymbol type)
    {
        if (input.Type.NullableElementType is { } nullableElement)
        {
            return type == nullableElement ? HasNullableValue(input) : UnsupportedExpr(pattern);
        }

        if (type == input.Type || type == _object)
        {
            return input.Type.IsReferenceType ? PatternNullCheck(input, TokenKind.BangEquals) : PatternResult(true);
        }

        if (input.Type.IsReferenceType && type.Kind is BoundKind.Class or BoundKind.Struct or BoundKind.Interface)
        {
            var descriptor = type.Kind == BoundKind.Struct ? StructBox(type).Owner : type;
            return new BoundIs { Operand = input, TestType = descriptor, Type = _bool };
        }

        if (input.Type.Kind == BoundKind.Struct && type.Kind == BoundKind.Interface
            && DeclaredInterfaceClosure(input.Type).Contains(type))
        {
            return PatternResult(true);
        }

        return UnsupportedExpr(pattern);
    }

    private BoundExpr PatternNullCheck(BoundExpr input, TokenKind op) => new BoundBinary
    {
        Type = _bool,
        Kind = BinKind.RefEq,
        Op = op,
        Left = input,
        Right = new BoundLiteral { Type = _null, LitKind = LiteralKind.Null }
    };

    private BoundLiteral PatternResult(bool result) =>
        new() { Type = _bool, LitKind = LiteralKind.Bool, BoolValue = result };
}
