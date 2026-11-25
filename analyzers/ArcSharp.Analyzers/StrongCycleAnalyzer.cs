using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using ArcSharp.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace ArcSharp.Analyzers;

// ARC3100: a class whose strong fields can reach each other in a loop. The
// compiler reports the same warning from its own type graph; this rule gives
// the same answer in an ordinary dotnet build.
//
// The graph has one node per class declared in this compilation. An edge C -> T
// exists when C declares a strong field of class type T, including T[], static
// fields, and class references reached through a struct field. A
// WeakReference<T> field is not an edge: it is how a cycle is broken.
// Strongly connected components are reported once each, which makes
// the warning a statement about types, not about a particular object graph. A
// tree or a singly linked list warns too; that is the documented false
// positive.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StrongCycleAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.PossibleStrongCycle);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var creations = new ConcurrentBag<IDelegateCreationOperation>();
            var functions = new ConcurrentDictionary<IMethodSymbol, IBlockOperation>(SymbolEqualityComparer.Default);
            start.RegisterOperationAction(operation =>
                creations.Add((IDelegateCreationOperation)operation.Operation), OperationKind.DelegateCreation);
            start.RegisterOperationAction(operation =>
            {
                if (operation.Operation is ILocalFunctionOperation { Body: { } body } function)
                {
                    functions.TryAdd(function.Symbol, body);
                }
            }, OperationKind.LocalFunction);
            start.RegisterCompilationEndAction(end =>
                Analyze(end, creations.SelectMany(creation => CollectCaptures(creation, functions)).ToList()));
        });
    }

    private static void Analyze(
        CompilationAnalysisContext context,
        List<(INamedTypeSymbol Delegate, ITypeSymbol Capture)> captures)
    {
        var classes = new List<INamedTypeSymbol>();
        Collect(context.Compilation.Assembly.GlobalNamespace, classes);

        // Framework delegates can be stored in fields and events.
        var frameworkDelegates = captures.Select(capture => capture.Delegate)
            .Where(type => !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, context.Compilation.Assembly))
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .ToList();
        classes.AddRange(frameworkDelegates);

        var bySymbol = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
        var known = new HashSet<INamedTypeSymbol>(classes, SymbolEqualityComparer.Default);
        foreach (var c in classes)
        {
            bySymbol[c] = StrongTargets(c, known);
        }

        foreach (var (type, capture) in captures)
        {
            if (bySymbol.TryGetValue(type, out var targets))
            {
                AddTarget(capture, known, targets, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default));
            }
        }

        foreach (var scc in StronglyConnected(classes, bySymbol))
        {
            if (scc.Count > 1)
            {
                var location = scc.Select(c => c.Locations.FirstOrDefault(l => l.IsInSource))
                    .Where(l => l != null)
                    .OrderBy(l => l!.SourceSpan.Start)
                    .FirstOrDefault();
                if (location == null)
                {
                    continue;
                }

                var names = scc.Select(c => c.Name).ToList();
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.PossibleStrongCycle, location,
                    DiagnosticCatalog.PossibleStrongCycleMessage(names)));
            }
            else
            {
                var c = scc[0];
                if (!bySymbol[c].Contains(c, SymbolEqualityComparer.Default))
                {
                    continue;
                }

                var location = c.Locations.FirstOrDefault(l => l.IsInSource);
                if (location == null)
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.PossibleStrongCycle, location,
                    DiagnosticCatalog.SelfCycleMessage(c.Name)));
            }
        }
    }

    private static void Collect(INamespaceOrTypeSymbol container, List<INamedTypeSymbol> into)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns)
            {
                Collect(ns, into);
            }
            else if (member is INamedTypeSymbol type)
            {
                if (type.TypeKind is TypeKind.Class or TypeKind.Delegate && !type.IsImplicitlyDeclared)
                {
                    into.Add(type);
                }

                Collect(type, into);
            }
        }
    }

    private static IEnumerable<(INamedTypeSymbol Delegate, ITypeSymbol Capture)> CollectCaptures(
        IDelegateCreationOperation creation, ConcurrentDictionary<IMethodSymbol, IBlockOperation> functions)
    {
        if (creation.Type is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } type)
        {
            yield break;
        }

        var (method, body) = creation.Target switch
        {
            IAnonymousFunctionOperation { Syntax: LambdaExpressionSyntax, Symbol.IsAsync: false } lambda =>
                (lambda.Symbol, lambda.Body),
            IMethodReferenceOperation { Method.MethodKind: MethodKind.LocalFunction } reference =>
                (reference.Method.OriginalDefinition, GetBody(reference.Method)),
            _ => (null, null)
        };
        if (method == null || body == null)
        {
            yield break;
        }

        var scopes = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        for (var scope = method.ContainingSymbol as IMethodSymbol; scope != null;
             scope = scope.ContainingSymbol as IMethodSymbol)
        {
            scopes.Add(scope);
        }

        foreach (var operation in CaptureOperations(body))
        {
            var capturedType = operation switch
            {
                ILocalReferenceOperation local when !local.Local.IsConst
                    && scopes.Contains(local.Local.ContainingSymbol) => local.Type,
                IParameterReferenceOperation parameter when scopes.Contains(parameter.Parameter.ContainingSymbol) =>
                    parameter.Type,
                IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } =>
                    method.ContainingType,
                _ => null
            };
            if (capturedType != null)
            {
                yield return (type, capturedType);
            }
        }

        IBlockOperation? GetBody(IMethodSymbol symbol) =>
            functions.TryGetValue(symbol.OriginalDefinition, out var localBody) ? localBody : null;

        IEnumerable<IOperation> CaptureOperations(IBlockOperation initial)
        {
            var pending = new Stack<IBlockOperation>();
            var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            pending.Push(initial);
            while (pending.Count != 0)
            {
                foreach (var operation in pending.Pop().DescendantsAndSelf())
                {
                    yield return operation;
                    var target = operation switch
                    {
                        IInvocationOperation invocation => invocation.TargetMethod,
                        IMethodReferenceOperation reference => reference.Method,
                        _ => null
                    };
                    if (target?.MethodKind == MethodKind.LocalFunction && seen.Add(target.OriginalDefinition)
                        && GetBody(target) is { } dependency)
                    {
                        pending.Push(dependency);
                    }
                }
            }
        }
    }

    private static List<INamedTypeSymbol> StrongTargets(
        INamedTypeSymbol type, HashSet<INamedTypeSymbol> known)
    {
        var into = new List<INamedTypeSymbol>();
        CollectTargets(type, known, into, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default));
        return into;
    }

    // Walks the fields of a class or struct. A class reference stored in a
    // struct field is still a strong edge from the enclosing object. Structs
    // cannot contain themselves in ArcSharp, but the visited set keeps the
    // walk finite regardless.
    private static void CollectTargets(
        INamedTypeSymbol type,
        HashSet<INamedTypeSymbol> known,
        List<INamedTypeSymbol> into,
        HashSet<INamedTypeSymbol> visitedStructs)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is IFieldSymbol { IsImplicitlyDeclared: false } field)
            {
                AddTarget(field.Type, known, into, visitedStructs);
            }
            else if (member is IEventSymbol @event && UnsupportedConstructAnalyzer.IsSupportedEvent(@event))
            {
                AddTarget(@event.Type, known, into, visitedStructs);
            }
        }
    }

    private static void AddTarget(
        ITypeSymbol target,
        HashSet<INamedTypeSymbol> known,
        List<INamedTypeSymbol> into,
        HashSet<INamedTypeSymbol> visitedStructs)
    {
        if (target is IArrayTypeSymbol array)
        {
            target = array.ElementType;
        }

        if (target is not INamedTypeSymbol named)
        {
            return;
        }

        if (named.TypeKind == TypeKind.Struct && visitedStructs.Add(named))
        {
            CollectTargets(named, known, into, visitedStructs);
        }

        if (known.Contains(named) && !IsWeakReference(named)
            && !into.Contains(named, SymbolEqualityComparer.Default))
        {
            into.Add(named);
        }
    }

    private static bool IsWeakReference(INamedTypeSymbol type) =>
        type.Name == "WeakReference" &&
        type.TypeArguments.Length == 1 &&
        type.ContainingNamespace?.ToDisplayString() == "System";

    // Tarjan's strongly connected components.
    private static IEnumerable<List<INamedTypeSymbol>> StronglyConnected(
        List<INamedTypeSymbol> nodes,
        Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> edges)
    {
        var index = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
        var low = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
        var onStack = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var stack = new List<INamedTypeSymbol>();
        var next = 0;
        var result = new List<List<INamedTypeSymbol>>();

        foreach (var n in nodes)
        {
            if (!index.ContainsKey(n))
            {
                Visit(n);
            }
        }

        return result;

        void Visit(INamedTypeSymbol v)
        {
            index[v] = next;
            low[v] = next;
            next++;
            stack.Add(v);
            onStack.Add(v);

            foreach (var w in edges[v])
            {
                if (!index.ContainsKey(w))
                {
                    Visit(w);
                    low[v] = System.Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w))
                {
                    low[v] = System.Math.Min(low[v], index[w]);
                }
            }

            if (low[v] != index[v])
            {
                return;
            }

            var scc = new List<INamedTypeSymbol>();
            while (stack.Count > 0)
            {
                var w = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                onStack.Remove(w);
                scc.Add(w);
                if (SymbolEqualityComparer.Default.Equals(w, v))
                {
                    break;
                }
            }

            result.Add(scc);
        }
    }
}
