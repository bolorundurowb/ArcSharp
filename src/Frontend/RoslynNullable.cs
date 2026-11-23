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
        if (type.NullableElementType is not { } element || unary.OperatorMethod != null)
        {
            return UnsupportedExpr(unary);
        }

        var operand = ReadNullableValue(input);
        BoundExpr? operation = unary.OperatorKind switch
        {
            UnaryOperatorKind.Plus => operand,
            UnaryOperatorKind.Minus => new BoundUnary { Type = element, Op = TokenKind.Minus, Operand = operand, IsChecked = unary.IsChecked },
            UnaryOperatorKind.Not => new BoundUnary { Type = element, Op = TokenKind.Bang, Operand = operand },
            UnaryOperatorKind.BitwiseNegation => new BoundUnary { Type = element, Op = TokenKind.Tilde, Operand = operand },
            _ => null
        };
        return operation == null ? UnsupportedExpr(unary) : Capture(value, input, new BoundChoice
        {
            Type = type, Condition = HasNullableValue(input), WhenTrue = NewNullable(type, operation), WhenFalse = DefaultValue(type)
        });
    }

    private BoundExpr LowerLiftedBinary(IBinaryOperation operation)
    {
        if (operation.OperatorMethod != null)
        {
            return UnsupportedExpr(operation);
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

        var a = new BoundPatternInput { Type = left.Type };
        var b = new BoundPatternInput { Type = right.Type };
        var first = ReadNullableValue(a);
        var second = ReadNullableValue(b);
        var type = MapType(operation.Type, operation.Syntax.GetLocation());
        var comparison = type == _bool;
        var op = BinaryToken(operation.OperatorKind);
        var kind = first.Type.LlvmType switch
        {
            "i1" => comparison ? BinKind.BoolCmp : BinKind.BoolArith,
            "float" => comparison ? BinKind.FloatCmp : BinKind.FloatArith,
            "double" => comparison ? BinKind.DoubleCmp : BinKind.DoubleArith,
            _ => comparison ? BinKind.IntCmp : BinKind.IntArith
        };
        var combined = new BoundBinary
        {
            Type = comparison ? _bool : type.NullableElementType ?? first.Type,
            Op = op, Kind = kind, IsChecked = operation.IsChecked, IsUnsigned = first.Type.IsUnsignedInteger,
            Left = first, Right = second
        };
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

    private static BoundExpr NumericConversion(BoundExpr value, TypeSymbol type, bool isChecked = false) =>
        value.Type == type ? value : new BoundConversion { Operand = value, Type = type, IsChecked = isChecked };

    private BoundExpr BoolAnd(BoundExpr left, BoundExpr right) =>
        new BoundBinary { Type = _bool, Op = TokenKind.AmpAmp, Kind = BinKind.BoolLogic, Left = left, Right = right };

    private BoundExpr Not(BoundExpr operand) =>
        new BoundUnary { Type = _bool, Op = TokenKind.Bang, Operand = operand };
}
