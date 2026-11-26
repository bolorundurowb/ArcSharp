using ArcSharp.Binding;
using ArcSharp.Diagnostics;
using ArcSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace ArcSharp.Frontend;

sealed partial class RoslynLowering
{
    private static BoundStringOp StringOperation(StringOp op, TypeSymbol type, BoundExpr? receiver, List<BoundExpr> arguments) =>
        new() { Op = op, Type = type, Receiver = receiver, Arguments = arguments };

    private BoundExpr? LowerStringCall(IInvocationOperation call)
    {
        var target = call.TargetMethod;
        var owner = target.ContainingType.SpecialType;
        if (target.Name == "ToString" && target.Parameters.Length == 0 && call.Instance is { } instance
            && (owner == SpecialType.System_String || IsTextPrimitive(owner)))
        {
            if (owner is SpecialType.System_Single or SpecialType.System_Double)
            {
                return StringOperation(StringOp.Format, _string, LowerExpr(instance), [DefaultValue(_string)]);
            }

            return StringOperation(StringOp.ToString, _string, LowerExpr(instance), []);
        }

        if (target.Name == "ToString" && target.Parameters.Length == 1 && call.Instance is { } number
            && IsFormattedNumber(owner))
        {
            return StringOperation(StringOp.Format, _string, LowerExpr(number), [LowerExpr(call.Arguments[0].Value)]);
        }

        if (owner != SpecialType.System_String)
        {
            return null;
        }

        if (target.Name == "Format")
        {
            return LowerCompositeFormat(call);
        }

        var character = target.Parameters.FirstOrDefault()?.Type.SpecialType == SpecialType.System_Char;
        var member = target.Name switch
        {
            "Equals" when target.Parameters.All(parameter => parameter.Type.SpecialType == SpecialType.System_String)
                => (StringOp.Equals, _bool),
            "Substring" => (StringOp.Substring, _string),
            "Contains" => (character ? StringOp.ContainsChar : StringOp.Contains, _bool),
            "IndexOf" => (character ? StringOp.IndexOfChar : StringOp.IndexOf, _int),
            "StartsWith" => (StringOp.StartsWith, _bool),
            "EndsWith" => (StringOp.EndsWith, _bool),
            "IsNullOrEmpty" => (StringOp.IsNullOrEmpty, _bool),
            "IsNullOrWhiteSpace" => (StringOp.IsNullOrWhiteSpace, _bool),
            "Trim" => (StringOp.Trim, _string),
            "Replace" => (character ? StringOp.ReplaceChar : StringOp.Replace, _string),
            "Concat" when target.Parameters.All(parameter => parameter.Type.SpecialType == SpecialType.System_String)
                => (StringOp.Concat, _string),
            _ => (StringOp.ToString, _void)
        };
        if (member.Item2 == _void)
        {
            return null;
        }

        var receiver = call.Instance == null ? null : LowerExpr(call.Instance);
        return StringOperation(member.Item1, member.Item2, receiver, LowerArguments(call.Arguments, new MethodSymbol
        {
            Name = target.Name, Owner = _string, ReturnType = member.Item2
        }));
    }

    private static bool IsTextPrimitive(SpecialType type) => type is
        SpecialType.System_Boolean or SpecialType.System_Char or
        SpecialType.System_SByte or SpecialType.System_Byte or
        SpecialType.System_Int16 or SpecialType.System_UInt16 or
        SpecialType.System_Int32 or SpecialType.System_UInt32 or
        SpecialType.System_Int64 or SpecialType.System_UInt64 or
        SpecialType.System_Single or SpecialType.System_Double;

    private static bool IsFormattedNumber(SpecialType type) =>
        IsTextPrimitive(type) && type is not (SpecialType.System_Boolean or SpecialType.System_Char);

    private BoundExpr LowerCompositeFormat(IInvocationOperation call)
    {
        var values = new List<BoundExpr>();
        foreach (var argument in call.Arguments)
        {
            if (argument.Parameter?.Ordinal == 0)
            {
                var pattern = LowerExpr(argument.Value);
                values.Add(new BoundNamedArgument { Ordinal = 0, Value = pattern, Type = pattern.Type });
                continue;
            }

            if (argument.Value is not IArrayCreationOperation { Initializer: { } initializer })
            {
                Report(argument.Syntax, "string.Format requires individual arguments or an inline argument array",
                    DiagnosticCatalog.UnsupportedConstructId);
                return ErrorExpr();
            }

            var ordinal = 1;
            foreach (var element in initializer.ElementValues)
            {
                var value = UnwrapBoxing(element);
                if (value.Type is { SpecialType: not SpecialType.System_String } type
                    && !IsTextPrimitive(type.SpecialType) && value.ConstantValue is not { HasValue: true, Value: null })
                {
                    Report(element.Syntax, "string.Format arguments must be strings, integral or floating-point values, bool, char, or null",
                        DiagnosticCatalog.UnsupportedConstructId);
                    return ErrorExpr();
                }

                var lowered = LowerExpr(value);
                values.Add(new BoundNamedArgument { Ordinal = ordinal++, Value = lowered, Type = lowered.Type });
            }
        }

        return StringOperation(StringOp.CompositeFormat, _string, null, values);
    }

    private BoundExpr LowerInterpolatedString(IInterpolatedStringOperation operation)
    {
        var parts = new List<BoundExpr>();
        foreach (var part in operation.Parts)
        {
            if (part is IInterpolatedStringTextOperation text)
            {
                parts.Add(LowerExpr(text.Text));
                continue;
            }

            if (part is not IInterpolationOperation hole)
            {
                Report(part.Syntax, "unsupported string interpolation", DiagnosticCatalog.UnsupportedConstructId);
                return ErrorExpr();
            }

            var value = LowerExpr(hole.Expression);
            if (IsFormattedNumber(hole.Expression.Type?.SpecialType ?? SpecialType.None))
            {
                value = StringOperation(StringOp.Format, _string, value,
                    [hole.FormatString == null ? DefaultValue(_string) : LowerExpr(hole.FormatString)]);
            }
            else
            {
                if (hole.FormatString != null && value.Type != _string && value.Type != _bool && value.Type != _char && value.Type != _null)
                {
                    Report(hole.Syntax, "this interpolation format is only supported for integral and floating-point values",
                        DiagnosticCatalog.UnsupportedConstructId);
                    return ErrorExpr();
                }

                value = StringOperand(value, hole.Expression.Syntax);
            }

            parts.Add(StringOperation(StringOp.Align, _string, value,
                [hole.Alignment == null ? new BoundLiteral { Type = _int, LitKind = LiteralKind.Int } : LowerExpr(hole.Alignment)]));
        }

        return JoinText(parts, operation.Syntax);
    }
}
