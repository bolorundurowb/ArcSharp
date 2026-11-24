using ArcSharp.Binding;
using ArcSharp.Lexing;
using ArcSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using BoundKind = ArcSharp.Binding.TypeKind;

namespace ArcSharp.Frontend;

sealed partial class RoslynLowering
{
    private TypeSymbol? _decimal;

    // `decimal` is a struct of two long slots whose operators are small methods
    // around one runtime call each. The runtime does the arithmetic on 256-bit
    // integers, because the emitted code has no 128-bit type to work with.
    private TypeSymbol GetDecimalType()
    {
        if (_decimal != null)
        {
            return _decimal;
        }

        var type = new TypeSymbol { Name = "decimal", Kind = BoundKind.Struct };
        _decimal = type;
        List<FieldSymbol> fields =
        [
            new FieldSymbol { Name = "low", Type = _long, Owner = type },
            new FieldSymbol { Name = "high", Type = _long, Owner = type }
        ];
        List<MethodSymbol> members = [];

        MethodSymbol Add(string name, TypeSymbol result, int arity, Func<List<BoundExpr>, BoundExpr> body)
        {
            var parameters = Enumerable.Range(0, arity)
                .Select(index => new ParamSymbol { Name = "value" + index, Type = type, Index = index })
                .ToList();
            var method = new MethodSymbol
            {
                Name = name, Owner = type, ReturnType = result, Parameters = parameters,
                IsPublic = true, IsStatic = name != "ToString"
            };
            members.Add(method);
            var arguments = parameters.Select(parameter => (BoundExpr)new BoundParam { Symbol = parameter, Type = type }).ToList();
            _readyBodies.Add(new BoundMethodBody
            {
                Method = method,
                Body = new BoundBlock { Statements = [new BoundReturn { Value = body(arguments) }] }
            });
            return method;
        }

        BoundExpr Operation(DecimalOp op, params BoundExpr[] arguments) => new BoundDecimalOp
        {
            Op = op, Arguments = [.. arguments], Type = type
        };

        BoundExpr One() => new BoundDecimalOp { Op = DecimalOp.Constant, Low = 1, Type = type };

        Add("op_Addition", type, 2, arguments => Operation(DecimalOp.Add, arguments[0], arguments[1]));
        Add("op_Subtraction", type, 2, arguments => Operation(DecimalOp.Subtract, arguments[0], arguments[1]));
        Add("op_Multiply", type, 2, arguments => Operation(DecimalOp.Multiply, arguments[0], arguments[1]));
        Add("op_Division", type, 2, arguments => Operation(DecimalOp.Divide, arguments[0], arguments[1]));
        Add("op_Modulus", type, 2, arguments => Operation(DecimalOp.Remainder, arguments[0], arguments[1]));
        Add("op_UnaryNegation", type, 1, arguments => Operation(DecimalOp.Negate, arguments[0]));
        Add("op_Increment", type, 1, arguments => Operation(DecimalOp.Add, arguments[0], One()));
        Add("op_Decrement", type, 1, arguments => Operation(DecimalOp.Subtract, arguments[0], One()));
        Add("op_Equality", _bool, 2, arguments => DecimalComparison(TokenKind.EqualsEquals, arguments));
        Add("op_Inequality", _bool, 2, arguments => DecimalComparison(TokenKind.BangEquals, arguments));
        Add("op_LessThan", _bool, 2, arguments => DecimalComparison(TokenKind.Less, arguments));
        Add("op_LessThanOrEqual", _bool, 2, arguments => DecimalComparison(TokenKind.LessEquals, arguments));
        Add("op_GreaterThan", _bool, 2, arguments => DecimalComparison(TokenKind.Greater, arguments));
        Add("op_GreaterThanOrEqual", _bool, 2, arguments => DecimalComparison(TokenKind.GreaterEquals, arguments));
        type.ToStringOverride = Add("ToString", _string, 0, _ => new BoundDecimalOp
        {
            Op = DecimalOp.ToText, Arguments = [new BoundThis { Type = type }], Type = _string
        });

        FinishLayout(type, fields, members);
        return type;
    }

