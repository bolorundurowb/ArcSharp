using System.Collections.Immutable;
using System.Linq;
using ArcSharp.Diagnostics;
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
        ImmutableArray.Create(Descriptors.UnsupportedConstruct, Descriptors.ArcClrDisagreement);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeNode, UnsupportedKinds);
        context.RegisterSyntaxNodeAction(AnalyzeDeclaration, DeclarationKinds);
        context.RegisterSyntaxNodeAction(AnalyzeMember, MemberKinds);
        context.RegisterSyntaxNodeAction(AnalyzeUsing, SyntaxKind.UsingStatement);
        context.RegisterSyntaxNodeAction(AnalyzeForEach, SyntaxKind.ForEachStatement);
        context.RegisterSyntaxNodeAction(AnalyzePattern, SyntaxKind.IsPatternExpression, SyntaxKind.CasePatternSwitchLabel);
        context.RegisterOperationAction(AnalyzeIncrement, OperationKind.Increment, OperationKind.Decrement);
        context.RegisterOperationAction(AnalyzeOverloadedBinary, OperationKind.Binary);
        context.RegisterOperationAction(AnalyzeEnumConversion, OperationKind.Conversion);
        context.RegisterSyntaxNodeAction(AnalyzeLocalDeclaration, SyntaxKind.LocalDeclarationStatement);
        context.RegisterSyntaxNodeAction(AnalyzeProperty, SyntaxKind.PropertyDeclaration, SyntaxKind.IndexerDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeArrayType, SyntaxKind.ArrayType);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static readonly SyntaxKind[] UnsupportedKinds =
    {
        SyntaxKind.GotoStatement,
        SyntaxKind.GotoCaseStatement,
        SyntaxKind.GotoDefaultStatement,
        SyntaxKind.ForEachVariableStatement,
        SyntaxKind.ThrowExpression,
        SyntaxKind.CatchFilterClause,
        SyntaxKind.LockStatement,
        SyntaxKind.YieldReturnStatement,
        SyntaxKind.YieldBreakStatement,
        SyntaxKind.UnsafeStatement,
        SyntaxKind.FixedStatement,
        SyntaxKind.StackAllocArrayCreationExpression,
        SyntaxKind.ImplicitStackAllocArrayCreationExpression,
        SyntaxKind.SimpleLambdaExpression,
        SyntaxKind.ParenthesizedLambdaExpression,
        SyntaxKind.AnonymousMethodExpression,
        SyntaxKind.BaseExpression,
        SyntaxKind.QueryExpression,
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
        SyntaxKind.DelegateDeclaration,
        SyntaxKind.EventDeclaration,
        SyntaxKind.EventFieldDeclaration,
        SyntaxKind.RecordDeclaration,
        SyntaxKind.RecordStructDeclaration,
        SyntaxKind.LocalFunctionStatement,
        SyntaxKind.ArrowExpressionClause,
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

    private static void AnalyzeNode(SyntaxNodeAnalysisContext context)
    {
        var message = Describe(context.Node);
        if (message != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, context.Node.GetLocation(), message));
        }
    }

    private static void AnalyzePattern(SyntaxNodeAnalysisContext context)
    {
        var pattern = context.Node is IsPatternExpressionSyntax expression
            ? expression.Pattern
            : ((CasePatternSwitchLabelSyntax)context.Node).Pattern;
        var unsupported = pattern.DescendantNodesAndSelf()
            .FirstOrDefault(node => node is RecursivePatternSyntax or ListPatternSyntax or SlicePatternSyntax);
        if (unsupported != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct,
                unsupported.GetLocation(), "recursive and list patterns are not supported"));
        }
    }

    private static void AnalyzeOverloadedBinary(OperationAnalysisContext context)
    {
        var operation = (Microsoft.CodeAnalysis.Operations.IBinaryOperation)context.Operation;
        if (operation.OperatorMethod != null && (operation.IsLifted
            || operation.OperatorKind is Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.ConditionalAnd
                or Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.ConditionalOr))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct,
                operation.Syntax.GetLocation(), "this overloaded binary operator is not supported"));
        }
    }

    private static void AnalyzeEnumConversion(OperationAnalysisContext context)
    {
        var operation = (Microsoft.CodeAnalysis.Operations.IConversionOperation)context.Operation;
        if ((operation.Operand.Type?.TypeKind == TypeKind.Enum && operation.Type?.IsReferenceType == true)
            || (operation.Type?.TypeKind == TypeKind.Enum && operation.Operand.Type?.IsReferenceType == true))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct,
                operation.Syntax.GetLocation(), "boxing and unboxing enum values are not supported"));
        }
    }

    private static void AnalyzeIncrement(OperationAnalysisContext context)
    {
        var operation = (Microsoft.CodeAnalysis.Operations.IIncrementOrDecrementOperation)context.Operation;
        if (operation.OperatorMethod != null || operation.IsLifted
            || operation.Target.Kind is not (OperationKind.LocalReference or OperationKind.ParameterReference
                or OperationKind.FieldReference or OperationKind.ArrayElementReference or OperationKind.PropertyReference)
            || operation.Target.Type?.SpecialType == SpecialType.System_Decimal)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct,
                operation.Syntax.GetLocation(), "'++' and '--' require supported numeric storage"));
        }
    }

    private static void AnalyzeForEach(SyntaxNodeAnalysisContext context)
    {
        var loop = (ForEachStatementSyntax)context.Node;
        var model = context.SemanticModel;
        var type = model.GetTypeInfo(loop.Expression, context.CancellationToken).Type;
        var conversion = model.GetForEachStatementInfo(loop).ElementConversion;
        if (type is not IArrayTypeSymbol { IsSZArray: true } || loop.Type is RefTypeSyntax
            || conversion.IsUserDefined || conversion.IsBoxing || conversion.IsUnboxing)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct,
                loop.GetLocation(), "'foreach' requires a single-dimensional array and a supported element conversion"));
        }
    }

    private static string? Describe(SyntaxNode node) => node.Kind() switch
    {
        SyntaxKind.GotoStatement or SyntaxKind.GotoCaseStatement or SyntaxKind.GotoDefaultStatement =>
            "'goto' is not supported",
        SyntaxKind.ForEachVariableStatement => "deconstructing 'foreach' is not supported",
        SyntaxKind.ThrowExpression => "throw expressions are not supported: use a 'throw' statement",
        SyntaxKind.CatchFilterClause => "exception filters are not supported",
        SyntaxKind.LockStatement => "'lock' is not supported: there are no threads",
        SyntaxKind.YieldReturnStatement or SyntaxKind.YieldBreakStatement =>
            "'yield' is not supported: iterators are not implemented",
        SyntaxKind.UnsafeStatement => "'unsafe' is not supported",
        SyntaxKind.FixedStatement => "'fixed' is not supported",
        SyntaxKind.StackAllocArrayCreationExpression or SyntaxKind.ImplicitStackAllocArrayCreationExpression =>
            "'stackalloc' is not supported",
        SyntaxKind.SimpleLambdaExpression or SyntaxKind.ParenthesizedLambdaExpression or SyntaxKind.AnonymousMethodExpression =>
            "lambdas ('=>') are not supported",
        SyntaxKind.BaseExpression =>
            "'base' member access is not supported: inherited members are called by name",
        SyntaxKind.QueryExpression => "LINQ is not supported",
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
        SyntaxKind.RefExpression =>
            "ref locals and ref returns are not supported: a managed reference must not outlive the call",
        SyntaxKind.DefaultExpression or SyntaxKind.DefaultLiteralExpression =>
            "'default' is not supported: assign an explicit value",
        SyntaxKind.RefType =>
            "ref locals and ref returns are not supported: a managed reference must not outlive the call",
        SyntaxKind.PointerType => "pointers are not supported",
        SyntaxKind.FunctionPointerType => "function pointers are not supported",
        SyntaxKind.TupleExpression or SyntaxKind.TupleType => "tuples are not supported",
        SyntaxKind.GlobalStatement => "top-level statements are not supported: declare a class with 'static void Main()'",
        SyntaxKind.AttributeList => "attributes are not supported",
        SyntaxKind.DelegateDeclaration => "delegates are not supported",
        SyntaxKind.EventDeclaration or SyntaxKind.EventFieldDeclaration => "events are not supported",
        SyntaxKind.RecordDeclaration or SyntaxKind.RecordStructDeclaration => "records are not supported",
        SyntaxKind.LocalFunctionStatement => "local functions are not supported",
        SyntaxKind.ArrowExpressionClause when node.Parent is not
            (MethodDeclarationSyntax or ConstructorDeclarationSyntax or OperatorDeclarationSyntax
                or ConversionOperatorDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
                or AccessorDeclarationSyntax) => "this expression-bodied member is not supported",
        SyntaxKind.CollectionExpression => "collection expressions ('[...]') are not supported",
        SyntaxKind.ImplicitArrayCreationExpression => "array initialisers are not supported: use 'new T[n]'",
        _ => null
    };

    private static void AnalyzeDeclaration(SyntaxNodeAnalysisContext context)
    {
        var decl = (TypeDeclarationSyntax)context.Node;
        var message = ModifierMessage(decl.Modifiers);
        if (context.SemanticModel.GetDeclaredSymbol(decl, context.CancellationToken) is INamedTypeSymbol
            { ContainingType.IsGenericType: true })
        {
            message ??= "types nested in generic types are not supported";
        }

        message ??= (decl is ClassDeclarationSyntax && decl.ParameterList != null
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
        var message = ModifierMessage(decl.Modifiers, decl is FieldDeclarationSyntax);
        if (decl is MethodDeclarationSyntax { TypeParameterList: not null } method
            && (method.Parent is InterfaceDeclarationSyntax
                || method.Modifiers.Any(SyntaxKind.VirtualKeyword)
                || method.Modifiers.Any(SyntaxKind.OverrideKeyword)
                || method.Modifiers.Any(SyntaxKind.AbstractKeyword)))
        {
            message = "virtual and interface generic methods are not supported yet";
        }
        if (message != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, decl.GetLocation(), message));
        }
    }

    private static string? ModifierMessage(SyntaxTokenList modifiers, bool field = false)
    {
        foreach (var m in modifiers)
        {
            switch (m.Kind())
            {
                case SyntaxKind.SealedKeyword:
                    return "'sealed' is not supported";
                case SyntaxKind.PartialKeyword:
                    return "'partial' is not supported";
                case SyntaxKind.ReadOnlyKeyword when !field:
                    return "'readonly' is not supported on this declaration";
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

        if (d.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct,
                d.Modifiers.First(m => m.IsKind(SyntaxKind.RefKeyword)).GetLocation(),
                "ref locals and ref returns are not supported: a managed reference must not outlive the call"));
        }
    }

    private static void AnalyzeProperty(SyntaxNodeAnalysisContext context)
    {
        var property = (BasePropertyDeclarationSyntax)context.Node;
        var expression = property switch
        {
            PropertyDeclarationSyntax declaration => declaration.ExpressionBody,
            IndexerDeclarationSyntax declaration => declaration.ExpressionBody,
            _ => null
        };
        if (property.Parent is InterfaceDeclarationSyntax && (expression != null
            || property.AccessorList?.Accessors.Any(accessor => accessor.Body != null || accessor.ExpressionBody != null) == true))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct,
                property.GetLocation(), "default interface accessors are not supported"));
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

    // The GC calls that ArcSharp rejects or accepts as no-ops. The compiler
    // reports the same ids and messages from the shared catalogue.
    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                ContainingType: { Name: "GC", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } }
            } method)
        {
            return;
        }

        switch (method.Name)
        {
            case "ReRegisterForFinalize":
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.UnsupportedConstruct, invocation.GetLocation(), DiagnosticCatalog.GcReRegisterMessage));
                break;
            case "Collect" when method.Parameters.Length == 0:
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.ArcClrDisagreement, invocation.GetLocation(), DiagnosticCatalog.GcCollectMessage));
                break;
            case "WaitForPendingFinalizers":
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.ArcClrDisagreement, invocation.GetLocation(), DiagnosticCatalog.GcWaitMessage));
                break;
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
