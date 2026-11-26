using ArcSharp.Binding;
using ArcSharp.Diagnostics;
using ArcSharp.Lexing;
using BoundKind = ArcSharp.Binding.TypeKind;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace ArcSharp.Frontend;

// The collections in corelib/Collections compare and hash their elements through
// ElementEquality. Each call is lowered for the closed element type, so the
// comparison is the one that type needs. A class with no override compares by
// reference, and a struct must declare its own Equals and GetHashCode.
sealed partial class RoslynLowering
{
    private readonly HashSet<string> _reportedElements = [];

    private static bool IsElementEquality(IMethodSymbol target) =>
        target.ContainingType.Name == "ElementEquality"
        && target.ContainingNamespace.ToDisplayString() == "System.Collections.Generic";

    private BoundExpr LowerElementCall(IInvocationOperation call)
    {
        var element = MapType(call.TargetMethod.TypeArguments[0], call.Syntax.GetLocation());
        if (element == _error)
        {
            return ErrorExpr();
        }

        var first = LowerExpr(call.Arguments[0].Value);
        if (call.TargetMethod.Name == "AreEqual")
        {
            return ElementEquals(first, LowerExpr(call.Arguments[1].Value), element, call.Syntax);
        }

        return ElementHash(first, element, call.Syntax);
    }

    private BoundExpr ElementEquals(BoundExpr left, BoundExpr right, TypeSymbol type, SyntaxNode syntax)
    {
        if (type == _string)
        {
            return new BoundBinary { Type = _bool, Op = TokenKind.EqualsEquals, Kind = BinKind.StrEq, Left = left, Right = right };
        }

        if (type.IsFloat || type == _decimal || type.IsDelegate)
        {
            return UnsupportedElement(syntax, type);
        }

        if (type == _bool || type.IsInteger || type.Kind == BoundKind.Enum)
        {
            return ValueEquals(left, right, type);
        }

        if (type.Kind == BoundKind.Array)
        {
            return RefCompare(left, right, TokenKind.EqualsEquals);
        }

        if (type == _object || !_sources.TryGetValue(type, out var source))
        {
            return UnsupportedElement(syntax, type);
        }

        if (type.Kind == BoundKind.Class && IsRecordClass(source))
        {
            return ValueEquals(left, right, type);
        }

        var equals = ElementMethod(source, "Equals", method => method.Parameters.Length == 1
            && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type.OriginalDefinition, source.OriginalDefinition));

        if (type.Kind == BoundKind.Struct)
        {
            return equals == null
                ? MissingElementMethod(syntax, type, $"Equals({type.Name})")
                : BindMemberCall(left, equals, [right]);
        }

        if (type.Kind != BoundKind.Class)
        {
            return UnsupportedElement(syntax, type);
        }

        equals ??= ElementMethod(source, "Equals", method => method.IsOverride && method.Parameters.Length == 1
            && method.Parameters[0].Type.SpecialType == SpecialType.System_Object);
        if (equals == null)
        {
            return RefCompare(left, right, TokenKind.EqualsEquals);
        }

