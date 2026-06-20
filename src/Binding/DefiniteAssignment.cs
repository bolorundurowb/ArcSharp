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
            new Analyzer(body.Method.Syntax?.Line ?? 0, report).Run(body.Body);
    }

    private sealed class Analyzer
    {
        private readonly int _line;
        private readonly Action<int, string> _report;
        private readonly HashSet<int> _reported = new();   // one warning per local

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
                        if (!VisitStmt(st, assigned)) return false;
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
                        return true;   // fall-through keeps only the pre-if assignments
                    var elseSet = new HashSet<int>(assigned);
                    bool elseC = VisitStmt(i.Else, elseSet);
                    if (thenC && elseC) { thenSet.IntersectWith(elseSet); Replace(assigned, thenSet); }
                    else if (thenC) Replace(assigned, thenSet);
                    else if (elseC) Replace(assigned, elseSet);
                    return thenC || elseC;
                }

                case BoundWhile w:
                {
                    VisitExpr(w.Condition, assigned);
                    VisitStmt(w.Body, new HashSet<int>(assigned));   // body may run zero times
                    return true;
                }

                case BoundFor f:
                {
                    if (f.Init != null) VisitStmt(f.Init, assigned);
                    if (f.Condition != null) VisitExpr(f.Condition, assigned);
                    var bodySet = new HashSet<int>(assigned);
                    VisitStmt(f.Body, bodySet);
                    if (f.Update != null) VisitExpr(f.Update, bodySet);
                    return true;
                }

                case BoundReturn r:
                    if (r.Value != null) VisitExpr(r.Value, assigned);
                    return false;

                default:
                    return true;
            }
        }

        private static void Replace(HashSet<int> dst, HashSet<int> src)
        {
            dst.Clear();
            foreach (var x in src) dst.Add(x);
        }

        // Visits an expression in evaluation order. Local reads are checked here;
        // local writes (assignment targets, out-targets) mark the local assigned.
        private void VisitExpr(BoundExpr e, HashSet<int> assigned)
        {
            switch (e)
            {
                case BoundLocal lo:
                    if (!assigned.Contains(lo.Symbol.Id) && _reported.Add(lo.Symbol.Id))
                        _report(_line, $"local '{lo.Symbol.Name}' may be used before assignment");
                    break;

                case BoundAssign a:
                    VisitExpr(a.Value, assigned);
                    if (a.Target is BoundLocal tl) assigned.Add(tl.Symbol.Id);
                    else VisitExpr(a.Target, assigned);
                    break;

                case BoundWeakRefTryGet wt:
                    VisitExpr(wt.WeakRef, assigned);
                    if (wt.OutTarget is BoundLocal ol) assigned.Add(ol.Symbol.Id);
                    else VisitExpr(wt.OutTarget, assigned);
                    break;

                case BoundFieldAccess fa:
                    if (fa.Receiver != null) VisitExpr(fa.Receiver, assigned);
                    break;

                case BoundCall c:
                    if (c.Receiver != null) VisitExpr(c.Receiver, assigned);
                    foreach (var arg in c.Arguments) VisitExpr(arg, assigned);
                    break;

                case BoundNewObject n:
                    foreach (var arg in n.Arguments) VisitExpr(arg, assigned);
                    break;

                case BoundNewArray na: VisitExpr(na.Size, assigned); break;
                case BoundIndex ix: VisitExpr(ix.Receiver, assigned); VisitExpr(ix.Index, assigned); break;
                case BoundBinary b: VisitExpr(b.Left, assigned); VisitExpr(b.Right, assigned); break;
                case BoundUnary u: VisitExpr(u.Operand, assigned); break;
                case BoundConversion cv: VisitExpr(cv.Operand, assigned); break;
                case BoundLength len: VisitExpr(len.Receiver, assigned); break;
                case BoundConsoleCall cc: if (cc.Argument != null) VisitExpr(cc.Argument, assigned); break;
                case BoundWeakRefNew wn: VisitExpr(wn.Target, assigned); break;
                case BoundWeakRefSet ws: VisitExpr(ws.WeakRef, assigned); VisitExpr(ws.Target, assigned); break;
                // BoundLiteral, BoundParam, BoundThis read no locals.
            }
        }
    }
}
