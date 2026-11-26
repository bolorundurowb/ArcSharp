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
    private readonly HashSet<BoundLocal> _moves = [];
    private readonly HashSet<BoundLocal> _argumentMoves = [];

    public bool IsBorrowed(BoundExpr value) =>
        _ownership.TryGetValue(value, out var ownership) && ownership == ReferenceOwnership.Borrowed;

    public bool IsMove(BoundLocal value) => _moves.Contains(value);

    public bool IsArgumentMove(BoundExpr value) => value is BoundLocal local && _argumentMoves.Contains(local);

    public IEnumerable<LocalSymbol> ArgumentOwners(BoundMethodBody body) =>
        body.AllLocals.Where(symbol => _argumentMoves.Any(move => move.Symbol == symbol));

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
            // A closure can outlive the call that built it. Its captures must
            // keep their own ARC ownership even when the source slot is stable.
            var captures = nodes.OfType<BoundNewObject>().Where(allocation => allocation.CapturesArguments)
                .SelectMany(allocation => allocation.Arguments.SelectMany(Walk)).OfType<BoundExpr>().ToHashSet();

            // The walk is post-order, so a conversion inherits stability
            // only after its operand has been checked.
            foreach (var value in nodes.OfType<BoundExpr>().Where(value => unique.Contains(value) && !captures.Contains(value)))
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

            // A finally block runs after the return value is stored, and it can
            // still read the local. Moving the local's reference into the return
            // slot would leave it null there, so a return inside a try keeps
            // its ordinary read.
            var returnsInTry = nodes.OfType<BoundTry>().SelectMany(statement => Walk(statement))
                .OfType<BoundReturn>().ToHashSet();

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
                    case BoundReturn { Value: BoundLocal local } returned
                        when stableReads.Contains(local) && !returnsInTry.Contains(returned):
                        result._moves.Add(local);
                        result._ownership[local] = ReferenceOwnership.Owned;
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

            // The last top-level statement has no successor that can read the
            // source. A fresh destination with a later exit-cleanup position
            // keeps the object alive at least as long as the original slot.
            switch (body.Body.Statements.LastOrDefault())
            {
                case BoundLocalDecl { Initializer: BoundLocal source } declaration
                    when stableReads.Contains(source) && body.AllLocals.IndexOf(source.Symbol) >= 0
                        && body.AllLocals.IndexOf(source.Symbol) < body.AllLocals.IndexOf(declaration.Symbol):
                    result._moves.Add(source);
                    result._ownership[source] = ReferenceOwnership.Owned;
                    break;
                case BoundExprStmt { Expression: BoundCall { Arguments: [BoundLocal argument] } call }
                    when call.Method.IsStatic && result.IsBorrowed(argument):
                    result._argumentMoves.Add(argument);
                    result._ownership[argument] = ReferenceOwnership.Owned;
                    break;
            }

            BorrowConfinedReads(body.Body, nodes, bodies, stableReads, result);

            if (trace != null)
            {
                trace.WriteLine($"[ownership] {body.Method.MangledName}");
                foreach (var value in nodes.OfType<BoundExpr>().Where(value => value.Type.IsReferenceType))
                {
                    var ownership = result._ownership[value].ToString().ToLowerInvariant();
                    var action = value is BoundLocal local && (result.IsMove(local) || result.IsArgumentMove(local))
                        ? " move" : "";
                    trace.WriteLine($"  {ownership,-8} {value.GetType().Name} {value.Type.Name}{action}");
                }
            }

        }

        return result;
    }

    private static void BorrowConfinedReads(BoundBlock block, object[] nodes,
        Dictionary<MethodSymbol, object[]> bodies, HashSet<BoundExpr> stableReads, LifetimeAnalysis result)
    {
        var addressTargets = nodes.OfType<BoundAssign>().Select(assignment => assignment.Target).ToHashSet();
        addressTargets.UnionWith(nodes.OfType<BoundByRefArg>().Select(argument => argument.Lvalue));
        addressTargets.UnionWith(nodes.OfType<BoundWeakRefTryGet>().Select(get => get.OutTarget));
        addressTargets.UnionWith(nodes.OfType<BoundDelegateCompound>().Select(compound => compound.Target));

        var reads = nodes.Select(node => node switch
        {
            BoundIs test => test.Operand,
            BoundLength length => length.Receiver,
            BoundIndex index when !addressTargets.Contains(index) => index.Receiver,
            BoundFieldAccess field when field.Type.Kind != TypeKind.Struct && !addressTargets.Contains(field) =>
                field.Receiver,
            _ => null
        }).OfType<BoundLocal>().ToHashSet();

        var usesByLocal = nodes.OfType<BoundLocal>().ToLookup(local => local.Symbol);
        foreach (var declaration in block.Statements.OfType<BoundLocalDecl>())
        {
            // A constructor with executable code could publish this before any local read.
            var confinedAllocation = declaration.Initializer switch
            {
                BoundNewArray => true,
                BoundNewObject { Type.IsReferenceType: true, Ctor: null } => true,
                BoundNewObject { Type.IsReferenceType: true, Ctor: { } ctor } =>
                    bodies.TryGetValue(ctor, out var constructor) && constructor is [BoundBlock],
                _ => false
            };

            var uses = usesByLocal[declaration.Symbol];
            if (confinedAllocation && uses.All(use => reads.Contains(use) && stableReads.Contains(use)))
            {
                // The slot keeps its original +1 and is released at func.exit.
                foreach (var use in uses)
                {
                    result.Borrow(use);
                }
            }
        }
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
        BoundSwitch selection => [selection.Value, .. selection.Sections],
        BoundSwitchSection section => [.. section.Tests, section.Body],
        BoundBranch => [],
        BoundReturn statement => [statement.Value],
        BoundUsing scope => [scope.ResourceInit, scope.Body],
        BoundThrow statement => [statement.Value],
        BoundTry statement => [statement.Body, .. statement.Catches.Select(clause => clause.Body), statement.Finally],
        BoundByRefArg argument => [argument.Lvalue],
        BoundFieldAccess field => [field.Receiver],
        BoundCall call => [call.Receiver, .. call.Arguments],
        BoundNewObject allocation => allocation.Arguments,
        BoundNewArray array => [array.Size, .. array.Initializers],
        BoundNamedArgument argument => [argument.Value],
        BoundIndex index => [index.Receiver, index.Index],
        BoundArraySlice slice => [slice.Receiver, slice.Start, slice.Length],
        BoundWeakRefNew weak => [weak.Target],
        BoundWeakRefTryGet get => [get.WeakRef, get.OutTarget],
        BoundWeakRefSet set => [set.WeakRef, set.Target],
        BoundBinary binary => [binary.Left, binary.Right],
        BoundUnary unary => [unary.Operand],
        BoundAssign assignment => [assignment.Target, assignment.Value],
        BoundIncrement increment => [increment.Target],
        BoundSequence sequence => [.. sequence.Effects, sequence.Value],
        BoundOperatorUpdate update => [update.Target, update.Result],
        BoundNumericCompound compound => [compound.Target, compound.Right],
        BoundStringCompound compound => [compound.Target, compound.Right],
        BoundDelegateCompound compound => [compound.Target, compound.Right],
        BoundConversion conversion => [conversion.Operand],
        BoundIs test => [test.Operand],
        BoundUnbox unbox => [unbox.Operand],
        BoundIdentityHash identity => [identity.Operand],
        BoundPatternTest pattern => [pattern.Value, pattern.Test],
        BoundPatternBinding binding => [binding.Test, binding.Value],
        BoundPatternInput => [],
        BoundChoice choice => [choice.Condition, choice.WhenTrue, choice.WhenFalse],
        BoundSwitchFailure failure => [failure.Exception],
        BoundAs cast => [cast.Operand],
        BoundSameType same => [same.Left, same.Right],
        BoundDecimalOp decimalOp => decimalOp.Arguments,
        BoundLength length => [length.Receiver],
        BoundStringOp text => [text.Receiver, .. text.Arguments],
        BoundConsoleCall call => [call.Argument],
        BoundGcCall call => [call.Argument],
        BoundLiteral or BoundLocal or BoundParam or BoundThis or BoundFunctionAddress => [],
        _ => throw new InvalidOperationException($"Missing lifetime traversal for {node.GetType().Name}")
    };
}
