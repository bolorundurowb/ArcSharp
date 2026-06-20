using System.Text;
using ArcSharp.Lexing;
using ArcSharp.Binding;

namespace ArcSharp.CodeGen;

public sealed class Emitter(BoundProgram program, string triple, bool boundsChecks = true)
{
    private readonly BoundProgram _program = program;
    private readonly string _triple = triple;
    private readonly bool _boundsChecks = boundsChecks;

    private readonly StringBuilder _mod = new();
    private readonly List<string> _strings = [];

    private List<string> _entry = [];
    private List<string> _body = [];
    private readonly List<string> _stmtTemps = [];
    private int _tmp, _lbl, _strId;
    private bool _terminated;
    private MethodSymbol _curMethod = null!;
    private readonly Dictionary<int, string> _localSlot = [];

    private string T() => "%t" + (_tmp++);
    private string L(string p) => p + (_lbl++);

    private string Inst(string rhs)
    {
        EnsureBlock();
        var t = T();
        _body.Add($"  {t} = {rhs}");
        return t;
    }

    private void Do(string s)
    {
        EnsureBlock();
        _body.Add($"  {s}");
    }

    private void Term(string s)
    {
        EnsureBlock();
        _body.Add($"  {s}");
        _terminated = true;
    }

    private void Lbl(string l)
    {
        _body.Add($"{l}:");
        _terminated = false;
    }

    private void EnsureBlock()
    {
        if (_terminated)
        {
            var l = L("dead");
            _body.Add($"{l}:");
            _terminated = false;
        }
    }

    public string Emit()
    {
        _mod.AppendLine("; ArcSharp generated module");
        _mod.AppendLine($"target triple = \"{_triple}\"");
        _mod.AppendLine();
        _mod.AppendLine("%TypeInfo = type { i8*, i64, void (i8*)*, i8**, i64, i8**, i64 }");
        _mod.AppendLine();
        EmitRuntimeDecls();
        _mod.AppendLine();

        var fns = new StringBuilder();

        foreach (var t in _program.Types)
        {
            if (t.Kind == TypeKind.Class)
            {
                EmitTypeInfo(t, fns);
            }
        }

        foreach (var t in _program.Types)
        {
            foreach (var f in t.StaticFields)
            {
                _mod.AppendLine($"{f.MangledStatic} = global {f.Type.LlvmType} {ZeroOf(f.Type)}");
            }
        }

        _mod.AppendLine();

        foreach (var b in _program.MethodBodies)
        {
            EmitMethod(b, fns);
        }

        EmitMain(fns);

        foreach (var s in _strings)
        {
            _mod.AppendLine(s);
        }

        return _mod.ToString() + "\n" + fns.ToString();
    }

    private void EmitRuntimeDecls()
    {
        foreach (var d in new[]
                 {
                     "declare i8* @arc_alloc(%TypeInfo*)",
                     "declare void @arc_retain(i8*)",
                     "declare void @arc_release(i8*)",
                     "declare void @arc_assign_take(i8**, i8*)",
                     "declare void @arc_store_strong(i8**, i8*)",
                     "declare void @arc_store_weak(i8**, i8*)",
                     "declare i8* @arc_load_weak(i8**)",
                     "declare i8* @arc_weakref_new(i8*)",
                     "declare i8* @arc_weakref_try_get(i8*)",
                     "declare void @arc_weakref_set(i8*, i8*)",
                     "declare i8* @arc_array_new(i64, i32)",
                     "declare i64 @arc_array_length(i8*)",
                     "declare void @arc_bounds_check(i8*, i64)",
                     "declare i8* @arc_str_lit(i8*, i64)",
                     "declare i64 @arc_str_length(i8*)",
                     "declare i8* @arc_str_concat(i8*, i8*)",
                     "declare i8* @arc_str_from_int(i64)",
                     "declare i8* @arc_str_from_bool(i1)",
                     "declare i8* @arc_str_from_float(float)",
                     "declare i8* @arc_str_from_double(double)",
                     "declare void @arc_console_write(i8*, i32)",
                     "declare void @arc_console_write_int(i64, i32)",
                     "declare void @arc_console_write_bool(i1, i32)",
                     "declare void @arc_console_write_float(float, i32)",
                     "declare void @arc_console_write_double(double, i32)",
                     "declare void @arc_console_newline()",
                     "declare void @arc_report()",
                 })
        {
            _mod.AppendLine(d);
        }
    }

    private static string ZeroOf(TypeSymbol t) => t.IsReferenceType ? "null" : (IsFloatType(t) ? "0.0" : "0");
    private static bool IsFloatType(TypeSymbol t) => t.LlvmType is "float" or "double";

