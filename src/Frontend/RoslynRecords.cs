using ArcSharp.Binding;
using ArcSharp.Diagnostics;
using ArcSharp.Lexing;
using ArcSharp.Syntax;
using BoundKind = ArcSharp.Binding.TypeKind;
using RoslynKind = Microsoft.CodeAnalysis.TypeKind;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace ArcSharp.Frontend;

// Record classes. Roslyn declares the members a record synthesises but gives
// them no bodies, so the bodies are written here as bound trees. Some members
// are not lowered at all: EqualityContract, PrintMembers, the copy constructor,
// <Clone>$ and Equals(object) need library types or reflection, and nothing
// calls them once equality, hashing, formatting and `with` are written directly.
sealed partial class RoslynLowering
{
    private enum RecordMember
    {
        None,
        Skip,
        Equals,
        SealedEquals,
        GetHashCode,
        ToString,
        EqualityOperator,
        InequalityOperator,
        Deconstruct
    }

    private static bool IsRecordClass(ITypeSymbol? type) =>
        type is INamedTypeSymbol { IsRecord: true, TypeKind: RoslynKind.Class };

    private static RecordMember ClassifyRecordMember(IMethodSymbol method)
    {
        if (!method.IsImplicitlyDeclared || method.ContainingType is not { IsRecord: true } type)
        {
            return RecordMember.None;
        }

        switch (method.MethodKind)
        {
            case MethodKind.Constructor:
                return method.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, type)
                    ? RecordMember.Skip
                    : RecordMember.None;
            case MethodKind.UserDefinedOperator:
                return method.Name switch
                {
                    "op_Equality" => RecordMember.EqualityOperator,
                    "op_Inequality" => RecordMember.InequalityOperator,
                    _ => RecordMember.None
                };
            case MethodKind.Ordinary:
                switch (method.Name)
                {
                    case "<Clone>$":
                    case "PrintMembers":
                        return RecordMember.Skip;
                    case "Equals" when method.Parameters.Length == 1:
                        if (method.Parameters[0].Type.SpecialType == SpecialType.System_Object)
                        {
                            return RecordMember.Skip;
                        }

                        return SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, type)
                            ? RecordMember.Equals
                            : RecordMember.SealedEquals;
                    case "GetHashCode":
                        return RecordMember.GetHashCode;
                    case "ToString":
                        return RecordMember.ToString;
                    case "Deconstruct":
                        return RecordMember.Deconstruct;
                }

