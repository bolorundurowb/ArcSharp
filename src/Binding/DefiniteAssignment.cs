namespace ArcSharp.Binding;

// Conservative definite-assignment flow analysis over the bound tree.
//
// Reports when a local may be read before it has been assigned on some path.
// The analysis errs toward silence: where control flow is ambiguous it keeps
// fewer locals "assigned" and only the clearly-unassigned reads are flagged,
// so it never rejects a program that is actually valid. Loops are treated as
// possibly-zero-iteration (assignments inside a body are not guaranteed after
// it), matching C# definite-assignment rules.
//
// Bound expressions do not carry source positions, so diagnostics are reported
// at the enclosing method's declaration line.
public static class DefiniteAssignment
{
    public static void Analyze(BoundProgram program, Action<int, string> report)
    {
        foreach (var body in program.MethodBodies)
        {
            new Analyzer(body.Method.DeclarationLine, report).Run(body.Body);
        }
    }

    private sealed class Analyzer
    {
        private readonly int _line;
        private readonly Action<int, string> _report;
        private readonly HashSet<int> _reported = new();   // one warning per local

        private sealed class BranchPaths
        {
            public bool IsLoop { get; init; }
            public List<HashSet<int>> Break { get; } = [];
            public List<HashSet<int>> Continue { get; } = [];
        }

        private readonly Stack<BranchPaths> _branchTargets = [];

        public Analyzer(int line, Action<int, string> report) { _line = line; _report = report; }

        public void Run(BoundBlock body) => VisitStmt(body, new HashSet<int>());

        // Returns true if control can fall through (complete normally) after the
        // statement; mutates `assigned` to the locals definitely assigned on that
        // fall-through path.
        private bool VisitStmt(BoundStmt s, HashSet<int> assigned)
        {
            switch (s)
            {
                case BoundBlock b:
                    foreach (var st in b.Statements)
                    {
                        if (!VisitStmt(st, assigned))
                        {
                            return false;
                        }
                    }

                    return true;

                case BoundLocalDecl d:
                    if (d.Initializer != null) { VisitExpr(d.Initializer, assigned); assigned.Add(d.Symbol.Id); }
                    return true;

                case BoundExprStmt e:
                    VisitExpr(e.Expression, assigned);
                    return true;

                case BoundIf i:
                    {
                        VisitExpr(i.Condition, assigned);
                        var thenSet = new HashSet<int>(assigned);
                        bool thenC = VisitStmt(i.Then, thenSet);
                        if (i.Else == null)
                        {
                            return true;   // fall-through keeps only the pre-if assignments
                        }

                        var elseSet = new HashSet<int>(assigned);
                        bool elseC = VisitStmt(i.Else, elseSet);
                        if (thenC && elseC) { thenSet.IntersectWith(elseSet); Replace(assigned, thenSet); }
                        else if (thenC)
                        {
                            Replace(assigned, thenSet);
                        }
                        else if (elseC)
                        {
                            Replace(assigned, elseSet);
                        }

                        return thenC || elseC;
                    }

                case BoundWhile w:
                    {
                        if (w.ConditionIsTop)
                        {
                            VisitExpr(w.Condition, assigned);
                            VisitLoopBody(w.Body, assigned);
                            return true;
                        }

                        var paths = VisitLoopBody(w.Body, assigned);
                        var conditionSet = MergePaths(paths.Continue);
                        if (conditionSet != null)
                        {
                            VisitExpr(w.Condition, conditionSet);
                            paths.Break.Add(conditionSet);
                        }

                        var exitSet = MergePaths(paths.Break);
                        if (exitSet == null)
                        {
                            return false;
                        }

                        Replace(assigned, exitSet);
                        return true;
                    }

                case BoundFor f:
                    {
                        if (f.Init != null)
                        {
                            VisitStmt(f.Init, assigned);
                        }

                        if (f.Condition != null)
                        {
                            VisitExpr(f.Condition, assigned);
                        }

                        var paths = VisitLoopBody(f.Body, assigned);
                        if (f.Update != null && MergePaths(paths.Continue) is { } updateSet)
                        {
                            VisitExpr(f.Update, updateSet);
                        }

                        return true;
                    }

                case BoundSwitch selection:
                    return VisitSwitch(selection, assigned);

                case BoundBranch branch:
                    var target = branch.IsContinue
                        ? _branchTargets.First(candidate => candidate.IsLoop)
                        : _branchTargets.Peek();
                    (branch.IsContinue ? target.Continue : target.Break).Add(new HashSet<int>(assigned));
                    return false;

                case BoundReturn r:
                    if (r.Value != null)
                    {
                        VisitExpr(r.Value, assigned);
                    }

                    return false;

                case BoundUsing u:
                    if (u.ResourceInit != null)
                    {
                        VisitExpr(u.ResourceInit, assigned);
                    }

                    assigned.Add(u.Resource.Id);
                    return VisitStmt(u.Body, assigned);

                case BoundThrow t:
                    if (t.Value != null)
                    {
                        VisitExpr(t.Value, assigned);
                    }

                    return false;

                case BoundTry t:
                    return VisitTry(t, assigned);

                default:
                    return true;
            }
        }