    private void EmitTypeInfo(TypeSymbol t, StringBuilder fns)
    {
        var nameLen = t.Name.Length + 1;
        _mod.AppendLine($"@{t.Name}__name = private constant [{nameLen} x i8] c\"{t.Name}\\00\"");

        var k = t.Vtable.Count;
        if (k > 0)
        {
            var entries = t.Vtable.Select(m => $"i8* bitcast ({FnPtrType(m)} @{m.MangledName} to i8*)");
            _mod.AppendLine($"@{t.Name}__vtable = private constant [{k} x i8*] [ {string.Join(", ", entries)} ]");
        }

        var s = _program.InterfaceSelectorCount;
        if (s > 0)
        {
            var entries = new List<string>();
            for (var sel = 0; sel < s; sel++)
            {
                if (t.InterfaceImpl.TryGetValue(sel, out var m))
                {
                    entries.Add($"i8* bitcast ({FnPtrType(m)} @{m.MangledName} to i8*)");
                }
                else
                {
                    entries.Add("i8* null");
                }
            }

            _mod.AppendLine($"@{t.Name}__itable = private constant [{s} x i8*] [ {string.Join(", ", entries)} ]");
        }

        var size = 24 + t.InstanceFields.Count * 8;
        var vtExpr = k > 0
            ? $"i8** getelementptr ([{k} x i8*], [{k} x i8*]* @{t.Name}__vtable, i64 0, i64 0)"
            : "i8** null";
        var itExpr = s > 0
            ? $"i8** getelementptr ([{s} x i8*], [{s} x i8*]* @{t.Name}__itable, i64 0, i64 0)"
            : "i8** null";

        _mod.AppendLine(
            $"@{t.Name}__ti = constant %TypeInfo {{ " +
            $"i8* getelementptr ([{nameLen} x i8], [{nameLen} x i8]* @{t.Name}__name, i64 0, i64 0), " +
            $"i64 {size}, void (i8*)* @{t.Name}__deinit, {vtExpr}, i64 {k}, {itExpr}, i64 {s} }}");

        fns.AppendLine($"define void @{t.Name}__deinit(i8* %self) {{");
        fns.AppendLine("entry:");
        foreach (var f in t.InstanceFields)
        {
            if (!f.Type.IsReferenceType)
            {
                continue;
            }

            fns.AppendLine($"  %p{f.Index} = getelementptr i8, i8* %self, i64 {f.ByteOffset}");
            fns.AppendLine($"  %pp{f.Index} = bitcast i8* %p{f.Index} to i8**");
            if (f.IsWeak)
            {
                fns.AppendLine($"  call void @arc_store_weak(i8** %pp{f.Index}, i8* null)");
            }
            else
            {
                fns.AppendLine($"  %v{f.Index} = load i8*, i8** %pp{f.Index}");
                fns.AppendLine($"  call void @arc_release(i8* %v{f.Index})");
            }
        }

        fns.AppendLine("  ret void");
        fns.AppendLine("}");
        fns.AppendLine();
    }

    private static string FnPtrType(MethodSymbol m)
    {
        var ps = new List<string> { "i8*" };
        foreach (var p in m.Parameters)
        {
            ps.Add(p.Type.LlvmType);
        }

        return $"{m.ReturnType.LlvmType} ({string.Join(", ", ps)})*";
    }

    private void EmitMethod(BoundMethodBody b, StringBuilder fns)
    {
        _curMethod = b.Method;
        _entry = [];
        _body = [];
        _stmtTemps.Clear();
        _localSlot.Clear();
        _tmp = 0;
        _lbl = 0;
        _terminated = false;

        var m = b.Method;
        var ps = new List<string>();
        if (!m.IsStatic)
        {
            ps.Add("i8* %this");
        }

        foreach (var p in m.Parameters)
        {
            ps.Add($"{p.Type.LlvmType} %a{p.Index}");
        }

        if (!m.IsStatic)
        {
            _entry.Add("  %this.addr = alloca i8*");
            _entry.Add("  store i8* %this, i8** %this.addr");
        }

        foreach (var p in m.Parameters)
        {
            _entry.Add($"  %arg{p.Index} = alloca {p.Type.LlvmType}");
            _entry.Add($"  store {p.Type.LlvmType} %a{p.Index}, {p.Type.LlvmType}* %arg{p.Index}");
        }

        foreach (var loc in b.AllLocals)
        {
            var slot = $"%loc{loc.Id}";
            _localSlot[loc.Id] = slot;
            _entry.Add($"  {slot} = alloca {loc.Type.LlvmType}");
            if (loc.Type.IsReferenceType)
            {
                _entry.Add($"  store i8* null, i8** {slot}");
            }
        }

        var isVoid = m.ReturnType.Kind == TypeKind.Void;
        if (!isVoid)
        {
            _entry.Add($"  %retval = alloca {m.ReturnType.LlvmType}");
            if (m.ReturnType.IsReferenceType)
            {
                _entry.Add("  store i8* null, i8** %retval");
            }
        }

        EmitStmt(b.Body);

        if (!_terminated)
        {
            if (!isVoid)
            {
                StoreRet(DefaultVal(m.ReturnType), m.ReturnType);
            }
        }

        Term("br label %func.exit");

        Lbl("func.exit");
        foreach (var loc in b.AllLocals)
        {
            if (loc.Type.IsReferenceType)
            {
                var v = Inst($"load i8*, i8** %loc{loc.Id}");
                Do($"call void @arc_release(i8* {v})");
            }
        }

        if (isVoid)
        {
            Term("ret void");
        }
        else
        {
            var r = Inst($"load {m.ReturnType.LlvmType}, {m.ReturnType.LlvmType}* %retval");
            Term($"ret {m.ReturnType.LlvmType} {r}");
        }

        fns.AppendLine($"define {m.ReturnType.LlvmType} @{m.MangledName}({string.Join(", ", ps)}) {{");
        fns.AppendLine("entry:");
        foreach (var l in _entry)
        {
            fns.AppendLine(l);
        }

        foreach (var l in _body)
        {
            fns.AppendLine(l);
        }

        fns.AppendLine("}");
        fns.AppendLine();
    }

