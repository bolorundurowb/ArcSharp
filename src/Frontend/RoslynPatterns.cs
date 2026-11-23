using ArcSharp.Binding;
using ArcSharp.Lexing;
using ArcSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using BoundKind = ArcSharp.Binding.TypeKind;

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

    private BoundExpr LowerPatternComparison(IOperation pattern, BoundPatternInput input, IOperation value, TokenKind op)
    {
        var type = input.Type;
        if (!value.ConstantValue.HasValue)
        {
            return UnsupportedExpr(pattern);
        }

        var constantValue = value.ConstantValue.Value;
        if (constantValue == null)
        {
            return type.IsReferenceType ? PatternNullCheck(input, op) : PatternResult(false);
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
            if (type.Kind == BoundKind.Struct && input.Type.IsReferenceType)
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
        if (type == input.Type || type == _object)
        {
            return input.Type.IsReferenceType ? PatternNullCheck(input, TokenKind.BangEquals) : PatternResult(true);
        }

        if (input.Type.IsReferenceType && type.Kind is BoundKind.Class or BoundKind.Struct)
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

    private BoundExpr PatternNullCheck(BoundPatternInput input, TokenKind op) => new BoundBinary
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
