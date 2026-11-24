using ArcSharp.Binding;
using ArcSharp.Lexing;
using ArcSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using BoundKind = ArcSharp.Binding.TypeKind;

namespace ArcSharp.Frontend;

sealed partial class RoslynLowering
{
    private static bool IsNullableSymbol(ITypeSymbol? type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private TypeSymbol GetNullableType(TypeSymbol element)
    {
        if (_nullableTypes.TryGetValue(element, out var existing))
        {
            return existing;
        }

        var type = new TypeSymbol
        {
            Name = element.Name + ".$nullable", Kind = BoundKind.Struct, NullableElementType = element
        };
        var hasValue = new FieldSymbol { Name = "hasValue", Type = _bool, Owner = type };
        var value = new FieldSymbol { Name = "value", Type = element, Owner = type };
        _nullableTypes.Add(element, type);
        List<FieldSymbol> fields = [hasValue, value];

        var parameter = new ParamSymbol { Name = "value", Type = element };
        var constructor = new MethodSymbol
        {
            Name = "<ctor>", Owner = type, ReturnType = _void, IsConstructor = true, Parameters = [parameter]
        };
        List<MethodSymbol> members = [constructor];
        BoundFieldAccess Read(FieldSymbol field) => new()
        {
            Receiver = new BoundThis { Type = type }, Field = field, Type = field.Type
        };
        BoundExprStmt Store(FieldSymbol field, BoundExpr expression) => new()
        {
            Expression = new BoundAssign { Target = Read(field), Value = expression, Type = field.Type }
        };
        _readyBodies.Add(new BoundMethodBody
        {
            Method = constructor,
            Body = new BoundBlock
            {
                Statements = [Store(value, new BoundParam { Symbol = parameter, Type = element }), Store(hasValue, PatternResult(true))]
            }
        });

        void AddMember(string name, TypeSymbol result, BoundStmt body, List<ParamSymbol>? parameters = null)
        {
            var method = new MethodSymbol { Name = name, Owner = type, ReturnType = result, Parameters = parameters ?? [] };
            members.Add(method);
            _readyBodies.Add(new BoundMethodBody { Method = method, Body = new BoundBlock { Statements = [body] } });
        }

        var exceptionType = MapType(_compilation.GetTypeByMetadataName("System.InvalidOperationException"), null);
        var exceptionCtor = exceptionType.Methods.Find(method => method.IsConstructor && method.Parameters.Count == 1);
        var missingValue = new BoundNewObject
        {
            Type = exceptionType,
            Ctor = exceptionCtor,
            Arguments = [new BoundLiteral
            {
                Type = _string, LitKind = LiteralKind.String, StringValue = "Nullable object must have a value."
            }]
        };
        AddMember("get_HasValue", _bool, new BoundReturn { Value = Read(hasValue) });
        AddMember("get_Value", element, new BoundIf
        {
            Condition = Read(hasValue),
            Then = new BoundReturn { Value = Read(value) },
            Else = new BoundThrow { Value = missingValue }
        });
        AddMember("GetValueOrDefault", element, new BoundReturn { Value = Read(value) });
        AddMember("GetValueOrDefault", element, new BoundReturn
        {
            Value = new BoundChoice
            {
                Type = element, Condition = Read(hasValue), WhenTrue = Read(value),
                WhenFalse = new BoundParam { Symbol = parameter, Type = element }
            }
        }, [parameter]);

        if (StructFieldsPending(type, fields))
        {
            _deferredLayouts.Add((type, fields, members));
        }
        else
        {
            FinishLayout(type, fields, members);
        }

        return type;
    }

    private BoundExpr LowerNullableConversion(IConversionOperation conversion, BoundExpr value, TypeSymbol type)
    {
        if (type.NullableElementType is { } element)
        {
            if (value is BoundLiteral { LitKind: LiteralKind.Null })
            {
                return DefaultValue(type);
            }

            if (value.Type.NullableElementType != null)
            {
                var input = new BoundPatternInput { Type = value.Type };
                return Capture(value, input, new BoundChoice
                {
                    Type = type, Condition = HasNullableValue(input), WhenFalse = DefaultValue(type),
                    WhenTrue = NewNullable(type, NumericConversion(ReadNullableValue(input), element, conversion.IsChecked))
                });
            }

            if (value.Type.IsReferenceType)
            {
                return UnsupportedExpr(conversion);
            }

            return NewNullable(type, NumericConversion(value, element, conversion.IsChecked));
        }

        if (type.IsReferenceType)
        {
            return UnsupportedExpr(conversion);
        }

        var getter = value.Type.Methods.Find(method => method.Name == "get_Value");
        if (getter == null)
        {
            return UnsupportedExpr(conversion);
        }

        return NumericConversion(new BoundCall { Method = getter, Receiver = value, Type = getter.ReturnType },
            type, conversion.IsChecked);
    }

    private BoundExpr LowerCoalesce(ICoalesceOperation operation)
    {
        var value = LowerExpr(operation.Value);
        var input = new BoundPatternInput { Type = value.Type };
        var type = MapType(operation.Type, operation.Syntax.GetLocation());
        BoundExpr result = input;
        if (value.Type.NullableElementType != null && value.Type != type)
        {
            if (type.IsReferenceType)
            {
                return UnsupportedExpr(operation);
            }

            var element = ReadNullableValue(input);
            result = type.NullableElementType is { } targetElement
                ? NewNullable(type, NumericConversion(element, targetElement))
                : element;
        }
        return Capture(value, input, new BoundChoice
        {
            Type = type, Condition = HasNullableValue(input), WhenTrue = NumericConversion(result, type),
            WhenFalse = LowerExpr(operation.WhenNull)
        });
    }

    private BoundExpr LowerLiftedUnary(IUnaryOperation unary)
    {
        var value = LowerExpr(unary.Operand);
        var input = new BoundPatternInput { Type = value.Type };
        var type = MapType(unary.Type, unary.Syntax.GetLocation());
        if (type.NullableElementType is not { } element)
        {
            return UnsupportedExpr(unary);
        }

        var operand = ReadNullableValue(input);
        BoundExpr? operation;
        if (unary.OperatorMethod is { } method)
        {
            var mapped = MapUserOperator(method, unary.Syntax);
            operation = mapped == null || mapped.ReturnType != element ? null : OperatorCall(mapped, operand);
        }
        else if (element == _decimal && unary.OperatorKind is UnaryOperatorKind.Minus or UnaryOperatorKind.Plus)
        {
            operation = unary.OperatorKind == UnaryOperatorKind.Plus ? operand : OperatorCall(FindDecimalMethod("op_UnaryNegation")!, operand);
        }
        else
        {
            operation = unary.OperatorKind switch
            {
                UnaryOperatorKind.Plus => operand,
                UnaryOperatorKind.Minus => new BoundUnary { Type = element, Op = TokenKind.Minus, Operand = operand, IsChecked = unary.IsChecked },
                UnaryOperatorKind.Not => new BoundUnary { Type = element, Op = TokenKind.Bang, Operand = operand },
                UnaryOperatorKind.BitwiseNegation => new BoundUnary { Type = element, Op = TokenKind.Tilde, Operand = operand },
                _ => null
            };
        }

        return operation == null ? UnsupportedExpr(unary) : Capture(value, input, new BoundChoice
        {
            Type = type, Condition = HasNullableValue(input), WhenTrue = NewNullable(type, operation), WhenFalse = DefaultValue(type)
        });
    }

    private BoundExpr LowerLiftedBinary(IBinaryOperation operation)
    {
        MethodSymbol? userMethod = null;
        if (operation.OperatorMethod is { } method)
        {
            userMethod = MapUserOperator(method, operation.Syntax);
            if (userMethod == null)
            {
                return UnsupportedExpr(operation);
            }
        }

        var left = LowerExpr(operation.LeftOperand);
        var right = LowerExpr(operation.RightOperand);
        if (left is BoundLiteral { LitKind: LiteralKind.Null } && right.Type.NullableElementType != null)
        {
            left = DefaultValue(right.Type);
        }
        else if (right is BoundLiteral { LitKind: LiteralKind.Null } && left.Type.NullableElementType != null)
        {
            right = DefaultValue(left.Type);
        }

        var type = MapType(operation.Type, operation.Syntax.GetLocation());
        if (userMethod == null && (left.Type.NullableElementType ?? left.Type) == _decimal)
        {
            userMethod = DecimalBinaryMethod(operation.OperatorKind);
        }

        return LiftedBinary(left, right, BinaryToken(operation.OperatorKind), type, operation.IsChecked, userMethod, null);
    }

    // The operands are nullable or plain values. The result is absent when either
    // operand is, except for comparisons and for bool? & and |. With
    // operationType the operands are widened to it and the result is narrowed
    // back to the nullable element, which is how a compound assignment works.
    private BoundExpr LiftedBinary(BoundExpr left, BoundExpr right, TokenKind op, TypeSymbol type, bool isChecked,
        MethodSymbol? userMethod, TypeSymbol? operationType)
    {
        var a = new BoundPatternInput { Type = left.Type };
        var b = new BoundPatternInput { Type = right.Type };
        var first = ReadNullableValue(a);
        var second = ReadNullableValue(b);
        if (operationType != null)
        {
            var isShift = op is TokenKind.LeftShift or TokenKind.RightShift or TokenKind.UnsignedRightShift;
            first = NumericConversion(first, operationType);
            second = NumericConversion(second, isShift ? _int : operationType);
        }

        var comparison = type == _bool;
        BoundExpr combined;
        if (userMethod != null)
        {
            combined = OperatorCall(userMethod, first, second);
        }
        else
        {
            var kind = first.Type.LlvmType switch
            {
                "i1" => comparison ? BinKind.BoolCmp : BinKind.BoolArith,
                "float" => comparison ? BinKind.FloatCmp : BinKind.FloatArith,
                "double" => comparison ? BinKind.DoubleCmp : BinKind.DoubleArith,
                _ => comparison ? BinKind.IntCmp : BinKind.IntArith
            };
            combined = new BoundBinary
            {
                Type = comparison ? _bool : operationType ?? type.NullableElementType ?? first.Type,
                Op = op, Kind = kind, IsChecked = isChecked, IsUnsigned = first.Type.IsUnsignedInteger,
                Left = first, Right = second
            };
        }

        if (operationType != null && type.NullableElementType is { } element)
        {
            combined = NumericConversion(combined, element, isChecked);
        }

        var hasBoth = BoolAnd(HasNullableValue(a), HasNullableValue(b));
        BoundExpr whenMissing = op switch
        {
            TokenKind.EqualsEquals => BoolAnd(Not(HasNullableValue(a)), Not(HasNullableValue(b))),
            TokenKind.BangEquals => Not(BoolAnd(Not(HasNullableValue(a)), Not(HasNullableValue(b)))),
            _ => comparison ? PatternResult(false) : DefaultValue(type)
        };

        // A present false decides bool? &, and a present true decides bool? |,
        // even when the other operand has no value.
        if (type.NullableElementType == _bool && op is TokenKind.Amp or TokenKind.Pipe)
        {
            BoundExpr Decides(BoundPatternInput input) => BoolAnd(HasNullableValue(input),
                op == TokenKind.Amp ? Not(ReadNullableValue(input)) : ReadNullableValue(input));
            whenMissing = new BoundChoice
            {
                Type = type,
                Condition = new BoundBinary
                {
                    Type = _bool, Op = TokenKind.PipePipe, Kind = BinKind.BoolLogic, Left = Decides(a), Right = Decides(b)
                },
                WhenTrue = NewNullable(type, PatternResult(op == TokenKind.Pipe)),
                WhenFalse = whenMissing
            };
        }

        var body = new BoundChoice
        {
            Type = type, Condition = hasBoth,
            WhenTrue = comparison ? combined : NewNullable(type, combined), WhenFalse = whenMissing
        };
        return Capture(left, a, Capture(right, b, body));
    }

    // A user-defined conversion applied to a nullable operand, or giving a
    // nullable result. An absent operand gives an absent result. Converting an
    // absent operand to a non-nullable type throws, as `Value` does.
    private BoundExpr LowerLiftedConversion(IConversionOperation conversion, IMethodSymbol method)
    {
        var value = LowerExpr(conversion.Operand);
        var type = MapType(conversion.Type, conversion.Syntax.GetLocation());
        var mapped = MapUserOperator(method, conversion.Syntax);
        if (mapped == null || mapped.Parameters.Count != 1)
        {
            return UnsupportedExpr(conversion);
        }

        var input = new BoundPatternInput { Type = value.Type };
        var source = ReadNullableValue(input);
        if (source.Type != mapped.Parameters[0].Type)
        {
            return UnsupportedExpr(conversion);
        }

        var call = OperatorCall(mapped, source);
        if (type.NullableElementType is { } element)
        {
            if (call.Type != element)
            {
                return UnsupportedExpr(conversion);
            }

            var wrapped = NewNullable(type, call);
            return value.Type.NullableElementType == null
                ? wrapped
                : Capture(value, input, new BoundChoice
                {
                    Type = type, Condition = HasNullableValue(input), WhenTrue = wrapped, WhenFalse = DefaultValue(type)
                });
        }

        var getter = value.Type.Methods.Find(candidate => candidate.Name == "get_Value");
        if (call.Type != type || getter == null)
        {
            return UnsupportedExpr(conversion);
        }

        var checkedValue = new BoundCall { Method = getter, Receiver = input, Type = getter.ReturnType };
        return Capture(value, input, OperatorCall(mapped, checkedValue));
    }
    // Boxing a nullable struct gives null when it has no value, and otherwise
    // the box of the contained struct. This is the same box a plain struct gets,
    // so unboxing it to either `S` or `S?` works.
    private BoundExpr LowerNullableBoxing(BoundExpr value, TypeSymbol type, SyntaxNode syntax)
    {
        var input = new BoundPatternInput { Type = value.Type };
        return Capture(value, input, new BoundChoice
        {
            Type = type, Condition = HasNullableValue(input),
            WhenTrue = new BoundConversion { Operand = BoxValue(ReadNullableValue(input), syntax), Type = type },
            WhenFalse = DefaultValue(type)
        });
    }

    // `(S?)obj` unboxes a struct box to a present value and null to an absent
    // one. With tryCast, as in `obj as S?`, an object of another type gives an
    // absent value instead of an InvalidCastException.
    private BoundExpr LowerNullableUnboxing(BoundExpr value, TypeSymbol type, bool tryCast)
    {
        var element = type.NullableElementType!;
        var input = new BoundPatternInput { Type = value.Type };
        var unboxed = NewNullable(type, new BoundUnbox { Operand = input, BoxedValue = StructBox(element), Type = element });
        BoundExpr condition = tryCast
            ? new BoundIs { Operand = input, TestType = StructBox(element).Owner, Type = _bool }
            : PatternNullCheck(input, TokenKind.BangEquals);
        return Capture(value, input, new BoundChoice
        {
            Type = type, Condition = condition, WhenTrue = unboxed, WhenFalse = DefaultValue(type)
        });
    }
    private MethodSymbol? MapUserOperator(IMethodSymbol method, SyntaxNode syntax)
    {
        var mapped = MapMethod(method, syntax.GetLocation());
        return mapped == null || mapped.IsAbstract || !mapped.IsStatic
            || mapped.Parameters.Any(parameter => parameter.RefKind != ArcSharp.Syntax.RefKind.None)
            ? null
            : mapped;
    }

    private static BoundExpr OperatorCall(MethodSymbol method, params BoundExpr[] arguments) =>
        new BoundCall { Method = method, Arguments = [.. arguments], Type = method.ReturnType };

    // `x op= y` where x is nullable. The target is evaluated once, and the
    // operation is the lifted form of the binary operator.
    private BoundExpr LowerLiftedCompound(ICompoundAssignmentOperation assignment, BoundExpr target)
    {
        if (target.Type.NullableElementType is not { } element
            || assignment.InConversion.MethodSymbol != null || assignment.OutConversion.MethodSymbol != null)
        {
            return UnsupportedExpr(assignment);
        }

        MethodSymbol? userMethod = null;
        if (assignment.OperatorMethod is { } method)
        {
            userMethod = MapUserOperator(method, assignment.Syntax);
            if (userMethod == null || userMethod.ReturnType != element)
            {
                return UnsupportedExpr(assignment);
            }
        }

        var value = LowerExpr(assignment.Value);
        if (value is BoundLiteral { LitKind: LiteralKind.Null })
        {
            value = DefaultValue(target.Type);
        }

        var op = BinaryToken(assignment.OperatorKind);
        if (userMethod == null && element == _decimal)
        {
            userMethod = DecimalBinaryMethod(assignment.OperatorKind);
        }

        TypeSymbol? operationType = null;
        if (userMethod == null)
        {
            var operand = value.Type.NullableElementType ?? value.Type;
            var supported = (element.IsInteger || element.IsFloat || element == _bool)
                && (operand.IsInteger || operand.IsFloat || operand == _bool);
            if (!supported)
            {
                return UnsupportedExpr(assignment);
            }

            var isShift = op is TokenKind.LeftShift or TokenKind.RightShift or TokenKind.UnsignedRightShift;
            operationType = isShift
                ? (element == _uint || element == _long || element == _ulong ? element : _int)
                : ArithmeticType(element, operand);
        }

        var input = new BoundPatternInput { Type = target.Type };
        var result = LiftedBinary(input, value, op, target.Type, assignment.IsChecked, userMethod, operationType);
        return new BoundOperatorUpdate { Target = target, Type = target.Type, Input = input, Result = result };
    }

    // `x++` and `x--` where x is nullable.
    private BoundExpr LowerLiftedIncrement(IIncrementOrDecrementOperation operation, BoundExpr target)
    {
        if (target.Type.NullableElementType is not { } element)
        {
            return UnsupportedExpr(operation);
        }

        var input = new BoundPatternInput { Type = target.Type };
        var current = ReadNullableValue(input);
        BoundExpr? updated = null;
        if (operation.OperatorMethod is { } method)
        {
            var mapped = MapUserOperator(method, operation.Syntax);
            updated = mapped == null || mapped.ReturnType != element ? null : OperatorCall(mapped, current);
        }
        else if (element == _decimal)
        {
            updated = OperatorCall(FindDecimalMethod(operation.Kind == OperationKind.Decrement ? "op_Decrement" : "op_Increment")!, current);
        }
        else if (element.IsInteger || element.IsFloat)
        {
            updated = new BoundBinary
            {
                Type = element, Op = operation.Kind == OperationKind.Decrement ? TokenKind.Minus : TokenKind.Plus,
                Kind = element == _float ? BinKind.FloatArith : element == _double ? BinKind.DoubleArith : BinKind.IntArith,
                IsChecked = operation.IsChecked, IsUnsigned = element.IsUnsignedInteger,
                Left = current, Right = One(element)
            };
        }

        if (updated == null)
        {
            return UnsupportedExpr(operation);
        }

        var result = new BoundChoice
        {
            Type = target.Type, Condition = HasNullableValue(input), WhenTrue = NewNullable(target.Type, updated),
            WhenFalse = DefaultValue(target.Type)
        };
        return new BoundOperatorUpdate
        {
            Target = target, Type = target.Type, Input = input, Result = result, IsPostfix = operation.IsPostfix
        };
    }

    private BoundLiteral One(TypeSymbol type)
    {
        var kind = type switch
        {
            _ when type == _float => LiteralKind.Float,
            _ when type == _double => LiteralKind.Double,
            _ when type == _long => LiteralKind.Long,
            _ when type == _ulong => LiteralKind.ULong,
            _ when type == _uint => LiteralKind.UInt,
            _ => LiteralKind.Int
        };
        return new BoundLiteral { Type = type, LitKind = kind, IntValue = 1, FloatValue = 1 };
    }
    private BoundExpr NewNullable(TypeSymbol type, BoundExpr value) => new BoundNewObject
    {
        Type = type, Ctor = type.Methods.Find(method => method.IsConstructor), Arguments = [value]
    };

    private BoundExpr HasNullableValue(BoundExpr input) => input.Type.NullableElementType != null
        ? new BoundFieldAccess { Receiver = input, Field = input.Type.InstanceFields[0], Type = _bool }
        : input.Type.IsReferenceType ? PatternNullCheck(input, TokenKind.BangEquals) : PatternResult(true);

    private static BoundExpr ReadNullableValue(BoundExpr input) => input.Type.NullableElementType is { } element
        ? new BoundFieldAccess { Receiver = input, Field = input.Type.InstanceFields[1], Type = element }
        : input;

    private static BoundExpr Capture(BoundExpr value, BoundPatternInput input, BoundExpr body) =>
        new BoundPatternTest { Value = value, Input = input, Test = body, Type = body.Type };

    private BoundExpr NumericConversion(BoundExpr value, TypeSymbol type, bool isChecked = false)
    {
        if (value.Type == type)
        {
            return value;
        }

        if (value.Type == _decimal || type == _decimal)
        {
            return DecimalConversion(value, type) ?? new BoundConversion { Operand = value, Type = type, IsChecked = isChecked };
        }

        return new BoundConversion { Operand = value, Type = type, IsChecked = isChecked };
    }

    private BoundExpr BoolAnd(BoundExpr left, BoundExpr right) =>
        new BoundBinary { Type = _bool, Op = TokenKind.AmpAmp, Kind = BinKind.BoolLogic, Left = left, Right = right };

    private BoundExpr Not(BoundExpr operand) =>
        new BoundUnary { Type = _bool, Op = TokenKind.Bang, Operand = operand };
}