    private static string DefaultVal(TypeSymbol t) => t.IsReferenceType ? "null" : (IsFloatType(t) ? "0.0" : "0");

    private static string FormatFloatConstant(string s)
    {
        // LLVM float/double constants must contain a decimal point or exponent.
        if (s.Contains('.') || s.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            return s;
        }

        return s + ".0";
    }

    private void StoreRet(string val, TypeSymbol t) => Do($"store {t.LlvmType} {val}, {t.LlvmType}* %retval");

    private void EmitStmt(BoundStmt s)
    {
        switch (s)
        {
            case BoundBlock b:
                foreach (var x in b.Statements)
                {
                    EmitStmt(x);
                }

                break;
            case BoundLocalDecl d: EmitLocalDecl(d); break;
            case BoundExprStmt e: EmitExprStmt(e); break;
            case BoundIf i: EmitIf(i); break;
            case BoundWhile w: EmitWhile(w); break;
            case BoundFor f: EmitFor(f); break;
            case BoundReturn r: EmitReturn(r); break;
        }
    }

    private void EmitLocalDecl(BoundLocalDecl d)
    {
        if (d.Initializer == null)
        {
            return;
        }

        var mark = _stmtTemps.Count;
        var v = EmitR(d.Initializer);
        var slot = _localSlot[d.Symbol.Id];
        if (d.Symbol.Type.IsReferenceType)
        {
            Do($"call void @arc_assign_take(i8** {slot}, i8* {v})");
        }
        else
        {
            Do($"store {d.Symbol.Type.LlvmType} {v}, {d.Symbol.Type.LlvmType}* {slot}");
        }

        ReleaseTemps(mark);
    }

    private void EmitExprStmt(BoundExprStmt e)
    {
        var mark = _stmtTemps.Count;
        var v = EmitR(e.Expression);
        if (e.Expression.Type.IsReferenceType)
        {
            _stmtTemps.Add(v);
        }

        ReleaseTemps(mark);
    }

    private void EmitIf(BoundIf s)
    {
        var mark = _stmtTemps.Count;
        var c = EmitR(s.Condition);
        ReleaseTemps(mark);
        string thenL = L("then"), endL = L("endif");
        var elseL = s.Else != null ? L("else") : endL;
        Term($"br i1 {c}, label %{thenL}, label %{elseL}");
        Lbl(thenL);
        EmitStmt(s.Then);
        if (!_terminated)
        {
            Term($"br label %{endL}");
        }

        if (s.Else != null)
        {
            Lbl(elseL);
            EmitStmt(s.Else);
            if (!_terminated)
            {
                Term($"br label %{endL}");
            }
        }

        Lbl(endL);
    }

    private void EmitWhile(BoundWhile s)
    {
        string condL = L("while.cond"), bodyL = L("while.body"), endL = L("while.end");
        Term($"br label %{condL}");
        Lbl(condL);
        var mark = _stmtTemps.Count;
        var c = EmitR(s.Condition);
        ReleaseTemps(mark);
        Term($"br i1 {c}, label %{bodyL}, label %{endL}");
        Lbl(bodyL);
        EmitStmt(s.Body);
        if (!_terminated)
        {
            Term($"br label %{condL}");
        }

        Lbl(endL);
    }

    private void EmitFor(BoundFor s)
    {
        if (s.Init != null)
        {
            EmitStmt(s.Init);
        }

        string condL = L("for.cond"), bodyL = L("for.body"), stepL = L("for.step"), endL = L("for.end");
        Term($"br label %{condL}");
        Lbl(condL);
        if (s.Condition != null)
        {
            var mark = _stmtTemps.Count;
            var c = EmitR(s.Condition);
            ReleaseTemps(mark);
            Term($"br i1 {c}, label %{bodyL}, label %{endL}");
        }
        else
        {
            Term($"br label %{bodyL}");
        }

        Lbl(bodyL);
        EmitStmt(s.Body);
        if (!_terminated)
        {
            Term($"br label %{stepL}");
        }

        Lbl(stepL);
        if (s.Update != null)
        {
            var mark = _stmtTemps.Count;
            EmitR(s.Update);
            ReleaseTemps(mark);
        }

        Term($"br label %{condL}");
        Lbl(endL);
    }