                break;
        }

        return RecordMember.None;
    }

    private static bool IsPrimaryConstructor(IMethodSymbol method) =>
        method is { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: false, ContainingType.IsRecord: true }
        && method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is RecordDeclarationSyntax;

    private bool TryLowerRecordBody(MethodSymbol method, IMethodSymbol roslyn, BoundBlock block)
    {
        if (roslyn.ContainingType is not { IsRecord: true })
        {
            return false;
        }

        if (IsPrimaryConstructor(roslyn))
        {
            LowerPrimaryConstructor(block, method, roslyn);
            return true;
        }

        BoundExpr? value = ClassifyRecordMember(roslyn) switch
        {
            RecordMember.Equals => RecordEquals(method, roslyn),
            RecordMember.SealedEquals => RecordSealedEquals(method, roslyn),
            RecordMember.GetHashCode => RecordHash(method, roslyn),
            RecordMember.ToString => RecordText(method, roslyn),
            RecordMember.EqualityOperator => RecordEqualityOperator(method, roslyn),
            RecordMember.InequalityOperator => new BoundUnary
            {
                Op = TokenKind.Bang, Operand = RecordEqualityOperator(method, roslyn), Type = _bool
            },
            _ => null
        };
        if (value != null)
        {
            block.Statements.Add(new BoundReturn { Value = value });
            return true;
        }

        if (ClassifyRecordMember(roslyn) == RecordMember.Deconstruct)
        {
            LowerDeconstruct(block, method);
            return true;
        }

        return false;
    }

    // Parameters are stored first, then field initialisers run, then the base
    // constructor, which matches the order the C# compiler uses for a record.
    private void LowerPrimaryConstructor(BoundBlock block, MethodSymbol method, IMethodSymbol roslyn)
    {
        var declaration = (RecordDeclarationSyntax)roslyn.DeclaringSyntaxReferences.First().GetSyntax();
        var type = method.Owner;
        foreach (var parameter in method.Parameters)
        {
            var property = type.Properties.Find(candidate => candidate.Name == parameter.Name && candidate.BackingField != null);
            if (property?.BackingField is not { } backing)
            {
                continue;
            }

            block.Statements.Add(new BoundExprStmt
            {
                Expression = new BoundAssign
                {
                    Target = Field(new BoundThis { Type = type }, backing),
                    Value = new BoundParam { Symbol = parameter, Type = parameter.Type },
                    Type = backing.Type
                }
            });
        }

        AppendFieldInitialisers(block, method);

        BoundExpr? baseCall = null;
        if (declaration.BaseList?.Types.FirstOrDefault() is PrimaryConstructorBaseTypeSyntax baseSyntax)
        {
            var model = _compilation.GetSemanticModel(declaration.SyntaxTree);
            baseCall = LowerInitialiser(model.GetOperation(baseSyntax));
        }

        if (baseCall != null)
        {
            block.Statements.Add(new BoundExprStmt { Expression = baseCall });
        }
        else
        {
            AppendImplicitBaseConstructor(block, method);
        }
    }

    private static BoundFieldAccess Field(BoundExpr receiver, FieldSymbol field) =>
        new() { Receiver = receiver, Field = field, Type = field.Type };

    private IEnumerable<FieldSymbol> OwnFields(TypeSymbol type) =>
        type.InstanceFields.Where(field => ReferenceEquals(field.Owner, type));

    private void ReportRecord(string message)
    {
        if (_sources.TryGetValue(_currentType, out var record))
        {
            ReportOnce(record, message);
        }
    }

    private BoundExpr And(BoundExpr left, BoundExpr right) => BoolAnd(left, right);

    private BoundExpr Or(BoundExpr left, BoundExpr right) =>
        new BoundBinary { Type = _bool, Op = TokenKind.PipePipe, Kind = BinKind.BoolLogic, Left = left, Right = right };

    private BoundExpr RefCompare(BoundExpr left, BoundExpr right, TokenKind op) =>
        new BoundBinary { Type = _bool, Op = op, Kind = BinKind.RefEq, Left = left, Right = right };

    private BoundExpr NullLiteral() => new BoundLiteral { Type = _null, LitKind = LiteralKind.Null };

    private BoundExpr TextLiteral(string text, TypeSymbol owner) => new BoundLiteral
    {
        Type = _string,
        LitKind = LiteralKind.String,
        StringValue = text,
        Site = (string.IsNullOrEmpty(owner.SourceFile) ? "<record>" : owner.SourceFile) + ":" + owner.DeclarationLine
    };

    private BoundExpr Concatenate(BoundExpr left, BoundExpr right) =>
        new BoundBinary { Type = _string, Op = TokenKind.Plus, Kind = BinKind.StrConcat, Left = left, Right = right };

    private static IMethodSymbol? FindRecordMethod(INamedTypeSymbol type, string name, Func<IMethodSymbol, bool> match) =>
        type.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(match);

    private MethodSymbol? MapRecordMethod(INamedTypeSymbol type, string name, Func<IMethodSymbol, bool> match) =>
        FindRecordMethod(type, name, match) is { } symbol ? MapMethod(symbol, type.Locations.FirstOrDefault()) : null;

    // The record's own Equals(R), used to compare two values of that record type.
    private MethodSymbol? RecordEqualsMethod(INamedTypeSymbol type) =>
        MapRecordMethod(type, "Equals", method => method.Parameters.Length == 1
            && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, type));

    private MethodSymbol? RecordHashMethod(INamedTypeSymbol type) =>
        MapRecordMethod(type, "GetHashCode", method => method.Parameters.Length == 0);

    private MethodSymbol? RecordTextMethod(INamedTypeSymbol type) =>
        MapRecordMethod(type, "ToString", method => method.Parameters.Length == 0);

    private BoundExpr RecordEquals(MethodSymbol method, IMethodSymbol roslyn)
    {
        var type = method.Owner;
        var roslynType = roslyn.ContainingType;
        var self = new BoundThis { Type = type };
        var other = new BoundParam { Symbol = method.Parameters[0], Type = type };

        var result = RefCompare(other, NullLiteral(), TokenKind.BangEquals);
        if (IsRecordClass(roslynType.BaseType))
        {
            var baseType = (INamedTypeSymbol)roslynType.BaseType!;
            var baseEquals = RecordEqualsMethod(baseType);
            if (baseEquals != null)
            {
                var call = new BoundCall
                {
                    Receiver = self,
                    Method = baseEquals,
                    Arguments = [new BoundConversion { Operand = other, Type = type.BaseType! }],
                    Type = _bool
                };
                result = And(result, call);
            }
        }
        else
        {
            result = And(result, new BoundSameType { Left = self, Right = other, Type = _bool });
        }

        foreach (var field in OwnFields(type))
        {
            result = And(result, ValueEquals(Field(self, field), Field(other, field), field.Type));
        }

        return result;
    }

    // The sealed override on a derived record forwards to the record's own Equals.
    private BoundExpr RecordSealedEquals(MethodSymbol method, IMethodSymbol roslyn)
    {
        var type = method.Owner;
        var equals = RecordEqualsMethod(roslyn.ContainingType);
        if (equals == null)
        {
            return ErrorExpr();
        }

        var other = new BoundParam { Symbol = method.Parameters[0], Type = method.Parameters[0].Type };
        return BindMemberCall(new BoundThis { Type = type }, equals,
            [new BoundAs { Operand = other, TestType = type, Type = type }]);
    }

    private BoundExpr RecordEqualityOperator(MethodSymbol method, IMethodSymbol roslyn)
    {
        var type = method.Owner;
        var left = new BoundParam { Symbol = method.Parameters[0], Type = type };
        var right = new BoundParam { Symbol = method.Parameters[1], Type = type };
        var equals = RecordEqualsMethod(roslyn.ContainingType);
        if (equals == null)
        {
            return ErrorExpr();
        }

        var viaEquals = And(RefCompare(left, NullLiteral(), TokenKind.BangEquals),
            BindMemberCall(left, equals, [right]));
        return Or(RefCompare(left, right, TokenKind.EqualsEquals), viaEquals);
    }

    private BoundExpr ValueEquals(BoundExpr left, BoundExpr right, TypeSymbol type)
    {
        if (type == _string)
        {
            return new BoundBinary { Type = _bool, Op = TokenKind.EqualsEquals, Kind = BinKind.StrEq, Left = left, Right = right };
        }

        if (type.Kind == BoundKind.Class && _sources.TryGetValue(type, out var source) && IsRecordClass(source))
        {
            var equality = MapRecordMethod(source, "op_Equality", method => method.Parameters.Length == 2);
            if (equality != null)
            {
                return new BoundCall { Method = equality, Arguments = [left, right], Type = _bool };
            }
        }

        if (type.IsReferenceType)
        {
            return RefCompare(left, right, TokenKind.EqualsEquals);
        }

        if (type.Kind is BoundKind.Struct)
        {
            ReportRecord($"record member of struct type '{type.Name}' is not supported in generated equality");
            return PatternResult(false);
        }

        var representation = type.EnumUnderlyingType ?? type;
        var (kind, unsigned) = representation switch
        {
            _ when representation == _bool => (BinKind.BoolCmp, false),
            _ when representation == _double => (BinKind.DoubleCmp, false),
            _ when representation == _float => (BinKind.FloatCmp, false),
            _ when representation == _long || representation == _ulong => (BinKind.LongCmp, representation.IsUnsignedInteger),
            _ => (BinKind.IntCmp, representation.IsUnsignedInteger)
        };
        return new BoundBinary
        {
            Type = _bool, Op = TokenKind.EqualsEquals, Kind = kind, IsUnsigned = unsigned, Left = left, Right = right
        };
    }

    // Equal records must hash equally, so only values that equality compares
    // by value contribute, and a value that cannot overflow a conversion
    // contributes itself. Floating-point members add nothing.
    private BoundExpr RecordHash(MethodSymbol method, IMethodSymbol roslyn)
    {
        var type = method.Owner;
        var roslynType = roslyn.ContainingType;
        var self = new BoundThis { Type = type };

        BoundExpr hash = new BoundLiteral { Type = _int, LitKind = LiteralKind.Int, IntValue = 17 };
        if (IsRecordClass(roslynType.BaseType)
            && RecordHashMethod((INamedTypeSymbol)roslynType.BaseType!) is { } baseHash)
        {
            hash = new BoundCall { Receiver = self, Method = baseHash, Type = _int };
        }

        foreach (var field in OwnFields(type))
        {
            var part = HashOf(Field(self, field), field.Type);
            if (part == null)
            {
                continue;
            }

            var scaled = new BoundBinary
            {
                Type = _int, Op = TokenKind.Star, Kind = BinKind.IntArith, Left = hash,
                Right = new BoundLiteral { Type = _int, LitKind = LiteralKind.Int, IntValue = 31 }
            };
            hash = new BoundBinary { Type = _int, Op = TokenKind.Plus, Kind = BinKind.IntArith, Left = scaled, Right = part };
        }

        return hash;
    }

    private BoundExpr? HashOf(BoundExpr value, TypeSymbol type)
    {
        if (type == _string)
        {
            var input = new BoundPatternInput { Type = type };
            return Capture(value, input, new BoundChoice
            {
                Condition = RefCompare(input, NullLiteral(), TokenKind.EqualsEquals),
                WhenTrue = Number(0),
                WhenFalse = new BoundLength { Receiver = input, IsString = true, Type = _int },
                Type = _int
            });
        }

        if (type == _bool)
        {
            return new BoundChoice { Condition = value, WhenTrue = Number(1), WhenFalse = Number(0), Type = _int };
        }

        if (type.Kind == BoundKind.Enum || type.IsInteger)
        {
            return NumericConversion(value, _int);
        }

        if (type.Kind == BoundKind.Class && _sources.TryGetValue(type, out var source) && IsRecordClass(source)
            && RecordHashMethod(source) is { } hashMethod)
        {
            var input = new BoundPatternInput { Type = type };
            return Capture(value, input, new BoundChoice
            {
                Condition = RefCompare(input, NullLiteral(), TokenKind.EqualsEquals),
                WhenTrue = Number(0),
                WhenFalse = BindMemberCall(input, hashMethod, []),
                Type = _int
            });
        }

        return null;
    }

    private BoundLiteral Number(int value) => new() { Type = _int, LitKind = LiteralKind.Int, IntValue = value };

    private IEnumerable<ISymbol> PrintedMembers(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var current = type; current != null && IsRecordClass(current); current = current.BaseType)
        {
            chain.Insert(0, current);
        }

        foreach (var declaring in chain)
        {
            foreach (var member in declaring.GetMembers())
            {
                switch (member)
                {
                    case IFieldSymbol { IsStatic: false, IsImplicitlyDeclared: false, IsConst: false, DeclaredAccessibility: Accessibility.Public }:
                    case IPropertySymbol { IsStatic: false, IsIndexer: false, IsOverride: false, GetMethod: not null, DeclaredAccessibility: Accessibility.Public }:
                        yield return member;
                        break;
                }
            }
        }
    }

    private BoundExpr RecordText(MethodSymbol method, IMethodSymbol roslyn)
    {
        var type = method.Owner;
        var roslynType = roslyn.ContainingType;
        var self = new BoundThis { Type = type };
        var location = roslynType.Locations.FirstOrDefault();

        BoundExpr text = TextLiteral(roslynType.Name + " {", type);
        var first = true;
        foreach (var member in PrintedMembers(roslynType))
        {
            BoundExpr? value;
            ITypeSymbol memberType;
            if (member is IFieldSymbol field)
            {
                memberType = field.Type;
                value = MapField(field, location) is { } mapped ? Field(self, mapped) : null;
            }
            else
            {
                var property = (IPropertySymbol)member;
                memberType = property.Type;
                value = MapMethod(property.GetMethod!, location) is { } getter ? BindMemberCall(self, getter, []) : null;
            }

            if (value == null)
            {
                continue;
            }

            text = Concatenate(text, TextLiteral((first ? " " : ", ") + member.Name + " = ", type));
            text = Concatenate(text, TextOf(value, memberType, type));
            first = false;
        }

        return Concatenate(text, TextLiteral(" }", type));
    }

    // Text for one record member. Strings and numbers go straight into the
    // concatenation, an enum prints its name, and an object prints through ToString.
    private BoundExpr TextOf(BoundExpr value, ITypeSymbol roslynType, TypeSymbol owner)
    {
        var type = value.Type;
        if (type.Kind == BoundKind.Enum && roslynType is INamedTypeSymbol enumType)
        {
            return EnumText(value, enumType, owner);
        }

        if (type.Kind is BoundKind.Struct or BoundKind.Array || type == _error)
        {
            ReportRecord($"record member of type '{type.Name}' cannot be printed by the generated ToString: declare ToString yourself");
            return TextLiteral("", owner);
        }

        if (type.Kind == BoundKind.Class && type != _object)
        {
            return ObjectText(value);
        }

        return value;
    }

    private BoundExpr EnumText(BoundExpr value, INamedTypeSymbol enumType, TypeSymbol owner)
    {
        var input = new BoundPatternInput { Type = value.Type };
        BoundExpr text = Concatenate(TextLiteral("", owner), input);
        var representation = value.Type.EnumUnderlyingType ?? _int;
        foreach (var constant in enumType.GetMembers().OfType<IFieldSymbol>().Where(field => field.HasConstantValue).Reverse())
        {
            var literal = new BoundLiteral
            {
                Type = value.Type,
                LitKind = representation == _long || representation == _ulong ? LiteralKind.Long : LiteralKind.Int,
                IntValue = constant.ConstantValue is ulong unsignedValue ? unchecked((long)unsignedValue) : Convert.ToInt64(constant.ConstantValue)
            };
            text = new BoundChoice
            {
                Condition = new BoundBinary
                {
                    Type = _bool, Op = TokenKind.EqualsEquals, Kind = BinKind.IntCmp,
                    IsUnsigned = representation.IsUnsignedInteger, Left = input, Right = literal
                },
                WhenTrue = TextLiteral(constant.Name, owner),
                WhenFalse = text,
                Type = _string
            };
        }

        return Capture(value, input, text);
    }

    private static PropertySymbol? FindProperty(TypeSymbol type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.Properties.Find(candidate => candidate.Name == name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void LowerDeconstruct(BoundBlock block, MethodSymbol method)
    {
        var type = method.Owner;
        var self = new BoundThis { Type = type };
        foreach (var parameter in method.Parameters)
        {
            var property = FindProperty(type, parameter.Name);
            BoundExpr? read = null;
            if (property?.Getter != null)
            {
                read = BindMemberCall(self, property.Getter, []);
            }

            if (read == null)
            {
                continue;
            }

            block.Statements.Add(new BoundExprStmt
            {
                Expression = new BoundAssign
                {
                    Target = new BoundParam { Symbol = parameter, Type = parameter.Type, ByRef = true },
                    Value = read,
                    Type = parameter.Type
                }
            });
        }
    }

    // `var (a, b) = value;` calls the instance Deconstruct method with hidden
    // out locals, then assigns each local to its target. Only a statement can
    // hold one, because the expression's own type would be a tuple.
    private BoundExpr LowerDeconstruction(IDeconstructionAssignmentOperation operation)
    {
        var info = operation.Syntax is AssignmentExpressionSyntax assignment
            ? _compilation.GetSemanticModel(assignment.SyntaxTree).GetDeconstructionInfo(assignment)
            : default;
        var targets = new List<IOperation>();
        if (info.Method is not { IsStatic: false } deconstruct
            || info.Nested.Any(nested => nested.Method != null)
            || !FlattenDeconstructionTargets(operation.Target, targets)
            || targets.Count != deconstruct.Parameters.Length
            || MapMethod(deconstruct, operation.Syntax.GetLocation()) is not { } method)
        {
            return UnsupportedExpr(operation);
        }

        var value = LowerExpr(operation.Value);
        var input = new BoundPatternInput { Type = value.Type };
        var effects = new List<BoundExpr>();
        var outputs = new List<BoundLocal>();
        foreach (var parameter in method.Parameters)
        {
            var local = new LocalSymbol { Name = "#deconstructed", Id = _localId++, Type = parameter.Type };
            _methodLocals.Add(local);
            outputs.Add(new BoundLocal { Symbol = local, Type = local.Type });
        }

        effects.Add(BindMemberCall(input, method, outputs.Select((output, index) => (BoundExpr)new BoundByRefArg
        {
            Lvalue = output, Kind = method.Parameters[index].RefKind, Type = output.Type
        }).ToList()));

        for (var i = 0; i < targets.Count; i++)
        {
            if (targets[i] is IDiscardOperation)
            {
                continue;
            }

            var target = LowerExpr(targets[i]);
            if (target.Type != outputs[i].Type)
            {
                return UnsupportedExpr(operation);
            }

            effects.Add(new BoundAssign { Target = target, Value = outputs[i], Type = target.Type });
        }

        var done = new BoundSequence { Effects = effects, Value = Number(0), Type = _int };
        return Capture(value, input, done);
    }

    private static bool FlattenDeconstructionTargets(IOperation target, List<IOperation> targets)
    {
        switch (target)
        {
            case IDeclarationExpressionOperation declaration:
                return FlattenDeconstructionTargets(declaration.Expression, targets);
            case ITupleOperation tuple:
                return tuple.Elements.All(element => FlattenDeconstructionTargets(element, targets));
            case ILocalReferenceOperation or IFieldReferenceOperation or IDiscardOperation:
                targets.Add(target);
                return true;
            default:
                return false;
        }
    }

    // `x with { ... }` copies every field of the record, then runs the member
    // initialisers on the copy. The record constructor does not run.
    private BoundExpr LowerWith(IWithOperation with)
    {
        var operand = LowerExpr(with.Operand);
        var type = operand.Type;
        if (type.Kind != BoundKind.Class)
        {
            return UnsupportedExpr(with);
        }

        var source = new BoundPatternInput { Type = type };
        var copy = new BoundPatternInput { Type = type };
        var effects = new List<BoundExpr>();
        foreach (var field in type.InstanceFields)
        {
            effects.Add(new BoundAssign { Target = Field(copy, field), Value = Field(source, field), Type = field.Type });
        }

        _initialiserTargets.Push(copy);
        AppendMemberInitialisers(effects, with.Initializer);
        _initialiserTargets.Pop();

        var copied = Capture(new BoundNewObject { Type = type, Site = Site(with.Syntax) }, copy,
            new BoundSequence { Effects = effects, Value = copy, Type = type });
        return Capture(operand, source, copied);
    }

    // The text of a class instance in a string concatenation or a print call:
    // its ToString when it has one, otherwise its type name, and nothing for null.
    private BoundExpr ObjectText(BoundExpr value)
    {
        var type = value.Type;
        var input = new BoundPatternInput { Type = type };
        BoundExpr text = FindToString(type) is { } toString
            ? BindMemberCall(input, toString, [])
            : TextLiteral(type.Name, _currentType);
        return Capture(value, input, new BoundChoice
        {
            Condition = RefCompare(input, NullLiteral(), TokenKind.BangEquals),
            WhenTrue = text,
            WhenFalse = TextLiteral("", _currentType),
            Type = _string
        });
    }

    private static MethodSymbol? FindToString(TypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var found = current.Methods.Find(candidate => candidate.Name == "ToString" && candidate.Parameters.Count == 0
                && !candidate.IsStatic && (candidate.IsVirtual || candidate.IsOverride));
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    // A class operand is converted to text through ToString. Other reference
    // types have no text form here, and reporting them is better than printing
    // a pointer as if it were a string.
    private BoundExpr StringOperand(BoundExpr operand, SyntaxNode syntax)
    {
        var type = operand.Type;
        if (type.Kind == BoundKind.Struct && type.ToStringOverride is { } toString)
        {
            return new BoundCall { Receiver = operand, Method = toString, Type = _string };
        }

        if (type == _string || type == _null || !type.IsReferenceType || type == _error)
        {
            return operand;
        }

        if (type.Kind == BoundKind.Class && type != _object && !type.Name.EndsWith(".$box", StringComparison.Ordinal))
        {
            return ObjectText(operand);
        }

        Report(syntax, $"converting '{type.Name}' to text is not supported", DiagnosticCatalog.UnsupportedConstructId);
        return operand;
    }
}