        private bool VisitSwitch(BoundSwitch selection, HashSet<int> assigned)
        {
            VisitExpr(selection.Value, assigned);
            var paths = new BranchPaths();
            _branchTargets.Push(paths);
            foreach (var section in selection.Sections)
            {
                var sectionSet = new HashSet<int>(assigned);
                foreach (var label in section.Tests)
                {
                    VisitExpr(label, sectionSet);
                }

                if (VisitStmt(section.Body, sectionSet))
                {
                    paths.Break.Add(sectionSet);
                }
            }

            _branchTargets.Pop();
            if (!selection.Sections.Any(section => section.IsDefault))
            {
                paths.Break.Add(assigned);
            }

            if (MergePaths(paths.Break) is not { } exitSet)
            {
                return false;
            }

            Replace(assigned, exitSet);
            return true;
        }

        private BranchPaths VisitLoopBody(BoundStmt body, HashSet<int> assigned)
        {
            var paths = new BranchPaths { IsLoop = true };
            var bodySet = new HashSet<int>(assigned);
            _branchTargets.Push(paths);
            if (VisitStmt(body, bodySet))
            {
                paths.Continue.Add(bodySet);
            }

            _branchTargets.Pop();
            return paths;
        }

        private static void ApplyFinally(List<HashSet<int>> paths, int first, bool completes, HashSet<int> assigned)
        {
            if (!completes)
            {
                paths.RemoveRange(first, paths.Count - first);
                return;
            }

            for (var i = first; i < paths.Count; i++)
            {
                paths[i].UnionWith(assigned);
            }
        }

        private static HashSet<int>? MergePaths(List<HashSet<int>> paths)
        {
            if (paths.Count == 0)
            {
                return null;
            }

            var merged = new HashSet<int>(paths[0]);
            foreach (var path in paths.Skip(1))
            {
                merged.IntersectWith(path);
            }

            return merged;
        }