    private void EmitReturn(BoundReturn r)
    {
        var mark = _stmtTemps.Count;
        if (r.Value != null)
        {
            var v = EmitR(r.Value);
            ReleaseTemps(mark);
            StoreRet(v, _curMethod.ReturnType);
        }
        else
        {
            ReleaseTemps(mark);
        }

        Term("br label %func.exit");
    }

    private void ReleaseTemps(int mark)
    {
        for (var i = _stmtTemps.Count - 1; i >= mark; i--)
        {
            Do($"call void @arc_release(i8* {_stmtTemps[i]})");
        }

        _stmtTemps.RemoveRange(mark, _stmtTemps.Count - mark);
    }

    private string EmitR(BoundExpr e)
    {
        switch (e)
        {
            case BoundLiteral l: return EmitLiteral(l);
            case BoundLocal lo: return EmitLoadSlot(_localSlot[lo.Symbol.Id], lo.Type);
            case BoundParam p: return EmitLoadSlot($"%arg{p.Symbol.Index}", p.Type);
            case BoundThis:
            {
                Do("call void @arc_retain(i8* %this)");
                return Inst("load i8*, i8** %this.addr");
            }
            case BoundFieldAccess fa: return EmitFieldRead(fa);
            case BoundLength len: return EmitLength(len);
            case BoundCall c: return EmitCall(c);
            case BoundNewObject n: return EmitNewObject(n);
            case BoundNewArray a: return EmitNewArray(a);
            case BoundIndex ix: return EmitIndexRead(ix);
            case BoundBinary b: return EmitBinary(b);
            case BoundUnary u: return EmitUnary(u);
            case BoundAssign asn: return EmitAssign(asn);
            case BoundConversion cv: return EmitConversion(cv);
            case BoundWeakRefNew wn: return EmitWeakRefNew(wn);
            case BoundWeakRefTryGet wt: return EmitWeakRefTryGet(wt);
            case BoundWeakRefSet ws: return EmitWeakRefSet(ws);
            case BoundConsoleCall cc: return EmitConsole(cc);
            default: return "0";
        }
    }

