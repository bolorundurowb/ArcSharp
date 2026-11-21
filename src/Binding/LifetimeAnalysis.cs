using ArcSharp.Syntax;

namespace ArcSharp.Binding;

public enum ReferenceOwnership
{
    Owned,
    Borrowed,
    Shared
}

public sealed class LifetimeAnalysis
{
    private readonly Dictionary<BoundExpr, ReferenceOwnership> _ownership = [];

    public bool IsBorrowed(BoundExpr value) =>
        _ownership.TryGetValue(value, out var ownership) && ownership == ReferenceOwnership.Borrowed;

    private void Borrow(BoundExpr value)
    {
        _ownership[value] = ReferenceOwnership.Borrowed;
        if (value is BoundConversion conversion)
        {
            Borrow(conversion.Operand);
        }
    }

    public static LifetimeAnalysis Analyze(BoundProgram program, TextWriter? trace)
    {
        var result = new LifetimeAnalysis();
        var bodies = program.MethodBodies.ToDictionary(body => body.Method,
            body => Walk(body.Body).ToArray());
        var nonEscaping = new Dictionary<MethodSymbol, HashSet<ParamSymbol>>();
        var stableReads = new HashSet<BoundExpr>();

        foreach (var (method, nodes) in bodies)
        {
            var unique = nodes.OfType<BoundExpr>().GroupBy(value => value)
                .Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet();
            var unstable = nodes.OfType<BoundByRefArg>().Select(arg => arg.Lvalue).ToHashSet();
            unstable.UnionWith(nodes.OfType<BoundWeakRefTryGet>().Select(get => get.OutTarget));
            var exposedLocals = unstable.OfType<BoundLocal>().Select(local => local.Symbol).ToHashSet();
            exposedLocals.UnionWith(nodes.OfType<BoundUsing>().Select(scope => scope.Resource));
            var exposedParams = unstable.OfType<BoundParam>().Select(param => param.Symbol).ToHashSet();

            // The walk is post-order, so a conversion inherits stability
            // only after its operand has been checked.
            foreach (var value in nodes.OfType<BoundExpr>().Where(unique.Contains))
            {
                var stable = value.Type.IsReferenceType && value switch
                {
                    BoundLocal local => !exposedLocals.Contains(local.Symbol),
                    BoundParam param => param.Symbol.RefKind == RefKind.None && !exposedParams.Contains(param.Symbol),
                    BoundConversion conversion => stableReads.Contains(conversion.Operand),
                    _ => false
                };
                if (stable)
                {
                    stableReads.Add(value);
                }
            }

            foreach (var value in nodes.OfType<BoundExpr>().Where(value => value.Type.IsReferenceType))
            {
                result._ownership[value] = value switch
                {
                    BoundNewObject or BoundNewArray or BoundLiteral or BoundCall
                        or BoundWeakRefNew or BoundAs or BoundBinary => ReferenceOwnership.Owned,
                    BoundConversion conversion when result._ownership.GetValueOrDefault(
                        conversion.Operand, ReferenceOwnership.Shared) == ReferenceOwnership.Owned => ReferenceOwnership.Owned,
                    _ => ReferenceOwnership.Shared
                };
            }

            foreach (var node in nodes)
            {
                switch (node)
                {
                    case BoundLength length when stableReads.Contains(length.Receiver):
                        result.Borrow(length.Receiver);
                        break;
                    case BoundBinary { Kind: BinKind.RefEq or BinKind.StrEq } binary
                        when Simple(binary.Right):
                        if (stableReads.Contains(binary.Left))
                        {
                            result.Borrow(binary.Left);
                        }

                        if (stableReads.Contains(binary.Right))
                        {
                            result.Borrow(binary.Right);
                        }

                        break;
                }
            }

            // Only parameters whose every use is a proven non-escaping read
            // qualify. An unused parameter qualifies as well.
            var escaping = nodes.OfType<BoundParam>().Where(param => !result.IsBorrowed(param))
                .Select(param => param.Symbol).ToHashSet();
            nonEscaping[method] = method.Parameters
                .Where(param => param.RefKind == RefKind.None && !escaping.Contains(param)).ToHashSet();

            foreach (var call in nodes.OfType<BoundCall>())
            {
                if (call.Receiver is { } receiver && stableReads.Contains(receiver) && call.Arguments.All(Simple))
                {
                    result.Borrow(receiver);
                }
            }
        }

        foreach (var body in program.MethodBodies)
        {
            var nodes = bodies[body.Method];

            foreach (var call in nodes.OfType<BoundCall>())
            {
                if (call.Virtual || call.Interface || call.Method.IsConstructor || !call.Arguments.All(Simple)
                    || call.Arguments.Count != call.Method.Parameters.Count
                    || !nonEscaping.TryGetValue(call.Method, out var parameters))
                {
                    continue;
                }

                for (var i = 0; i < call.Arguments.Count; i++)
                {
                    var arg = call.Arguments[i];
                    if (stableReads.Contains(arg) && parameters.Contains(call.Method.Parameters[i]))
                    {
                        result.Borrow(arg);
                    }
                }
            }

            if (trace != null)
            {
                trace.WriteLine($"[ownership] {body.Method.MangledName}");
                foreach (var value in nodes.OfType<BoundExpr>().Where(value => value.Type.IsReferenceType))
                {
                    var ownership = result._ownership[value].ToString().ToLowerInvariant();
                    trace.WriteLine($"  {ownership,-8} {value.GetType().Name} {value.Type.Name}");
                }
            }

        }

        return result;
    }

