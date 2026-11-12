using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using ArcSharp.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ArcSharp.Analyzers;

// ARC3100: a class whose strong fields can reach each other in a loop. The
// compiler reports the same warning from its own type graph; this rule gives
// the same answer in an ordinary dotnet build.
//
// The graph has one node per class declared in this compilation. An edge C -> T
// exists when C declares a strong field of class type T, including T[], static
// fields, and class references reached through a struct field. WeakReference<T>
// fields and the legacy `weak` modifier are not edges: both are how a cycle is
// broken. Strongly connected components are reported once each, which makes
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
        context.RegisterCompilationAction(Analyze);
    }

    private static void Analyze(CompilationAnalysisContext context)
    {
        var classes = new List<INamedTypeSymbol>();
        Collect(context.Compilation.Assembly.GlobalNamespace, classes);

        var bySymbol = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
        var known = new HashSet<INamedTypeSymbol>(classes, SymbolEqualityComparer.Default);
        foreach (var c in classes)
        {
            bySymbol[c] = StrongTargets(c, known);
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
                if (type.TypeKind == TypeKind.Class && !type.IsImplicitlyDeclared)
                {
                    into.Add(type);
                }

                Collect(type, into);
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
            if (member is not IFieldSymbol field || field.IsImplicitlyDeclared)
            {
                continue;
            }

            var target = field.Type;
            if (target is IArrayTypeSymbol array)
            {
                target = array.ElementType;
            }

            if (target is not INamedTypeSymbol named)
            {
                continue;
            }

            if (named.TypeKind == TypeKind.Struct)
            {
                if (visitedStructs.Add(named))
                {
                    CollectTargets(named, known, into, visitedStructs);
                }

                continue;
            }

            if (named.TypeKind != TypeKind.Class)
            {
                continue;
            }

            if (IsWeakReference(named) || !known.Contains(named))
            {
                continue;
            }

            if (!into.Contains(named, SymbolEqualityComparer.Default))
            {
                into.Add(named);
            }
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