    private BoundExpr DecimalComparison(TokenKind op, List<BoundExpr> arguments) => new BoundBinary
    {
        Op = op, Kind = BinKind.IntCmp, Type = _bool,
        Left = new BoundDecimalOp { Op = DecimalOp.Compare, Arguments = arguments, Type = _int },
        Right = new BoundLiteral { Type = _int, LitKind = LiteralKind.Int }
    };

    private MethodSymbol? DecimalBinaryMethod(BinaryOperatorKind kind) => FindDecimalMethod(kind switch
    {
        BinaryOperatorKind.Add => "op_Addition",
        BinaryOperatorKind.Subtract => "op_Subtraction",
        BinaryOperatorKind.Multiply => "op_Multiply",
        BinaryOperatorKind.Divide => "op_Division",
        BinaryOperatorKind.Remainder => "op_Modulus",
        BinaryOperatorKind.Equals => "op_Equality",
        BinaryOperatorKind.NotEquals => "op_Inequality",
        BinaryOperatorKind.LessThan => "op_LessThan",
        BinaryOperatorKind.LessThanOrEqual => "op_LessThanOrEqual",
        BinaryOperatorKind.GreaterThan => "op_GreaterThan",
        BinaryOperatorKind.GreaterThanOrEqual => "op_GreaterThanOrEqual",
        _ => ""
    });

    private MethodSymbol? FindDecimalMethod(string name) =>
        GetDecimalType().Methods.Find(candidate => candidate.Name == name);

    private BoundExpr DecimalConstant(decimal value)
    {
        var bits = decimal.GetBits(value);
        var scale = (bits[3] >> 16) & 0xFF;
        var negative = bits[3] < 0;
        var low = (uint)bits[0] | ((ulong)(uint)bits[1] << 32);
        var high = (uint)bits[2] | ((ulong)scale << 32) | (negative ? 1UL << 63 : 0UL);
        return new BoundDecimalOp
        {
            Op = DecimalOp.Constant, Low = unchecked((long)low), High = unchecked((long)high), Type = GetDecimalType()
        };
    }

    // Converts between decimal and a built-in numeric type. Null means the
    // pair has no conversion here, such as decimal to an enum.
    private BoundExpr? DecimalConversion(BoundExpr value, TypeSymbol type)
    {
        var decimalType = GetDecimalType();
        if (type == decimalType)
        {
            if (value.Type == _ulong)
            {
                return new BoundDecimalOp { Op = DecimalOp.FromUnsigned, Arguments = [value], Type = type };
            }

            if (value.Type.IsInteger)
            {
                return new BoundDecimalOp
                {
                    Op = DecimalOp.FromInteger, Arguments = [NumericConversion(value, _long)], Type = type
                };
            }

            if (value.Type.IsFloat)
            {
                return new BoundDecimalOp
                {
                    Op = DecimalOp.FromDouble, Arguments = [NumericConversion(value, _double)], Type = type
                };
            }

            return null;
        }

        if (type == _ulong)
        {
            return new BoundDecimalOp { Op = DecimalOp.ToUnsigned, Arguments = [value], Type = type };
        }

        if (type.IsFloat)
        {
            var number = new BoundDecimalOp { Op = DecimalOp.ToDouble, Arguments = [value], Type = _double };
            return NumericConversion(number, type);
        }

        var range = IntegerRange(type);
        if (range == null)
        {
            return null;
        }

        var integer = new BoundDecimalOp
        {
            Op = DecimalOp.ToInteger, Arguments = [value], Low = range.Value.Min, High = range.Value.Max, Type = _long
        };
        return NumericConversion(integer, type);
    }

    private (long Min, long Max)? IntegerRange(TypeSymbol type)
    {
        if (type == _sbyte)
        {
            return (sbyte.MinValue, sbyte.MaxValue);
        }

        if (type == _byte)
        {
            return (byte.MinValue, byte.MaxValue);
        }

        if (type == _short)
        {
            return (short.MinValue, short.MaxValue);
        }

        if (type == _ushort || type == _char)
        {
            return (ushort.MinValue, ushort.MaxValue);
        }

        if (type == _int)
        {
            return (int.MinValue, int.MaxValue);
        }

        if (type == _uint)
        {
            return (uint.MinValue, uint.MaxValue);
        }

        return type == _long ? (long.MinValue, long.MaxValue) : null;
    }
}