    private static bool Simple(BoundExpr value) => value switch
    {
        BoundLocal or BoundParam or BoundLiteral => true,
        BoundConversion { Type.IsReferenceType: true, Operand.Type.IsReferenceType: true } conversion =>
            Simple(conversion.Operand),
        _ => false
    };

    private static IEnumerable<object> Walk(object? node)
    {
        if (node == null)
        {
            yield break;
        }

        foreach (var child in Children(node))
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }

        yield return node;
    }

    private static IEnumerable<object?> Children(object node) => node switch
    {
        BoundBlock block => block.Statements,
        BoundLocalDecl declaration => [declaration.Initializer],
        BoundExprStmt statement => [statement.Expression],
        BoundIf branch => [branch.Condition, branch.Then, branch.Else],
        BoundWhile loop => [loop.Condition, loop.Body],
        BoundFor loop => [loop.Init, loop.Condition, loop.Body, loop.Update],
        BoundReturn statement => [statement.Value],
        BoundUsing scope => [scope.ResourceInit, scope.Body],
        BoundByRefArg argument => [argument.Lvalue],
        BoundFieldAccess field => [field.Receiver],
        BoundCall call => [call.Receiver, .. call.Arguments],
        BoundNewObject allocation => allocation.Arguments,
        BoundNewArray array => [array.Size],
        BoundIndex index => [index.Receiver, index.Index],
        BoundWeakRefNew weak => [weak.Target],
        BoundWeakRefTryGet get => [get.WeakRef, get.OutTarget],
        BoundWeakRefSet set => [set.WeakRef, set.Target],
        BoundBinary binary => [binary.Left, binary.Right],
        BoundUnary unary => [unary.Operand],
        BoundAssign assignment => [assignment.Target, assignment.Value],
        BoundConversion conversion => [conversion.Operand],
        BoundIs test => [test.Operand],
        BoundAs cast => [cast.Operand],
        BoundLength length => [length.Receiver],
        BoundConsoleCall call => [call.Argument],
        BoundLiteral or BoundLocal or BoundParam or BoundThis => [],
        _ => throw new InvalidOperationException($"Missing lifetime traversal for {node.GetType().Name}")
    };
}
