using System.Collections.Immutable;
using System.Linq;
using ArcSharp.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

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
        context.RegisterSyntaxNodeAction(AnalyzePattern, SyntaxKind.IsPatternExpression,
            SyntaxKind.CasePatternSwitchLabel, SyntaxKind.SwitchExpressionArm);
        context.RegisterOperationAction(AnalyzeIncrement, OperationKind.Increment, OperationKind.Decrement);
        context.RegisterOperationAction(AnalyzeBoxingConversion, OperationKind.Conversion);
        context.RegisterSyntaxNodeAction(AnalyzeLocalDeclaration, SyntaxKind.LocalDeclarationStatement);
        context.RegisterSyntaxNodeAction(AnalyzeProperty, SyntaxKind.PropertyDeclaration, SyntaxKind.IndexerDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeArrayType, SyntaxKind.ArrayType);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterOperationAction(AnalyzeDelegateCreation, OperationKind.DelegateCreation);
        context.RegisterSyntaxNodeAction(AnalyzeEventField, SyntaxKind.EventFieldDeclaration);
        context.RegisterOperationAction(AnalyzeConditionalAccess, OperationKind.ConditionalAccess);
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
        SyntaxKind.AnonymousMethodExpression,
        SyntaxKind.BaseExpression,
        SyntaxKind.QueryExpression,
        SyntaxKind.AwaitExpression,
        SyntaxKind.CoalesceAssignmentExpression,
        SyntaxKind.RangeExpression,
        SyntaxKind.ImplicitObjectCreationExpression,
        SyntaxKind.AnonymousObjectCreationExpression,
        SyntaxKind.InterpolatedStringExpression,
        SyntaxKind.RefExpression,
        SyntaxKind.RefType,
        SyntaxKind.PointerType,
        SyntaxKind.FunctionPointerType,
        SyntaxKind.TupleExpression,
        SyntaxKind.TupleType,
        SyntaxKind.GlobalStatement,
        SyntaxKind.AttributeList,
        SyntaxKind.EventDeclaration,
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
        SyntaxKind.InterfaceDeclaration,
        SyntaxKind.RecordDeclaration
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

    internal static bool IsSupportedEvent(IEventSymbol symbol) =>
        symbol is { IsAbstract: false, IsVirtual: false, IsOverride: false, AddMethod: { IsExtern: false }, RemoveMethod: { IsExtern: false } }
        && symbol.ContainingType.TypeKind == TypeKind.Class
        && symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
            is VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax };

    private static void AnalyzeEventField(SyntaxNodeAnalysisContext context)
    {
        var declaration = (EventFieldDeclarationSyntax)context.Node;
        if (declaration.Declaration.Variables.Any(variable =>
            context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is IEventSymbol symbol
            && !IsSupportedEvent(symbol)))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct, declaration.GetLocation(),
                "only non-virtual field-like events on classes are supported"));
        }
    }

    private static void AnalyzeNode(SyntaxNodeAnalysisContext context)
    {
        var message = Describe(context.Node);
        if (message != null && !IsDeconstructionTarget(context.Node))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, context.Node.GetLocation(), message));
        }
    }

    // `(a, b) = value;` deconstructs into existing variables. The tuple syntax is
    // only the shape of the targets, so it is not a tuple value.
    private static bool IsDeconstructionTarget(SyntaxNode node)
    {
        if (node is not TupleExpressionSyntax)
        {
            return false;
        }

        var current = node;
        while (current.Parent is ArgumentSyntax { Parent: TupleExpressionSyntax outer })
        {
            current = outer;
        }

        return current.Parent is AssignmentExpressionSyntax assignment
            && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assignment.Left == current;
    }

    private static void AnalyzePattern(SyntaxNodeAnalysisContext context)
    {
        var pattern = context.Node switch
        {
            IsPatternExpressionSyntax expression => expression.Pattern,
            CasePatternSwitchLabelSyntax label => label.Pattern,
            SwitchExpressionArmSyntax arm => arm.Pattern,
            _ => throw new System.InvalidOperationException("unexpected pattern syntax")
        };
        foreach (var list in pattern.DescendantNodesAndSelf().OfType<ListPatternSyntax>())
        {
            if (context.SemanticModel.GetOperation(list, context.CancellationToken)
                is Microsoft.CodeAnalysis.Operations.IListPatternOperation { InputType: not IArrayTypeSymbol })
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct,
                    list.GetLocation(), "list patterns require a single-dimensional array"));
            }
        }
    }

    // A lambda must be synchronous, not nested in another lambda, and have a
    // by-value signature. A delegate can bind to a method declared in the program.
    // A library method has no body to call, and a struct instance method would
    // need the struct boxed.
    private static void AnalyzeDelegateCreation(OperationAnalysisContext context)
    {
        var creation = (IDelegateCreationOperation)context.Operation;
        if (creation.Target is IAnonymousFunctionOperation { Syntax: LambdaExpressionSyntax } lambda)
        {
            if (lambda.Symbol.IsAsync || lambda.Syntax.Ancestors().Any(node => node is LambdaExpressionSyntax)
                || creation.Type is not INamedTypeSymbol type || !HasByValueSignature(type))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct, creation.Syntax.GetLocation(),
                    "only non-nested synchronous lambdas with by-value delegate signatures are supported"));
            }

            return;
        }

        if (creation.Target is not IMethodReferenceOperation reference)
        {
            return;
        }

        var method = reference.Method;
        if (!method.Locations.Any(location => location.IsInSource))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct,
                creation.Syntax.GetLocation(), "a delegate cannot bind to a library method"));
        }
        else if (!method.IsStatic && method.ContainingType.TypeKind == TypeKind.Struct)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct,
                creation.Syntax.GetLocation(),
                "a delegate to an instance method of a struct is not supported: the struct would have to be boxed"));
        }
    }

    private static bool HasByValueSignature(INamedTypeSymbol type) =>
        type.DelegateInvokeMethod is { ReturnsByRef: false, ReturnsByRefReadonly: false } invoke
        && invoke.Parameters.All(parameter => parameter.RefKind == RefKind.None);

    private static void AnalyzeBoxingConversion(OperationAnalysisContext context)
    {
        var operation = (Microsoft.CodeAnalysis.Operations.IConversionOperation)context.Operation;
        // Only a nullable struct declared in the program has a box. Primitives and
        // enums have none, with or without the nullable wrapper.
        static bool UnsupportedValue(ITypeSymbol? type) =>
            type?.TypeKind == TypeKind.Enum
            || (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                && (nullable.TypeArguments[0].TypeKind != TypeKind.Struct
                    || nullable.TypeArguments[0].SpecialType != SpecialType.None));

        if ((UnsupportedValue(operation.Operand.Type) && operation.Type?.IsReferenceType == true)
            || (UnsupportedValue(operation.Type) && operation.Operand.Type?.IsReferenceType == true))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct,
                operation.Syntax.GetLocation(), "boxing and unboxing enum values, or nullable values of a built-in type, are not supported"));
        }
    }

    private static void AnalyzeIncrement(OperationAnalysisContext context)
    {
        var operation = (Microsoft.CodeAnalysis.Operations.IIncrementOrDecrementOperation)context.Operation;
        if (operation.Target.Kind is not (OperationKind.LocalReference or OperationKind.ParameterReference
                or OperationKind.FieldReference or OperationKind.ArrayElementReference or OperationKind.PropertyReference))
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
        SyntaxKind.AnonymousMethodExpression => "anonymous methods are not supported",
        SyntaxKind.BaseExpression =>
            "'base' member access is not supported: inherited members are called by name",
        SyntaxKind.QueryExpression => "LINQ is not supported",
        SyntaxKind.AwaitExpression => "'await' is not supported: there is no async support",
        SyntaxKind.CoalesceAssignmentExpression =>
            "the null-coalescing assignment ('??=') is not supported",
        SyntaxKind.RangeExpression => "ranges ('..') are not supported",
        SyntaxKind.ImplicitObjectCreationExpression => "target-typed 'new()' is not supported: name the type",
        SyntaxKind.AnonymousObjectCreationExpression => "anonymous types are not supported",
        SyntaxKind.InterpolatedStringExpression => "interpolated strings are not supported: use concatenation",
        SyntaxKind.RefExpression =>
            "ref locals and ref returns are not supported: a managed reference must not outlive the call",
        SyntaxKind.RefType =>
            "ref locals and ref returns are not supported: a managed reference must not outlive the call",
        SyntaxKind.PointerType => "pointers are not supported",
        SyntaxKind.FunctionPointerType => "function pointers are not supported",
        SyntaxKind.TupleExpression or SyntaxKind.TupleType => "tuples are not supported",
        SyntaxKind.GlobalStatement => "top-level statements are not supported: declare a class with 'static void Main()'",
        SyntaxKind.AttributeList => "attributes are not supported",
        SyntaxKind.EventDeclaration => "only non-virtual field-like events on classes are supported",
        SyntaxKind.RecordStructDeclaration => "record structs are not supported: declare a record class",
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

        message ??= decl is RecordDeclarationSyntax { TypeParameterList: not null } ? "generic records are not supported" : null;
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
        if (decl is MethodDeclarationSyntax { TypeParameterList: not null, Parent: InterfaceDeclarationSyntax } generic
            && generic.Modifiers.Any(SyntaxKind.StaticKeyword)
            && (generic.Modifiers.Any(SyntaxKind.AbstractKeyword) || generic.Modifiers.Any(SyntaxKind.VirtualKeyword)))
        {
            message = "generic static abstract and static virtual methods are not supported";
        }
        else if (decl is MethodDeclarationSyntax { TypeParameterList: not null, Parent: StructDeclarationSyntax } method
            && context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken) is { } symbol
            && symbol.ContainingType.AllInterfaces
                .SelectMany(iface => iface.GetMembers().OfType<IMethodSymbol>())
                .Any(member => member.IsGenericMethod
                    && SymbolEqualityComparer.Default.Equals(symbol.ContainingType.FindImplementationForInterfaceMember(member), symbol)))
        {
            message = "generic interface methods are not supported on structs";
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

    // '?.' is accepted on a reference receiver, either as a statement or with a
    // reference result. A value result would have to become nullable.
    private static void AnalyzeConditionalAccess(OperationAnalysisContext context)
    {
        var operation = (Microsoft.CodeAnalysis.Operations.IConditionalAccessOperation)context.Operation;
        var outermost = operation;
        while (outermost.Parent is Microsoft.CodeAnalysis.Operations.IConditionalAccessOperation parent
            && parent.WhenNotNull == outermost)
        {
            outermost = parent;
        }

        var asStatement = outermost.Parent is Microsoft.CodeAnalysis.Operations.IExpressionStatementOperation;
        var supported = operation.Operation.Type?.IsReferenceType == true
            && (asStatement || operation.Type?.IsReferenceType == true);
        if (!supported)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedConstruct,
                operation.Syntax.GetLocation(),
                "the null-conditional operator ('?.') needs a reference receiver and a reference result, or a statement"));
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var o = (ObjectCreationExpressionSyntax)context.Node;
        if (o.ArgumentList?.Arguments.Count > 1
            && context.SemanticModel.GetTypeInfo(o, context.CancellationToken).Type?.SpecialType == SpecialType.System_Decimal)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, o.GetLocation(),
                "'new decimal' with several arguments is not supported: build the value from an integer and divide"));
        }

        if (o.Initializer == null)
        {
            return;
        }

        if (o.Initializer.IsKind(SyntaxKind.CollectionInitializerExpression))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, o.Initializer.GetLocation(),
                "collection initialisers are not supported: call 'Add' after 'new'"));
        }
        else if (context.SemanticModel.GetTypeInfo(o, context.CancellationToken).Type is { IsValueType: true })
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedConstruct, o.Initializer.GetLocation(),
                "object initialisers on structs are not supported: assign the fields after 'new'"));
        }
    }
}
