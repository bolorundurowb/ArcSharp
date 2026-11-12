// Static possible-strong-cycle analysis (roadmap section 7, ARC3100).
//
// Builds a graph over the program's class types: an edge C -> T exists when C
// declares a strong field of class type T, including arrays of a class (T[]),
// static fields, and class references reached through a struct field (a class
// holding a struct that holds a class is still a strong path). WeakReference<T>
// fields and the legacy `weak` modifier are not edges: both are ways of
// breaking a cycle. A strongly connected component with more than one class,
// or a single class with a self-edge, means objects of those types can form a
// reference cycle at run time, which ARC leaks. That is a warning, not an
// error: the program is legal and the leak may be intentional
// (samples/cycle_strong.cs exists to document it).
//
// The check is about types, not object graphs. It warns on a binary tree's
// self-typed field, which does not leak unless an instance is later wired
// into a loop, and it cannot see a cycle that only exists at run time through
// a base-typed or interface-typed field. The runtime [arc] line remains the
// evidence either way.
namespace ArcSharp.Binding;

using ArcSharp.Diagnostics;

public static class CycleAnalysis
{
    public static void Analyze(BoundProgram program, Action<int, string> report)
    {
        var classes = program.Types.Where(t => t.Kind == TypeKind.Class).ToList();
        var names = classes.Select(t => t.Name).ToHashSet();

        var edges = new Dictionary<TypeSymbol, List<TypeSymbol>>();
        foreach (var c in classes)
        {
            edges[c] = StrongTargets(c, names);
        }

        foreach (var scc in StronglyConnected(classes, edges))
        {
            if (scc.Count > 1)
            {
                var line = scc.Min(t => t.ClassSyntax?.Line ?? 0);
                var sccNames = scc.Select(t => t.Name).ToList();
                report(line, DiagnosticCatalog.PossibleStrongCycleMessage(sccNames));
            }
            else if (edges[scc[0]].Contains(scc[0]))
            {
                var t = scc[0];
                report(t.ClassSyntax?.Line ?? 0, DiagnosticCatalog.SelfCycleMessage(t.Name));
            }
        }
    }

    private static List<TypeSymbol> StrongTargets(TypeSymbol t, HashSet<string> classNames)
    {
        var into = new List<TypeSymbol>();
        CollectTargets(t, classNames, into, new HashSet<TypeSymbol>());
        return into;
    }

    // Walks the fields of a class or struct. Structs are values, so a class
    // reference stored in one is still a strong edge from the enclosing
    // object; structs cannot contain themselves, but the visited set keeps the
    // walk safe if that rule ever changes.
    private static void CollectTargets(
        TypeSymbol t, HashSet<string> classNames, List<TypeSymbol> into, HashSet<TypeSymbol> visitedStructs)
    {
        foreach (var f in t.InstanceFields.Concat(t.StaticFields))
        {
            if (f.IsWeak)
            {
                continue;
            }

            var ft = f.Type;
            if (ft.Kind == TypeKind.WeakRef)
            {
                continue;
            }

            if (ft.Kind == TypeKind.Array)
            {
                ft = ft.ElementType!;
            }

            if (ft.Kind == TypeKind.Struct)
            {
                if (visitedStructs.Add(ft))
                {
                    CollectTargets(ft, classNames, into, visitedStructs);
                }

                continue;
            }

            if (ft.Kind == TypeKind.Class && classNames.Contains(ft.Name) && !into.Contains(ft))
            {
                into.Add(ft);
            }
        }
    }

    // Tarjan's strongly connected components over the class graph.
    private static IEnumerable<List<TypeSymbol>> StronglyConnected(
        List<TypeSymbol> nodes, Dictionary<TypeSymbol, List<TypeSymbol>> edges)
    {
        var index = new Dictionary<TypeSymbol, int>();
        var low = new Dictionary<TypeSymbol, int>();
        var onStack = new HashSet<TypeSymbol>();
        var stack = new List<TypeSymbol>();
        var next = 0;
        var result = new List<List<TypeSymbol>>();

        foreach (var n in nodes)
        {
            if (!index.ContainsKey(n))
            {
                Visit(n);
            }
        }

        return result;

        void Visit(TypeSymbol v)
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
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w))
                {
                    low[v] = Math.Min(low[v], index[w]);
                }
            }

            if (low[v] != index[v])
            {
                return;
            }

            var scc = new List<TypeSymbol>();
            while (stack.Count > 0)
            {
                var w = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                onStack.Remove(w);
                scc.Add(w);
                if (w == v)
                {
                    break;
                }
            }

            result.Add(scc);
        }
    }
}