        // A catch clause can start after any part of the body has run, so it
        // sees only what was assigned before the try. What is assigned after the
        // statement is what every path that completes has in common, plus
        // whatever the finally block assigns.
        private bool VisitTry(BoundTry t, HashSet<int> assigned)
        {
            var before = new HashSet<int>(assigned);
            var paths = new List<HashSet<int>>();
            var branches = t.Finally == null
                ? []
                : _branchTargets.Select(target => (Target: target, Break: target.Break.Count, Continue: target.Continue.Count)).ToArray();

            var bodySet = new HashSet<int>(before);
            if (VisitStmt(t.Body, bodySet))
            {
                paths.Add(bodySet);
            }

            foreach (var clause in t.Catches)
            {
                var catchSet = new HashSet<int>(before);
                if (clause.Variable != null)
                {
                    catchSet.Add(clause.Variable.Id);
                }

                if (VisitStmt(clause.Body, catchSet))
                {
                    paths.Add(catchSet);
                }
            }

            HashSet<int> after = paths.Count > 0 ? paths[0] : [];
            foreach (var path in paths.Skip(1))
            {
                after.IntersectWith(path);
            }

            if (t.Finally != null)
            {
                var finallySet = new HashSet<int>(before);
                var completes = VisitStmt(t.Finally, finallySet);
                foreach (var (target, breakCount, continueCount) in branches)
                {
                    ApplyFinally(target.Break, breakCount, completes, finallySet);
                    ApplyFinally(target.Continue, continueCount, completes, finallySet);
                }

                if (!completes)
                {
                    return false;
                }

                after.UnionWith(finallySet);
            }

            if (paths.Count == 0)
            {
                return false;
            }

            Replace(assigned, after);
            return true;
        }

        private static void Replace(HashSet<int> dst, HashSet<int> src)
        {
            dst.Clear();
            foreach (var x in src)
            {
                dst.Add(x);
            }
        }

        private void VisitArguments(List<BoundExpr> arguments, HashSet<int> assigned)
        {
            var outputs = new List<int>();
            foreach (var argument in arguments)
            {
                var value = argument is BoundNamedArgument named ? named.Value : argument;
                if (value is BoundByRefArg { Kind: ArcSharp.Syntax.RefKind.Out, Lvalue: BoundLocal local })
                {
                    outputs.Add(local.Symbol.Id);
                }
                else
                {
                    VisitExpr(value, assigned);
                }
            }

            assigned.UnionWith(outputs);
        }

