using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ArcSharp.Analyzers;

// ARC3001: a construct that is valid C# but outside the ArcSharp subset. The
// compiler reports these itself; the analyser makes the same rule visible in
// an ordinary dotnet build, before the source ever reaches ArcSharp.
//
// The checks are syntactic on purpose. The point is to stop a construct that
// the compiler cannot lower, not to duplicate the binder's type checking.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnsupportedConstructAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.UnsupportedConstruct);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeNode, UnsupportedKinds);
        context.RegisterSyntaxNodeAction(AnalyzeDeclaration, DeclarationKinds);
        context.RegisterSyntaxNodeAction(AnalyzeMember, MemberKinds);
        context.RegisterSyntaxNodeAction(AnalyzeUsing, SyntaxKind.UsingStatement);
        context.RegisterSyntaxNodeAction(AnalyzeLocalDeclaration, SyntaxKind.LocalDeclarationStatement);
        context.RegisterSyntaxNodeAction(AnalyzeParameter, SyntaxKind.Parameter);
        context.RegisterSyntaxNodeAction(AnalyzeArgument, SyntaxKind.Argument);
        context.RegisterSyntaxNodeAction(AnalyzeBinary, BinaryKinds);
        context.RegisterSyntaxNodeAction(AnalyzeProperty, SyntaxKind.PropertyDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeAccessor, AccessorKinds);
        context.RegisterSyntaxNodeAction(AnalyzeStruct, SyntaxKind.StructDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeArrayType, SyntaxKind.ArrayType);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private static readonly SyntaxKind[] UnsupportedKinds =
    {
        SyntaxKind.BreakStatement,
        SyntaxKind.ContinueStatement,
        SyntaxKind.GotoStatement,
        SyntaxKind.DoStatement,
        SyntaxKind.ForEachStatement,
        SyntaxKind.ForEachVariableStatement,
        SyntaxKind.SwitchStatement,
        SyntaxKind.TryStatement,
        SyntaxKind.ThrowStatement,
        SyntaxKind.ThrowExpression,
        SyntaxKind.LockStatement,
        SyntaxKind.YieldReturnStatement,
        SyntaxKind.YieldBreakStatement,
        SyntaxKind.CheckedStatement,
        SyntaxKind.CheckedExpression,
        SyntaxKind.UnsafeStatement,
        SyntaxKind.FixedStatement,
        SyntaxKind.StackAllocArrayCreationExpression,
        SyntaxKind.ImplicitStackAllocArrayCreationExpression,
        SyntaxKind.SimpleLambdaExpression,
        SyntaxKind.ParenthesizedLambdaExpression,
        SyntaxKind.AnonymousMethodExpression,
        SyntaxKind.BaseExpression,
        SyntaxKind.QueryExpression,
        SyntaxKind.IsPatternExpression,
        SyntaxKind.SwitchExpression,
        SyntaxKind.WithExpression,
        SyntaxKind.AwaitExpression,
        SyntaxKind.ConditionalAccessExpression,
        SyntaxKind.CoalesceExpression,
        SyntaxKind.CoalesceAssignmentExpression,
        SyntaxKind.RangeExpression,
        SyntaxKind.ImplicitObjectCreationExpression,
        SyntaxKind.AnonymousObjectCreationExpression,
        SyntaxKind.InterpolatedStringExpression,
        SyntaxKind.RefExpression,
        SyntaxKind.DefaultExpression,
        SyntaxKind.DefaultLiteralExpression,
        SyntaxKind.RefType,
        SyntaxKind.PointerType,
        SyntaxKind.FunctionPointerType,
        SyntaxKind.TupleExpression,
        SyntaxKind.TupleType,
        SyntaxKind.GlobalStatement,
        SyntaxKind.AttributeList,
        SyntaxKind.DestructorDeclaration,
        SyntaxKind.OperatorDeclaration,
        SyntaxKind.ConversionOperatorDeclaration,
        SyntaxKind.IndexerDeclaration,
        SyntaxKind.EnumDeclaration,
        SyntaxKind.DelegateDeclaration,
        SyntaxKind.EventDeclaration,
        SyntaxKind.EventFieldDeclaration,
        SyntaxKind.RecordDeclaration,
        SyntaxKind.RecordStructDeclaration,
        SyntaxKind.LocalFunctionStatement,
        SyntaxKind.ArrowExpressionClause,
        SyntaxKind.TypeParameterList,
        SyntaxKind.CollectionExpression,
        SyntaxKind.ImplicitArrayCreationExpression
    };

    private static readonly SyntaxKind[] DeclarationKinds =
    {
        SyntaxKind.ClassDeclaration,
        SyntaxKind.StructDeclaration,
        SyntaxKind.InterfaceDeclaration
    };

    private static readonly SyntaxKind[] MemberKinds =
    {
        SyntaxKind.MethodDeclaration,
        SyntaxKind.ConstructorDeclaration,
        SyntaxKind.FieldDeclaration,
        SyntaxKind.PropertyDeclaration,
        SyntaxKind.EventDeclaration,
        SyntaxKind.OperatorDeclaration,
        SyntaxKind.ConversionOperatorDeclaration
    };

    private static readonly SyntaxKind[] BinaryKinds =
    {
        SyntaxKind.LeftShiftExpression,
        SyntaxKind.RightShiftExpression,
        SyntaxKind.UnsignedRightShiftExpression,
        SyntaxKind.LeftShiftAssignmentExpression,
        SyntaxKind.RightShiftAssignmentExpression,
        SyntaxKind.UnsignedRightShiftAssignmentExpression
    };

    private static readonly SyntaxKind[] AccessorKinds =
    {
        SyntaxKind.GetAccessorDeclaration,
        SyntaxKind.SetAccessorDeclaration,
        SyntaxKind.InitAccessorDeclaration,
        SyntaxKind.AddAccessorDeclaration,
        SyntaxKind.RemoveAccessorDeclaration
    };

    private static void AnalyzeNode(SyntaxNodeAnalysisContext context)
    {
        var message = Describe(context.Node);
        if (message != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, context.Node.GetLocation(), message));
        }
    }

    private static string? Describe(SyntaxNode node) => node.Kind() switch
    {
        SyntaxKind.BreakStatement =>
            "'break' is not supported: end a loop by making its condition false",
        SyntaxKind.ContinueStatement =>
            "'continue' is not supported: restructure the loop with an 'if'",
        SyntaxKind.GotoStatement => "'goto' is not supported",
        SyntaxKind.DoStatement => "'do'/'while' loops are not supported: use 'while'",
        SyntaxKind.ForEachStatement or SyntaxKind.ForEachVariableStatement =>
            "'foreach' is not supported: use a 'for' loop with an index",
        SyntaxKind.SwitchStatement => "'switch' is not supported: use 'if'/'else'",
        SyntaxKind.TryStatement => "exceptions are not supported: 'try'/'catch'/'finally'",
        SyntaxKind.ThrowStatement or SyntaxKind.ThrowExpression =>
            "'throw' is not supported: exceptions are not implemented",
        SyntaxKind.LockStatement => "'lock' is not supported: there are no threads",
        SyntaxKind.YieldReturnStatement or SyntaxKind.YieldBreakStatement =>
            "'yield' is not supported: iterators are not implemented",
        SyntaxKind.CheckedStatement =>
            ((CheckedStatementSyntax)node).Keyword.IsKind(SyntaxKind.CheckedKeyword)
                ? "'checked' is not supported: arithmetic is always unchecked"
                : "'unchecked' is not supported: arithmetic is always unchecked",
        SyntaxKind.CheckedExpression =>
            ((CheckedExpressionSyntax)node).Keyword.IsKind(SyntaxKind.CheckedKeyword)
                ? "'checked' is not supported: arithmetic is always unchecked"
                : "'unchecked' is not supported: arithmetic is always unchecked",
        SyntaxKind.UnsafeStatement => "'unsafe' is not supported",
        SyntaxKind.FixedStatement => "'fixed' is not supported",
        SyntaxKind.StackAllocArrayCreationExpression or SyntaxKind.ImplicitStackAllocArrayCreationExpression =>
            "'stackalloc' is not supported",
        SyntaxKind.SimpleLambdaExpression or SyntaxKind.ParenthesizedLambdaExpression or SyntaxKind.AnonymousMethodExpression =>
            "lambdas ('=>') are not supported",
        SyntaxKind.BaseExpression =>
            "'base' member access is not supported: inherited members are called by name",
        SyntaxKind.QueryExpression => "LINQ is not supported",
        SyntaxKind.IsPatternExpression => "pattern matching is not supported: use 'is Type' and 'as'",
        SyntaxKind.SwitchExpression => "switch expressions are not supported: use 'if'/'else'",
        SyntaxKind.WithExpression => "'with' expressions are not supported",
        SyntaxKind.AwaitExpression => "'await' is not supported: there is no async support",
        SyntaxKind.ConditionalAccessExpression => "the null-conditional operator ('?.') is not supported",
        SyntaxKind.CoalesceExpression or SyntaxKind.CoalesceAssignmentExpression =>
            "the null-coalescing operator ('??') is not supported",
        SyntaxKind.RangeExpression => "ranges ('..') are not supported",
        SyntaxKind.ImplicitObjectCreationExpression => "target-typed 'new()' is not supported: name the type",
        SyntaxKind.AnonymousObjectCreationExpression => "anonymous types are not supported",
        SyntaxKind.InterpolatedStringExpression => "interpolated strings are not supported: use concatenation",
        SyntaxKind.RefExpression => "'ref' expressions are not supported",
        SyntaxKind.DefaultExpression or SyntaxKind.DefaultLiteralExpression =>
            "'default' is not supported: assign an explicit value",
        SyntaxKind.RefType => "'ref' types are not supported",
        SyntaxKind.PointerType => "pointers are not supported",
        SyntaxKind.FunctionPointerType => "function pointers are not supported",
        SyntaxKind.TupleExpression or SyntaxKind.TupleType => "tuples are not supported",
        SyntaxKind.GlobalStatement => "top-level statements are not supported: declare a class with 'static void Main()'",
        SyntaxKind.AttributeList => "attributes are not supported",
        SyntaxKind.DestructorDeclaration => "finalisers ('~T()') are not supported",
        SyntaxKind.OperatorDeclaration or SyntaxKind.ConversionOperatorDeclaration =>
            "operator overloading is not supported",
        SyntaxKind.IndexerDeclaration => "indexers are not supported",
        SyntaxKind.EnumDeclaration => "enums are not supported: use named constants or plain 'int' fields",
        SyntaxKind.DelegateDeclaration => "delegates are not supported",
        SyntaxKind.EventDeclaration or SyntaxKind.EventFieldDeclaration => "events are not supported",
        SyntaxKind.RecordDeclaration or SyntaxKind.RecordStructDeclaration => "records are not supported",
        SyntaxKind.LocalFunctionStatement => "local functions are not supported",
        SyntaxKind.ArrowExpressionClause => "expression-bodied members are not supported: use a block body",
        SyntaxKind.TypeParameterList => "generics are not supported: only WeakReference<T> is recognised",
        SyntaxKind.CollectionExpression => "collection expressions ('[...]') are not supported",
        SyntaxKind.ImplicitArrayCreationExpression => "array initialisers are not supported: use 'new T[n]'",
        _ => null
    };

    private static void AnalyzeDeclaration(SyntaxNodeAnalysisContext context)
    {
        var decl = (TypeDeclarationSyntax)context.Node;
        var message = ModifierMessage(decl.Modifiers) ??
                      (decl is ClassDeclarationSyntax && decl.ParameterList != null
                          ? "primary constructors are not supported"
                          : null);
        if (message != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, decl.Identifier.GetLocation(), message));
        }
    }

    private static void AnalyzeMember(SyntaxNodeAnalysisContext context)
    {
        var decl = (MemberDeclarationSyntax)context.Node;
        var message = ModifierMessage(decl.Modifiers);
        if (message != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, decl.GetLocation(), message));
        }
    }

    private static string? ModifierMessage(SyntaxTokenList modifiers)
    {
        foreach (var m in modifiers)
        {
            switch (m.Kind())
            {
                case SyntaxKind.SealedKeyword:
                    return "'sealed' is not supported";
                case SyntaxKind.PartialKeyword:
                    return "'partial' is not supported";
                case SyntaxKind.ReadOnlyKeyword:
                    return "'readonly' is not supported";
                case SyntaxKind.ConstKeyword:
                    return "constants are not supported: use a 'static' field";
                case SyntaxKind.ExternKeyword:
                    return "'extern' is not supported";
                case SyntaxKind.AsyncKeyword:
                    return "'async' is not supported";
                case SyntaxKind.VolatileKeyword:
                    return "'volatile' is not supported";
            }
        }

        return null;
    }

    private static void AnalyzeUsing(SyntaxNodeAnalysisContext context)
    {
        var u = (UsingStatementSyntax)context.Node;
        if (u.Declaration is { Variables.Count: > 1 })
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct,
                u.UsingKeyword.GetLocation(),
                "a 'using' statement may declare only one resource"));
        }
    }

    private static void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context)
    {
        var d = (LocalDeclarationStatementSyntax)context.Node;
        if (!d.UsingKeyword.IsKind(SyntaxKind.None))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct,
                d.UsingKeyword.GetLocation(),
                "'using var' is not supported: use a 'using (...)' block"));
        }
    }

    private static void AnalyzeParameter(SyntaxNodeAnalysisContext context)
    {
        var p = (ParameterSyntax)context.Node;
        if (p.Default != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, p.GetLocation(),
                "optional parameters are not supported"));
        }
        else if (p.Modifiers.Any(t => t.IsKind(SyntaxKind.ParamsKeyword)))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, p.GetLocation(),
                "parameter arrays are not supported"));
        }
    }

    private static void AnalyzeArgument(SyntaxNodeAnalysisContext context)
    {
        var a = (ArgumentSyntax)context.Node;
        if (a.NameColon != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, a.GetLocation(),
                "named arguments are not supported"));
        }
    }

    private static void AnalyzeBinary(SyntaxNodeAnalysisContext context)
    {
        var b = (BinaryExpressionSyntax)context.Node;
        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.UnsupportedConstruct, b.OperatorToken.GetLocation(),
            "shift operators ('<<', '>>', '>>>') are not supported"));
    }

    private static void AnalyzeProperty(SyntaxNodeAnalysisContext context)
    {
        var p = (PropertyDeclarationSyntax)context.Node;
        if (p.Modifiers.Any(t => t.IsKind(SyntaxKind.StaticKeyword)))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, p.Identifier.GetLocation(),
                "static auto-properties are not supported: use a public static field"));
            return;
        }

        var hasSetter = p.AccessorList?.Accessors.Any(a =>
            a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration)) ?? false;
        if (!hasSetter)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, p.Identifier.GetLocation(),
                "get-only auto-properties are not supported: declare 'get; set;'"));
        }
    }

    private static void AnalyzeAccessor(SyntaxNodeAnalysisContext context)
    {
        var a = (AccessorDeclarationSyntax)context.Node;
        if (a.Body != null || a.ExpressionBody != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, a.GetLocation(),
                "properties with accessor bodies are not supported: use an auto-property"));
        }
    }

    private static void AnalyzeStruct(SyntaxNodeAnalysisContext context)
    {
        var s = (StructDeclarationSyntax)context.Node;
        if (s.BaseList != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, s.BaseList.GetLocation(),
                "interfaces are not supported on structs"));
        }
    }

    private static void AnalyzeArrayType(SyntaxNodeAnalysisContext context)
    {
        var a = (ArrayTypeSyntax)context.Node;
        if (a.RankSpecifiers.Any(r => r.Rank > 1) || a.ElementType is ArrayTypeSyntax)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, a.GetLocation(),
                "multi-dimensional and jagged arrays are not supported"));
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var o = (ObjectCreationExpressionSyntax)context.Node;
        if (o.Initializer != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, o.Initializer.GetLocation(),
                "object initialisers are not supported: assign the fields after 'new'"));
        }
    }
}