    private string EmitLiteral(BoundLiteral l)
    {
        switch (l.LitKind)
        {
            case Syntax.LiteralKind.Int: return l.IntValue.ToString();
            case Syntax.LiteralKind.Char: return l.IntValue.ToString();
            case Syntax.LiteralKind.Long: return l.IntValue.ToString();
            case Syntax.LiteralKind.Float:
            {
                var d = FormatFloatConstant(
                    ((double)l.FloatValue).ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Inst($"fptrunc double {d} to float");
            }
            case Syntax.LiteralKind.Double:
                return FormatFloatConstant(l.FloatValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            case Syntax.LiteralKind.Bool: return l.BoolValue ? "1" : "0";
            case Syntax.LiteralKind.Null: return "null";
            case Syntax.LiteralKind.String:
            {
                var bytes = Encoding.UTF8.GetBytes(l.StringValue);
                var len = bytes.Length;
                var sb = new StringBuilder();
                foreach (var bt in bytes)
                {
                    if (bt >= 0x20 && bt < 0x7f && bt != (byte)'"' && bt != (byte)'\\')
                    {
                        sb.Append((char)bt);
                    }
                    else
                    {
                        sb.Append("\\" + bt.ToString("X2"));
                    }
                }

                var g = $"@.str.{_strId++}";
                _strings.Add($"{g} = private constant [{len + 1} x i8] c\"{sb}\\00\"");
                var ptr = Inst($"getelementptr [{len + 1} x i8], [{len + 1} x i8]* {g}, i64 0, i64 0");
                return Inst($"call i8* @arc_str_lit(i8* {ptr}, i64 {len})");
            }
        }

        return "0";
    }

    private string EmitLoadSlot(string slot, TypeSymbol type)
    {
        var v = Inst($"load {type.LlvmType}, {type.LlvmType}* {slot}");
        if (type.IsReferenceType)
        {
            Do($"call void @arc_retain(i8* {v})");
        }

        return v;
    }

    private string FieldAddr(string basePtr, FieldSymbol f)
    {
        var p = Inst($"getelementptr i8, i8* {basePtr}, i64 {f.ByteOffset}");
        return Inst($"bitcast i8* {p} to {f.Type.LlvmType}*");
    }

    private string EmitFieldRead(BoundFieldAccess fa)
    {
        if (fa.Field.IsStatic)
        {
            var g = fa.Field.MangledStatic;
            if (fa.Field.IsWeak)
            {
                return Inst($"call i8* @arc_load_weak(i8** {g})");
            }

            var v = Inst($"load {fa.Field.Type.LlvmType}, {fa.Field.Type.LlvmType}* {g}");
            if (fa.Field.Type.IsReferenceType)
            {
                Do($"call void @arc_retain(i8* {v})");
            }

            return v;
        }

        var recv = EmitR(fa.Receiver!);
        _stmtTemps.Add(recv);
        var addr = FieldAddr(recv, fa.Field);
        if (fa.Field.IsWeak)
        {
            var pp = Inst($"bitcast {fa.Field.Type.LlvmType}* {addr} to i8**");
            return Inst($"call i8* @arc_load_weak(i8** {pp})");
        }

        var val = Inst($"load {fa.Field.Type.LlvmType}, {fa.Field.Type.LlvmType}* {addr}");
        if (fa.Field.Type.IsReferenceType)
        {
            Do($"call void @arc_retain(i8* {val})");
        }

        return val;
    }

    private string EmitLength(BoundLength len)
    {
        var recv = EmitR(len.Receiver);
        _stmtTemps.Add(recv);
        var l = Inst($"call i64 @arc_array_length(i8* {recv})");
        return Inst($"trunc i64 {l} to i32");
    }

    private string EmitCall(BoundCall c)
    {
        string? recv = null;
        if (c.Receiver != null)
        {
            recv = EmitR(c.Receiver);
            _stmtTemps.Add(recv);
        }

        var argVals = new List<string>();
        foreach (var a in c.Arguments)
        {
            var v = EmitR(a);
            if (a.Type.IsReferenceType)
            {
                _stmtTemps.Add(v);
            }

            argVals.Add($"{a.Type.LlvmType} {v}");
        }

        var m = c.Method;
        var ret = m.ReturnType.LlvmType;
        var isVoid = m.ReturnType.Kind == TypeKind.Void;

        if (c.Virtual || c.Interface)
        {
            var tip = Inst($"getelementptr i8, i8* {recv}, i64 16");
            var tipp = Inst($"bitcast i8* {tip} to %TypeInfo**");
            var ti = Inst($"load %TypeInfo*, %TypeInfo** {tipp}");
            var tableField = c.Interface ? 5 : 3;
            var slot = c.Interface ? c.Selector : m.VtableSlot;
            var tabp = Inst($"getelementptr %TypeInfo, %TypeInfo* {ti}, i32 0, i32 {tableField}");
            var tab = Inst($"load i8**, i8*** {tabp}");
            var fpp = Inst($"getelementptr i8*, i8** {tab}, i64 {slot}");
            var fp = Inst($"load i8*, i8** {fpp}");
            var fn = Inst($"bitcast i8* {fp} to {FnPtrType(m)}");
            var callArgs = string.Join(", ", new[] { $"i8* {recv}" }.Concat(argVals));
            if (isVoid)
            {
                Do($"call void {fn}({callArgs})");
                return "";
            }

            return Inst($"call {ret} {fn}({callArgs})");
        }

        var parts = new List<string>();
        if (!m.IsStatic)
        {
            parts.Add($"i8* {recv}");
        }

        parts.AddRange(argVals);
        var args = string.Join(", ", parts);
        if (isVoid)
        {
            Do($"call void @{m.MangledName}({args})");
            return "";
        }

        return Inst($"call {ret} @{m.MangledName}({args})");
    }

    private string EmitNewObject(BoundNewObject n)
    {
        var obj = Inst($"call i8* @arc_alloc(%TypeInfo* @{n.Type.Name}__ti)");
        if (n.Ctor != null)
        {
            var argVals = new List<string>();
            foreach (var a in n.Arguments)
            {
                var v = EmitR(a);
                if (a.Type.IsReferenceType)
                {
                    _stmtTemps.Add(v);
                }

                argVals.Add($"{a.Type.LlvmType} {v}");
            }

            var args = string.Join(", ", new[] { $"i8* {obj}" }.Concat(argVals));
            Do($"call void @{n.Ctor.MangledName}({args})");
        }

        return obj;
    }

    private string EmitNewArray(BoundNewArray a)
    {
        var size = EmitR(a.Size);
        var s64 = Inst($"sext i32 {size} to i64");
        var isRef = a.ElementType.IsReferenceType ? 1 : 0;
        return Inst($"call i8* @arc_array_new(i64 {s64}, i32 {isRef})");
    }

    private string ElemAddr(string arr, string idx, TypeSymbol elem)
    {
        var i64 = Inst($"sext i32 {idx} to i64");
        if (_boundsChecks)
        {
            Do($"call void @arc_bounds_check(i8* {arr}, i64 {i64})");
        }

        var mul = Inst($"mul i64 {i64}, 8");
        var off = Inst($"add i64 {mul}, 32");
        var p = Inst($"getelementptr i8, i8* {arr}, i64 {off}");
        return Inst($"bitcast i8* {p} to {elem.LlvmType}*");
    }

    private string EmitIndexRead(BoundIndex ix)
    {
        var arr = EmitR(ix.Receiver);
        _stmtTemps.Add(arr);
        var idx = EmitR(ix.Index);
        var addr = ElemAddr(arr, idx, ix.Type);
        var v = Inst($"load {ix.Type.LlvmType}, {ix.Type.LlvmType}* {addr}");
        if (ix.Type.IsReferenceType)
        {
            Do($"call void @arc_retain(i8* {v})");
        }

        return v;
    }

    private string EmitBinary(BoundBinary b)
    {
        switch (b.Kind)
        {
            case BinKind.IntArith: return Inst($"{ArithOp(b.Op)} i32 {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.LongArith: return Inst($"{ArithOp(b.Op)} i64 {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.FloatArith: return Inst($"{FloatArithOp(b.Op)} float {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.DoubleArith: return Inst($"{FloatArithOp(b.Op)} double {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.IntCmp: return Inst($"icmp {CmpOp(b.Op)} i32 {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.LongCmp: return Inst($"icmp {CmpOp(b.Op)} i64 {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.FloatCmp: return Inst($"fcmp {FloatCmpOp(b.Op)} float {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.DoubleCmp: return Inst($"fcmp {FloatCmpOp(b.Op)} double {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.RefEq:
            {
                var l = EmitR(b.Left);
                if (b.Left.Type.IsReferenceType)
                {
                    _stmtTemps.Add(l);
                }

                var r = EmitR(b.Right);
                if (b.Right.Type.IsReferenceType)
                {
                    _stmtTemps.Add(r);
                }

                return Inst($"icmp {(b.Op == TokenKind.EqualsEquals ? "eq" : "ne")} i8* {l}, {r}");
            }
            case BinKind.BoolLogic: return EmitLogic(b);
            case BinKind.StrConcat: return EmitConcat(b);
            default: return "0";
        }
    }

    private static string ArithOp(TokenKind op) => op switch
    {
        TokenKind.Plus => "add",
        TokenKind.Minus => "sub",
        TokenKind.Star => "mul",
        TokenKind.Slash => "sdiv",
        TokenKind.Percent => "srem",
        _ => "add"
    };

    private static string FloatArithOp(TokenKind op) => op switch
    {
        TokenKind.Plus => "fadd",
        TokenKind.Minus => "fsub",
        TokenKind.Star => "fmul",
        TokenKind.Slash => "fdiv",
        TokenKind.Percent => "frem",
        _ => "fadd"
    };

    private static string CmpOp(TokenKind op) => op switch
    {
        TokenKind.EqualsEquals => "eq",
        TokenKind.BangEquals => "ne",
        TokenKind.Less => "slt",
        TokenKind.LessEquals => "sle",
        TokenKind.Greater => "sgt",
        TokenKind.GreaterEquals => "sge",
        _ => "eq"
    };

    private static string FloatCmpOp(TokenKind op) => op switch
    {
        TokenKind.EqualsEquals => "oeq",
        TokenKind.BangEquals => "one",
        TokenKind.Less => "olt",
        TokenKind.LessEquals => "ole",
        TokenKind.Greater => "ogt",
        TokenKind.GreaterEquals => "oge",
        _ => "oeq"
    };

    private string EmitLogic(BoundBinary b)
    {
        var slot = Inst("alloca i1");
        var l = EmitR(b.Left);
        Do($"store i1 {l}, i1* {slot}");
        string rhsL = L("logic.rhs"), endL = L("logic.end");
        if (b.Op == TokenKind.AmpAmp)
        {
            Term($"br i1 {l}, label %{rhsL}, label %{endL}");
        }
        else
        {
            Term($"br i1 {l}, label %{endL}, label %{rhsL}");
        }

        Lbl(rhsL);
        var r = EmitR(b.Right);
        Do($"store i1 {r}, i1* {slot}");
        Term($"br label %{endL}");
        Lbl(endL);
        return Inst($"load i1, i1* {slot}");
    }

    private string EmitConcat(BoundBinary b)
    {
        var l = EmitStringOperand(b.Left);
        var r = EmitStringOperand(b.Right);
        _stmtTemps.Add(l);
        _stmtTemps.Add(r);
        return Inst($"call i8* @arc_str_concat(i8* {l}, i8* {r})");
    }

    private string EmitStringOperand(BoundExpr e)
    {
        if (e.Type.Kind == TypeKind.String)
        {
            return EmitR(e);
        }

        var v = EmitR(e);
        if (e.Type.LlvmType == "i1")
        {
            return Inst($"call i8* @arc_str_from_bool(i1 {v})");
        }

        if (e.Type.LlvmType == "i64")
        {
            return Inst($"call i8* @arc_str_from_int(i64 {v})");
        }

        if (e.Type.LlvmType == "i32")
        {
            var x = Inst($"sext i32 {v} to i64");
            return Inst($"call i8* @arc_str_from_int(i64 {x})");
        }

        if (e.Type.LlvmType == "float")
        {
            return Inst($"call i8* @arc_str_from_float(float {v})");
        }

        if (e.Type.LlvmType == "double")
        {
            return Inst($"call i8* @arc_str_from_double(double {v})");
        }

        if (e.Type.IsReferenceType)
        {
            return v;
        }

        return Inst("call i8* @arc_str_from_int(i64 0)");
    }

    private string EmitUnary(BoundUnary u)
    {
        var v = EmitR(u.Operand);
        if (u.Op == TokenKind.Bang)
        {
            return Inst($"xor i1 {v}, true");
        }

        if (u.Type.LlvmType is "float" or "double")
        {
            return Inst($"fneg {u.Type.LlvmType} {v}");
        }

        return Inst($"sub {u.Type.LlvmType} 0, {v}");
    }

    private string EmitAssign(BoundAssign a)
    {
        var value = EmitR(a.Value);
        switch (a.Target)
        {
            case BoundLocal lo:
                if (lo.Type.IsReferenceType)
                {
                    Do($"call void @arc_assign_take(i8** {_localSlot[lo.Symbol.Id]}, i8* {value})");
                    Do($"call void @arc_retain(i8* {value})");
                }
                else
                {
                    Do($"store {lo.Type.LlvmType} {value}, {lo.Type.LlvmType}* {_localSlot[lo.Symbol.Id]}");
                }

                return value;
            case BoundParam p:
                Do($"store {p.Type.LlvmType} {value}, {p.Type.LlvmType}* %arg{p.Symbol.Index}");
                return value;
            case BoundFieldAccess fa:
                return EmitFieldStore(fa, value);
            case BoundIndex ix:
            {
                var arr = EmitR(ix.Receiver);
                _stmtTemps.Add(arr);
                var idx = EmitR(ix.Index);
                var addr = ElemAddr(arr, idx, ix.Type);
                if (ix.Type.IsReferenceType)
                {
                    var pp = Inst($"bitcast {ix.Type.LlvmType}* {addr} to i8**");
                    Do($"call void @arc_assign_take(i8** {pp}, i8* {value})");
                    Do($"call void @arc_retain(i8* {value})");
                }
                else
                {
                    Do($"store {ix.Type.LlvmType} {value}, {ix.Type.LlvmType}* {addr}");
                }

                return value;
            }
        }

        return value;
    }

    private string EmitFieldStore(BoundFieldAccess fa, string value)
    {
        string addr;
        if (fa.Field.IsStatic)
        {
            addr = fa.Field.MangledStatic;
        }
        else
        {
            var recv = EmitR(fa.Receiver!);
            _stmtTemps.Add(recv);
            addr = FieldAddr(recv, fa.Field);
        }

        var ty = fa.Field.Type.LlvmType;
        if (fa.Field.IsWeak)
        {
            var pp = Inst($"bitcast {ty}* {addr} to i8**");
            Do($"call void @arc_store_weak(i8** {pp}, i8* {value})");
            return value;
        }

        if (fa.Field.Type.IsReferenceType)
        {
            var pp = Inst($"bitcast {ty}* {addr} to i8**");
            Do($"call void @arc_assign_take(i8** {pp}, i8* {value})");
            Do($"call void @arc_retain(i8* {value})");
            return value;
        }

        Do($"store {ty} {value}, {ty}* {addr}");
        return value;
    }

    private string EmitConversion(BoundConversion cv)
    {
        var v = EmitR(cv.Operand);
        var from = cv.Operand.Type;
        var to = cv.Type;
        if (from.IsReferenceType && to.IsReferenceType)
        {
            return v;
        }

        // integer extension/truncation
        if (from.LlvmType == "i32" && to.LlvmType == "i64")
        {
            return Inst($"sext i32 {v} to i64");
        }

        if (from.LlvmType == "i64" && to.LlvmType == "i32")
        {
            return Inst($"trunc i64 {v} to i32");
        }

        // integer -> floating point
        if (from.LlvmType is "i32" or "i64" && to.LlvmType is "float" or "double")
        {
            return Inst($"sitofp {from.LlvmType} {v} to {to.LlvmType}");
        }

        // floating point -> integer
        if (from.LlvmType is "float" or "double" && to.LlvmType is "i32" or "i64")
        {
            return Inst($"fptosi {from.LlvmType} {v} to {to.LlvmType}");
        }

        // float <-> double
        if (from.LlvmType == "float" && to.LlvmType == "double")
        {
            return Inst($"fpext float {v} to double");
        }

        if (from.LlvmType == "double" && to.LlvmType == "float")
        {
            return Inst($"fptrunc double {v} to float");
        }

        return v;
    }

    private string EmitWeakRefNew(BoundWeakRefNew n)
    {
        var target = EmitR(n.Target);
        if (n.Target.Type.IsReferenceType)
        {
            _stmtTemps.Add(target);
        }

        return Inst($"call i8* @arc_weakref_new(i8* {target})");
    }

    private string EmitWeakRefTryGet(BoundWeakRefTryGet n)
    {
        var wr = EmitR(n.WeakRef);
        _stmtTemps.Add(wr);
        var got = Inst($"call i8* @arc_weakref_try_get(i8* {wr})");
        StoreRefLValue(n.OutTarget, got);
        return Inst($"icmp ne i8* {got}, null");
    }

    private string EmitWeakRefSet(BoundWeakRefSet n)
    {
        var wr = EmitR(n.WeakRef);
        _stmtTemps.Add(wr);
        var target = EmitR(n.Target);
        if (n.Target.Type.IsReferenceType)
        {
            _stmtTemps.Add(target);
        }

        Do($"call void @arc_weakref_set(i8* {wr}, i8* {target})");
        return "";
    }

    private void StoreRefLValue(BoundExpr lval, string value)
    {
        switch (lval)
        {
            case BoundLocal lo:
                Do($"call void @arc_assign_take(i8** {_localSlot[lo.Symbol.Id]}, i8* {value})");
                break;
            case BoundParam p:
                Do($"call void @arc_assign_take(i8** %arg{p.Symbol.Index}, i8* {value})");
                break;
            case BoundFieldAccess fa:
            {
                string addr;
                if (fa.Field.IsStatic)
                {
                    addr = fa.Field.MangledStatic;
                }
                else
                {
                    var recv = EmitR(fa.Receiver!);
                    _stmtTemps.Add(recv);
                    addr = FieldAddr(recv, fa.Field);
                }

                var pp = Inst($"bitcast {fa.Field.Type.LlvmType}* {addr} to i8**");
                Do($"call void @arc_assign_take(i8** {pp}, i8* {value})");
                break;
            }
            case BoundIndex ix:
            {
                var arr = EmitR(ix.Receiver);
                _stmtTemps.Add(arr);
                var idx = EmitR(ix.Index);
                var addr = ElemAddr(arr, idx, ix.Type);
                var pp = Inst($"bitcast {ix.Type.LlvmType}* {addr} to i8**");
                Do($"call void @arc_assign_take(i8** {pp}, i8* {value})");
                break;
            }
        }
    }

    private string EmitConsole(BoundConsoleCall c)
    {
        var nl = c.Which == ConsoleKind.WriteLine ? 1 : 0;
        if (c.Argument == null)
        {
            Do("call void @arc_console_newline()");
            return "";
        }

        var t = c.Argument.Type;
        if (t.Kind == TypeKind.String)
        {
            var v = EmitR(c.Argument);
            _stmtTemps.Add(v);
            Do($"call void @arc_console_write(i8* {v}, i32 {nl})");
            return "";
        }

        if (t.LlvmType == "i1")
        {
            var v = EmitR(c.Argument);
            Do($"call void @arc_console_write_bool(i1 {v}, i32 {nl})");
            return "";
        }

        if (t.LlvmType == "i64")
        {
            var v = EmitR(c.Argument);
            Do($"call void @arc_console_write_int(i64 {v}, i32 {nl})");
            return "";
        }

        if (t.LlvmType == "i32")
        {
            var v = EmitR(c.Argument);
            var x = Inst($"sext i32 {v} to i64");
            Do($"call void @arc_console_write_int(i64 {x}, i32 {nl})");
            return "";
        }

        if (t.LlvmType == "float")
        {
            var v = EmitR(c.Argument);
            Do($"call void @arc_console_write_float(float {v}, i32 {nl})");
            return "";
        }

        if (t.LlvmType == "double")
        {
            var v = EmitR(c.Argument);
            Do($"call void @arc_console_write_double(double {v}, i32 {nl})");
            return "";
        }

        var rv = EmitR(c.Argument);
        if (t.IsReferenceType)
        {
            _stmtTemps.Add(rv);
        }

        Do($"call void @arc_console_write(i8* {rv}, i32 {nl})");
        return "";
    }

    private void EmitMain(StringBuilder fns)
    {
        var entry = _program.Entry;
        fns.AppendLine("define i32 @main() {");
        fns.AppendLine("entry:");
        if (entry == null)
        {
            fns.AppendLine("  ret i32 0");
            fns.AppendLine("}");
            return;
        }

        if (entry.ReturnType.Kind == TypeKind.Void)
        {
            fns.AppendLine($"  call void @{entry.MangledName}()");
            fns.AppendLine("  call void @arc_report()");
            fns.AppendLine("  ret i32 0");
        }
        else
        {
            fns.AppendLine($"  %r = call {entry.ReturnType.LlvmType} @{entry.MangledName}()");
            fns.AppendLine("  call void @arc_report()");
            if (entry.ReturnType.LlvmType == "i32")
            {
                fns.AppendLine("  ret i32 %r");
            }
            else
            {
                fns.AppendLine("  %rc = trunc i64 %r to i32");
                fns.AppendLine("  ret i32 %rc");
            }
        }

        fns.AppendLine("}");
    }
}