        // Visits an expression in evaluation order. Local reads are checked here;
        // local writes (assignment targets, out-targets) mark the local assigned.
        private void VisitExpr(BoundExpr e, HashSet<int> assigned)
        {
            switch (e)
            {
                case BoundLocal lo:
                    if (!assigned.Contains(lo.Symbol.Id) && _reported.Add(lo.Symbol.Id))
                    {
                        _report(_line, $"local '{lo.Symbol.Name}' may be used before assignment");
                    }

                    break;

                case BoundStringCompound compound:
                    VisitExpr(compound.Target, assigned);
                    VisitExpr(compound.Right, assigned);
                    break;

                case BoundNumericCompound compound:
                    VisitExpr(compound.Target, assigned);
                    VisitExpr(compound.Right, assigned);
                    break;

                case BoundDelegateCompound compound:
                    VisitExpr(compound.Target, assigned);
                    VisitExpr(compound.Right, assigned);
                    break;

                case BoundOperatorUpdate update:
                    VisitExpr(update.Target, assigned);
                    VisitExpr(update.Result, assigned);
                    break;

                case BoundIncrement increment:
                    VisitExpr(increment.Target, assigned);
                    break;

                case BoundSequence sequence:
                    foreach (var effect in sequence.Effects)
                    {
                        VisitExpr(effect, assigned);
                    }

                    VisitExpr(sequence.Value, assigned);
                    break;

                case BoundAssign a:
                    VisitExpr(a.Value, assigned);
                    if (a.Target is BoundLocal tl)
                    {
                        assigned.Add(tl.Symbol.Id);
                    }
                    else if (a.Target is BoundFieldAccess { Receiver: BoundLocal rl2, Receiver.Type.Kind: TypeKind.Struct })
                    {
                        // Writing a field of a struct value assigns the whole
                        // value: the storage starts zeroed, so later reads of
                        // it are safe and must not warn.
                        assigned.Add(rl2.Symbol.Id);
                    }
                    else
                    {
                        VisitExpr(a.Target, assigned);
                    }

                    break;

                case BoundWeakRefTryGet wt:
                    VisitExpr(wt.WeakRef, assigned);
                    if (wt.OutTarget is BoundLocal ol)
                    {
                        assigned.Add(ol.Symbol.Id);
                    }
                    else
                    {
                        VisitExpr(wt.OutTarget, assigned);
                    }

                    break;

                case BoundFieldAccess fa:
                    if (fa.Receiver != null)
                    {
                        VisitExpr(fa.Receiver, assigned);
                    }

                    break;

                case BoundCall c:
                    if (c.Receiver != null)
                    {
                        VisitExpr(c.Receiver, assigned);
                    }

                    VisitArguments(c.Arguments, assigned);
                    break;

                case BoundNewObject n:
                    VisitArguments(n.Arguments, assigned);
                    break;

                case BoundByRefArg argument:
                    VisitExpr(argument.Lvalue, assigned);
                    break;

                case BoundNamedArgument argument:
                    VisitExpr(argument.Value, assigned);
                    break;

                case BoundNewArray na:
                    VisitExpr(na.Size, assigned);
                    foreach (var initializer in na.Initializers)
                    {
                        VisitExpr(initializer, assigned);
                    }

                    break;
                case BoundArraySlice slice:
                    VisitExpr(slice.Receiver, assigned);
                    VisitExpr(slice.Start, assigned);
                    VisitExpr(slice.Length, assigned);
                    break;

                case BoundIndex ix: VisitExpr(ix.Receiver, assigned); VisitExpr(ix.Index, assigned); break;
                case BoundBinary b: VisitExpr(b.Left, assigned); VisitExpr(b.Right, assigned); break;
                case BoundUnary u: VisitExpr(u.Operand, assigned); break;
                case BoundConversion cv: VisitExpr(cv.Operand, assigned); break;
                case BoundUnbox unbox: VisitExpr(unbox.Operand, assigned); break;
                case BoundIdentityHash identity: VisitExpr(identity.Operand, assigned); break;
                case BoundChoice choice:
                    VisitExpr(choice.Condition, assigned);
                    var whenTrue = new HashSet<int>(assigned);
                    var whenFalse = new HashSet<int>(assigned);
                    VisitExpr(choice.WhenTrue, whenTrue);
                    VisitExpr(choice.WhenFalse, whenFalse);
                    whenTrue.IntersectWith(whenFalse);
                    Replace(assigned, whenTrue);
                    break;

                case BoundSwitchFailure failure:
                    VisitExpr(failure.Exception, assigned);
                    break;

                case BoundPatternTest pattern:
                    VisitExpr(pattern.Value, assigned);
                    VisitExpr(pattern.Test, assigned);
                    break;

                case BoundPatternBinding binding:
                    // Roslyn already validates the success-path scope of pattern locals.
                    VisitExpr(binding.Test, assigned);
                    VisitExpr(binding.Value, assigned);
                    assigned.Add(binding.Local.Id);
                    break;
                case BoundLength len: VisitExpr(len.Receiver, assigned); break;
                case BoundStringOp text:
                    if (text.Receiver != null) { VisitExpr(text.Receiver, assigned); }
                    foreach (var argument in text.Arguments) { VisitExpr(argument, assigned); }
                    break;
                case BoundSameType same: VisitExpr(same.Left, assigned); VisitExpr(same.Right, assigned); break;
                case BoundDecimalOp decimalOp:
                    foreach (var argument in decimalOp.Arguments)
                    {
                        VisitExpr(argument, assigned);
                    }

                    break;
                case BoundConsoleCall cc: if (cc.Argument != null) { VisitExpr(cc.Argument, assigned); } break;
                case BoundGcCall gc: if (gc.Argument != null) { VisitExpr(gc.Argument, assigned); } break;
                case BoundWeakRefNew wn: VisitExpr(wn.Target, assigned); break;
                case BoundWeakRefSet ws: VisitExpr(ws.WeakRef, assigned); VisitExpr(ws.Target, assigned); break;
                    // BoundLiteral, BoundParam, BoundThis read no locals.
            }
        }
    }
}