        // A null left operand equals only a null right operand, as EqualityComparer<T>.Default does.
        var leftInput = new BoundPatternInput { Type = type };
        var rightInput = new BoundPatternInput { Type = type };
        var argument = equals.Parameters[0].Type == _object
            ? (BoundExpr)new BoundConversion { Operand = rightInput, Type = _object }
            : rightInput;
        var comparison = new BoundChoice
        {
            Condition = RefCompare(leftInput, NullLiteral(), TokenKind.EqualsEquals),
            WhenTrue = RefCompare(rightInput, NullLiteral(), TokenKind.EqualsEquals),
            WhenFalse = BindMemberCall(leftInput, equals, [argument]),
            Type = _bool
        };
        return Capture(left, leftInput, Capture(right, rightInput, comparison));
    }

    private BoundExpr ElementHash(BoundExpr value, TypeSymbol type, SyntaxNode syntax)
    {
        if (type == _string)
        {
            // The runtime hashes a null string to zero, so the hash needs no null check here.
            return StringOperation(StringOp.Hash, _int, null, [value]);
        }

        if (type.IsFloat || type == _decimal || type.IsDelegate)
        {
            return UnsupportedElement(syntax, type);
        }

        if (type == _long || type == _ulong)
        {
            return LongHash(value, type);
        }

        if (type == _bool || type.IsInteger || type.Kind == BoundKind.Enum)
        {
            return HashOf(value, type) ?? UnsupportedElement(syntax, type);
        }

        if (type.Kind == BoundKind.Array)
        {
            return NullSafeHash(value, type, input => new BoundIdentityHash { Operand = input, Type = _int });
        }

        if (type == _object || !_sources.TryGetValue(type, out var source))
        {
            return UnsupportedElement(syntax, type);
        }

        var hash = ElementMethod(source, "GetHashCode", method => method.IsOverride && method.Parameters.Length == 0);

        if (type.Kind == BoundKind.Struct)
        {
            return hash == null
                ? MissingElementMethod(syntax, type, "GetHashCode()")
                : BindMemberCall(value, hash, []);
        }

        if (type.Kind != BoundKind.Class)
        {
            return UnsupportedElement(syntax, type);
        }

        if (IsRecordClass(source))
        {
            return HashOf(value, type) ?? UnsupportedElement(syntax, type);
        }

        return NullSafeHash(value, type, input => hash == null
            ? new BoundIdentityHash { Operand = input, Type = _int }
            : BindMemberCall(input, hash, []));
    }

    // The fold keeps the high half of a 64-bit value, so keys that differ only
    // above bit 31 do not share a bucket. The value is captured once.
    private BoundExpr LongHash(BoundExpr value, TypeSymbol type)
    {
        var input = new BoundPatternInput { Type = type };
        var shifted = new BoundBinary
        {
            Type = type, Op = TokenKind.RightShift, Kind = BinKind.LongArith, IsUnsigned = type.IsUnsignedInteger,
            Left = input, Right = Number(32)
        };
        var folded = new BoundBinary { Type = type, Op = TokenKind.Caret, Kind = BinKind.LongArith, Left = input, Right = shifted };
        return Capture(value, input, NumericConversion(folded, _int));
    }

    // Hashes a nullable reference as zero, the same value a null element gets.
    private BoundExpr NullSafeHash(BoundExpr value, TypeSymbol type, Func<BoundExpr, BoundExpr> hashOfInput)
    {
        var input = new BoundPatternInput { Type = type };
        return Capture(value, input, new BoundChoice
        {
            Condition = RefCompare(input, NullLiteral(), TokenKind.EqualsEquals),
            WhenTrue = Number(0),
            WhenFalse = hashOfInput(input),
            Type = _int
        });
    }

    // The matching method that the type or a class it derives from declares.
    // Object's and ValueType's own members are not searched, so a missing
    // override yields null.
    private MethodSymbol? ElementMethod(INamedTypeSymbol source, string name, Func<IMethodSymbol, bool> match)
    {
        for (var current = source; current != null
            && current.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType);
            current = current.BaseType)
        {
            var found = current.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(match);
            if (found != null)
            {
                return MapMethod(found, source.Locations.FirstOrDefault());
            }
        }

        return null;
    }

    private BoundExpr UnsupportedElement(SyntaxNode syntax, TypeSymbol type) =>
        ReportElement(syntax, $"collection element type '{type.Name}' is not supported");

    private BoundExpr MissingElementMethod(SyntaxNode syntax, TypeSymbol type, string signature) =>
        ReportElement(syntax, $"struct element type '{type.Name}' must declare {signature} to be used in a collection");

    // The same message from every instantiation of a collection method is reported once.
    private BoundExpr ReportElement(SyntaxNode syntax, string message)
    {
        if (_reportedElements.Add(message))
        {
            Report(syntax, message, DiagnosticCatalog.UnsupportedConstructId);
        }

        return ErrorExpr();
    }
}
