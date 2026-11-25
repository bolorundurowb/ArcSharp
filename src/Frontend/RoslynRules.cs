using ArcSharp.Diagnostics;
using ArcSharp.Lexing;
using RoslynKind = Microsoft.CodeAnalysis.TypeKind;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace ArcSharp.Frontend;

sealed partial class RoslynLowering
{
    public void CopyRoslynDiagnostics()
    {
        foreach (var diagnostic in _compilation.GetDiagnostics())
        {
            if (diagnostic.Severity is Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden or Microsoft.CodeAnalysis.DiagnosticSeverity.Info || diagnostic.IsSuppressed)
            {
                continue;
            }

            var severity = diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning
                ? Lexing.DiagnosticSeverity.Warning
                : Lexing.DiagnosticSeverity.Error;
            Report(diagnostic.Location, diagnostic.GetMessage(), diagnostic.Id, severity);
        }
    }

    public void ReportUnsupportedSyntax()
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            new SyntaxRules(this, model).Visit(tree.GetRoot());
        }
    }

    public void ReportLifetimeRules()
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            new LifetimeRules(this, model).Visit(tree.GetRoot());
        }
    }

    // A finaliser runs while the object's strong count is zero, so a store of
    // 'this' that outlives the finaliser would resurrect it. Only a store the
    // compiler can see is rejected here; a store hidden behind a call is
    // caught by the runtime check that aborts when the count is above zero.
    public void ReportFinaliserRules()
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var destructor in tree.GetRoot().DescendantNodes().OfType<DestructorDeclarationSyntax>())
            {
                if (model.GetOperation(destructor) is { } body)
                {
                    ReportResurrection(body);
                }
            }
        }
    }

    private void ReportResurrection(IOperation body)
    {
        var aliases = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        bool changed;
        do
        {
            changed = false;
            foreach (var operation in body.DescendantsAndSelf())
            {
                switch (operation)
                {
                    case IVariableDeclaratorOperation { Initializer: { } initialiser } declarator
                        when IsThisDerived(initialiser.Value, aliases):
                        changed |= aliases.Add(declarator.Symbol);
                        break;
                    case ISimpleAssignmentOperation { Target: ILocalReferenceOperation local } assignment
                        when IsThisDerived(assignment.Value, aliases):
                        changed |= aliases.Add(local.Local);
                        break;
                }
            }
        }
        while (changed);

        foreach (var operation in body.DescendantsAndSelf())
        {
            if (operation is ISimpleAssignmentOperation
                {
                    Target: IFieldReferenceOperation or IPropertyReferenceOperation or IArrayElementReferenceOperation
                } assignment
                && IsThisDerived(assignment.Value, aliases))
            {
                Report(assignment.Syntax,
                    "a finaliser must not store 'this' in a field, property, or array element: an object cannot be resurrected",
                    "ARC2016");
            }
        }
    }

    // 'this', a conversion of it, or a local that was assigned one of those.
    private static bool IsThisDerived(IOperation value, HashSet<ILocalSymbol> aliases)
    {
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        return value switch
        {
            IInstanceReferenceOperation => true,
            ILocalReferenceOperation local => aliases.Contains(local.Local),
            _ => false
        };
    }

    private sealed class SyntaxRules : CSharpSyntaxWalker
    {
        private readonly RoslynLowering _lowering;
        private readonly SemanticModel _model;

        public SyntaxRules(RoslynLowering lowering, SemanticModel model)
        {
            _lowering = lowering;
            _model = model;
        }

        public override void Visit(SyntaxNode? node)
        {
            if (node == null)
            {
                return;
            }

            switch (node.Kind())
            {
                case SyntaxKind.ForEachVariableStatement:
                    Reject(node, "deconstructing 'foreach' is not supported");
                    break;
                case SyntaxKind.ThrowExpression:
                    Reject(node, "throw expressions are not supported: use a 'throw' statement");
                    break;
                case SyntaxKind.CatchFilterClause:
                    Reject(node, "exception filters are not supported");
                    break;
                case SyntaxKind.LockStatement:
                    Reject(node, "'lock' is not supported");
                    break;
                case SyntaxKind.YieldReturnStatement:
                case SyntaxKind.YieldBreakStatement:
                    Reject(node, "'yield' is not supported");
                    break;
                case SyntaxKind.UnsafeStatement:
                    Reject(node, "'unsafe' is not supported");
                    break;
                case SyntaxKind.AnonymousMethodExpression:
                    Reject(node, "anonymous methods are not supported");
                    break;
                case SyntaxKind.LocalFunctionStatement:
                    Reject(node, "local functions are not supported");
                    break;
                case SyntaxKind.ArrowExpressionClause when node.Parent is not
                    (MethodDeclarationSyntax or ConstructorDeclarationSyntax or OperatorDeclarationSyntax
                        or ConversionOperatorDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
                        or AccessorDeclarationSyntax):
                    Reject(node, "this expression-bodied member is not supported");
                    break;
                case SyntaxKind.QueryExpression:
                    Reject(node, "query expressions are not supported");
                    break;
                case SyntaxKind.AwaitExpression:
                    Reject(node, "'await' is not supported");
                    break;
                case SyntaxKind.RecordStructDeclaration:
                    Reject(node, "record structs are not supported: declare a record class");
                    break;
                case SyntaxKind.RecordDeclaration when node is RecordDeclarationSyntax { TypeParameterList: not null }:
                    Reject(node, "generic records are not supported");
                    break;
            }

            base.Visit(node);
        }

        public override void VisitEventDeclaration(EventDeclarationSyntax node)
        {
            Reject(node, UnsupportedEvent);
            base.VisitEventDeclaration(node);
        }

        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            if (node.Declaration.Variables.Any(variable =>
                _model.GetDeclaredSymbol(variable) is IEventSymbol symbol && !HasEventStorage(symbol)))
            {
                Reject(node, UnsupportedEvent);
            }

            base.VisitEventFieldDeclaration(node);
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (node.ReturnType is RefTypeSyntax)
            {
                Reject(node, "ref locals and ref returns are not supported: a managed reference must not outlive the call");
            }

            base.VisitMethodDeclaration(node);
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            if (_model.GetDeclaredSymbol(node) is INamedTypeSymbol symbol && ContainsItself(symbol))
            {
                _lowering.Report(node, $"struct '{symbol.Name}' contains itself", DiagnosticCatalog.UnsupportedConstructId);
            }

            base.VisitStructDeclaration(node);
        }

        public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            if (node.Declaration.Type is RefTypeSyntax)
            {
                Reject(node, "ref locals and ref returns are not supported: a managed reference must not outlive the call");
            }

            base.VisitLocalDeclarationStatement(node);
        }

        public override void VisitAttribute(AttributeSyntax node)
        {
            Reject(node, "attributes are not supported");
            base.VisitAttribute(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            if (_model.GetTypeInfo(node).Type is INamedTypeSymbol { IsAbstract: true, TypeKind: RoslynKind.Class })
            {
                _lowering.Report(node, $"cannot instantiate abstract class '{node.Type}'", "ARC2012");
            }

            base.VisitObjectCreationExpression(node);
        }

        public override void VisitUsingStatement(UsingStatementSyntax node)
        {
            var type = ResourceType(node);
            if (type != null && type.TypeKind != RoslynKind.Error && !HasDispose(type))
            {
                _lowering.Report(node, $"type '{type.Name}' used in 'using' has no parameterless 'void Dispose()' method", "ARC2011");
            }

            base.VisitUsingStatement(node);
        }

        private ITypeSymbol? ResourceType(UsingStatementSyntax node)
        {
            if (node.Expression != null)
            {
                return _model.GetTypeInfo(node.Expression).Type;
            }

            var variable = node.Declaration?.Variables.FirstOrDefault();
            return variable == null ? null : (_model.GetDeclaredSymbol(variable) as ILocalSymbol)?.Type;
        }

        private static bool HasDispose(ITypeSymbol type)
        {
            if (type.TypeKind == RoslynKind.Interface)
            {
                return type.GetMembers("Dispose").OfType<IMethodSymbol>()
                    .Any(method => method.Parameters.Length == 0 && method.ReturnsVoid);
            }

            for (var current = type; current != null; current = current.BaseType)
            {
                if (current.GetMembers("Dispose").OfType<IMethodSymbol>().Any(method =>
                        method.Parameters.Length == 0 && method.ReturnsVoid && !method.IsStatic))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsItself(INamedTypeSymbol symbol)
        {
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            return FieldContains(symbol, symbol, seen);
        }

        private static bool FieldContains(INamedTypeSymbol self, ITypeSymbol type, HashSet<ITypeSymbol> seen)
        {
            if (!seen.Add(type))
            {
                return false;
            }

            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic)
                {
                    continue;
                }

                if (SymbolEqualityComparer.Default.Equals(field.Type, self))
                {
                    return true;
                }

                if (field.Type is INamedTypeSymbol { TypeKind: RoslynKind.Struct } nested && FieldContains(self, nested, seen))
                {
                    return true;
                }
            }

            return false;
        }

        private void Reject(SyntaxNode node, string message) =>
            _lowering.Report(node, message, DiagnosticCatalog.UnsupportedConstructId);
    }

    private sealed class LifetimeRules : CSharpSyntaxWalker
    {
        private readonly RoslynLowering _lowering;
        private readonly SemanticModel _model;

        public LifetimeRules(RoslynLowering lowering, SemanticModel model)
        {
            _lowering = lowering;
            _model = model;
        }

        public override void VisitArgument(ArgumentSyntax node)
        {
            var kind = node.RefKindKeyword.Kind();
            if (kind is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword
                && IsReadOnlyIn(node.Expression))
            {
                _lowering.Report(node, ReadOnlyMessage(node.Expression), "ARC2015");
            }

            base.VisitArgument(node);
        }

        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            if (IsReadOnlyIn(node.Left))
            {
                _lowering.Report(node, ReadOnlyMessage(node.Left), "ARC2015");
            }

            base.VisitAssignmentExpression(node);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (node.Expression is MemberAccessExpressionSyntax access
                && ReceiverIsStruct(access.Expression)
                && IsReadOnlyIn(access.Expression))
            {
                _lowering.Report(node, ReadOnlyMessage(access.Expression), "ARC2015");
            }

            base.VisitInvocationExpression(node);
        }

        private bool IsReadOnlyIn(ExpressionSyntax expression)
        {
            expression = Unwrap(expression);
            if (expression is IdentifierNameSyntax or ThisExpressionSyntax)
            {
                return _model.GetSymbolInfo(expression).Symbol is IParameterSymbol { RefKind: RefKind.In };
            }

            if (expression is MemberAccessExpressionSyntax access && ReceiverIsStruct(access.Expression))
            {
                return IsReadOnlyIn(access.Expression);
            }

            if (expression is ElementAccessExpressionSyntax element && ReceiverIsStruct(element.Expression))
            {
                return IsReadOnlyIn(element.Expression);
            }

            return false;
        }

        private bool ReceiverIsStruct(ExpressionSyntax expression)
        {
            var type = _model.GetTypeInfo(Unwrap(expression)).Type;
            return type is { TypeKind: RoslynKind.Struct };
        }

        private string ReadOnlyMessage(ExpressionSyntax expression)
        {
            var name = "value";
            var current = Unwrap(expression);
            while (current is MemberAccessExpressionSyntax access)
            {
                current = Unwrap(access.Expression);
            }

            while (current is ElementAccessExpressionSyntax element)
            {
                current = Unwrap(element.Expression);
            }

            if (_model.GetSymbolInfo(current).Symbol is IParameterSymbol parameter)
            {
                name = parameter.Name;
            }

            return $"'in' parameter '{name}' is read-only";
        }

        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
        }
    }
}
