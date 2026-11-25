using System.Text;
using ArcSharp.Lexing;
using ArcSharp.Binding;
using ArcSharp.Syntax;

namespace ArcSharp.CodeGen;

public sealed class Emitter(BoundProgram program, string triple, LifetimeAnalysis lifetime, bool boundsChecks = true)
{
    private readonly BoundProgram _program = program;
    private readonly string _triple = triple;
    private readonly bool _boundsChecks = boundsChecks;

    private readonly StringBuilder _mod = new();
    private readonly List<string> _strings = [];
    private readonly Dictionary<string, string> _siteOperands = [];
    private int _staticRootCount;

    private List<string> _entry = [];
    private List<string> _body = [];
    private readonly List<string> _stmtTemps = [];
    // Struct temporaries live in allocas and need __drop, not arc_release.
    private readonly List<(TypeSymbol Type, string Addr)> _stmtStructTemps = [];
    private readonly Dictionary<BoundPatternInput, string> _patternInputs = [];
    private int _tmp, _lbl, _strId;

    // Exceptions. A thrown exception is the one reference stored in the global
    // @arc_exception. A call or runtime check that can fail is followed by a test
    // of it, and a pending exception branches to the unwind target of the
    // innermost open region, or to func.unwind when no region is open. The
    // branch first releases the statement temporaries that are live at that
    // point, so an unwind path performs the same releases as a normal exit.
    private sealed class Region
    {
        public required string Label;
        public bool Used;

        // Runs when control leaves the region by an exception, a return, or
        // normal completion. Null for a try block that only has catch clauses.
        public Action? Cleanup;
    }

    private readonly Stack<(string Break, string? Continue, int RegionDepth)> _branchTargets = [];
    private readonly List<Region> _regions = []; // open try and using statements, innermost last
    private readonly List<string> _hiddenSlots = []; // owned references released at func.exit
    private readonly List<string> _catchSlots = []; // the exception each enclosing catch block holds
    private readonly List<List<string>> _pinScopes = []; // pins of the calls being emitted
    private bool _funcUnwindUsed;
    private bool _terminated;
    private string _block = "entry";
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
        _block = l;
        _body.Add($"{l}:");
        _terminated = false;
    }

    private void EnsureBlock()
    {
        if (_terminated)
        {
            var l = L("dead");
            Lbl(l);
        }
    }

    public string Emit()
    {
        _genTmp = 0;
        _mod.AppendLine("; ArcSharp generated module");
        _mod.AppendLine($"target triple = \"{_triple}\"");
        _mod.AppendLine();
        _mod.AppendLine("%ArcSlot = type { i64, i32, i32, i8* }");
        _mod.AppendLine("%TypeInfo = type { i8*, i64, void (i8*)*, i8**, i64, i8**, i64, i8*, %ArcSlot*, i64, void (i8*)* }");
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
            else if (t.Kind == TypeKind.Struct)
            {
                EmitStructTypeInfo(t);
                EmitStructHelpers(t, fns);
            }
        }

        foreach (var t in _program.Types)
        {
            foreach (var f in t.StaticFields)
            {
                _mod.AppendLine($"{f.MangledStatic} = global {f.Type.LlvmType} {ZeroOf(f.Type)}");
            }
        }

        EmitStaticRoots();
        EmitExceptionTypeTable();
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
                     "declare i8* @arc_alloc(%TypeInfo*, i8*)",
                     "declare void @arc_retain(i8*)",
                     "declare void @arc_release(i8*)",
                     "declare void @arc_assign_take(i8**, i8*)",
                     "declare void @arc_store_strong(i8**, i8*)",
                     "declare void @arc_store_weak(i8**, i8*)",
                     "declare i8* @arc_load_weak(i8**)",
                     "declare i8* @arc_weakref_new(i8*, i8*)",
                     "declare i8* @arc_weakref_try_get(i8*)",
                     "declare void @arc_weakref_set(i8*, i8*)",
            "declare i8* @arc_array_new(i64, i32, %TypeInfo*, i8*)",
            "declare i8* @arc_array_slice_new(i8*, i64, i8*)",
            "declare i64 @arc_array_length(i8*)",
            "declare void @arc_bounds_check(i8*, i64)",
            "declare void @arc_array_store_check(i8*, i64, i8*)",
                     "declare i8* @arc_str_lit(i8*, i64, i8*)",
                     "declare i64 @arc_str_length(i8*)",
                     "declare i8* @arc_str_concat(i8*, i8*)",
                     "declare i32 @arc_str_equal(i8*, i8*)",
                     "declare i8* @arc_str_from_int(i64)",
                     "declare i8* @arc_str_from_uint(i64)",
                     "declare i8* @arc_str_from_char(i32)",
                     "declare i8* @arc_str_from_bool(i32)",
                     "declare i8* @arc_str_from_float(float)",
                     "declare i8* @arc_str_from_double(double)",
                     "declare void @arc_console_write(i8*, i32)",
                     "declare void @arc_console_write_int(i64, i32)",
                     "declare void @arc_console_write_uint(i64, i32)",
                     "declare void @arc_console_write_char(i32, i32)",
                     "declare void @arc_console_write_bool(i32, i32)",
                     "declare void @arc_console_write_float(float, i32)",
                     "declare void @arc_console_write_double(double, i32)",
            "declare void @arc_console_newline()",
            "declare i32 @arc_is_instance(i8*, %TypeInfo*)",
            "declare i32 @arc_has_interface(i8*, i64)",
            "declare void @arc_suppress_finalize(i8*)",
            "declare i8* @arc_delegate_combine(i8*, i8*)",
            "declare i8* @arc_delegate_remove(i8*, i8*)",
            "declare i32 @arc_delegate_equal(i8*, i8*)",
            "declare void @arc_set_roots(i8***, i64)",
            "declare void @arc_report()",
            "declare void @arc_set_exception_types(%TypeInfo**, i64)",
            "declare void @arc_throw(i8*)",
            "declare void @arc_unhandled()",
            "declare void @arc_finaliser_exception(i8*)",
            "declare void @arc_null_check(i8*)",
            "declare void @arc_cast_check(i8*, %TypeInfo*)",
            "declare void @arc_div_check(i64, i64, i32)",
            "declare void @arc_overflow_check(i32)",
            "declare void @arc_dec_set(i8*, i64, i64)",
            "declare void @arc_dec_add(i8*, i8*, i8*)",
            "declare void @arc_dec_sub(i8*, i8*, i8*)",
            "declare void @arc_dec_mul(i8*, i8*, i8*)",
            "declare void @arc_dec_div(i8*, i8*, i8*)",
            "declare void @arc_dec_rem(i8*, i8*, i8*)",
            "declare void @arc_dec_neg(i8*, i8*)",
            "declare i32 @arc_dec_cmp(i8*, i8*)",
            "declare void @arc_dec_from_i64(i64, i8*)",
            "declare void @arc_dec_from_u64(i64, i8*)",
            "declare void @arc_dec_from_double(double, i8*)",
            "declare i64 @arc_dec_to_i64(i8*, i64, i64)",
            "declare i64 @arc_dec_to_u64(i8*)",
            "declare double @arc_dec_to_double(i8*)",
            "declare i8* @arc_dec_to_str(i8*)",
            "declare float @llvm.trunc.f32(float)",
            "declare double @llvm.trunc.f64(double)",
            "@arc_exception = external global i8*",
                 })
        {
            _mod.AppendLine(d);
        }
    }

    // The classes the runtime raises itself. The order is the ARC_EXC_* order in
    // the runtime. They come from corelib/Exceptions.cs and are always compiled.
    private static readonly string[] RuntimeExceptionTypes =
    [
        "System.NullReferenceException",
        "System.IndexOutOfRangeException",
        "System.InvalidCastException",
        "System.DivideByZeroException",
        "System.OverflowException",
        "System.ArrayTypeMismatchException"
    ];

    private void EmitExceptionTypeTable()
    {
        var entries = RuntimeExceptionTypes.Select(name => _program.Types.Find(type => type.Name == name))
            .Select(type => type == null ? "%TypeInfo* null" : $"%TypeInfo* @{type.Name}__ti");
        _mod.AppendLine(
            $"@arc_exception_types = private constant [{RuntimeExceptionTypes.Length} x %TypeInfo*] [ {string.Join(", ", entries)} ]");
    }

    private static string ZeroOf(TypeSymbol t) =>
        t.Kind == TypeKind.Struct ? "zeroinitializer" : t.IsReferenceType ? "null" : IsFloatType(t) ? "0.0" : "0";

    private static bool IsFloatType(TypeSymbol t) => t.LlvmType is "float" or "double";

    private static string LlvmBytes(string text, out int byteLength)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        byteLength = bytes.Length;
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

        return sb.ToString();
    }

    private string EmitTypeName(TypeSymbol t)
    {
        var escaped = LlvmBytes(t.ToString(), out var nbytes);
        _mod.AppendLine($"@{t.Name}__name = private constant [{nbytes + 1} x i8] c\"{escaped}\\00\"");
        return $"getelementptr ([{nbytes + 1} x i8], [{nbytes + 1} x i8]* @{t.Name}__name, i64 0, i64 0)";
    }

    // kind matches ArcSlot in the runtime: 1 is a strong reference, 2 is an
    // inline struct whose nested TypeInfo continues the walk. Weak fields are
    // left out because they do not keep their target alive.
    private string EmitSlots(TypeSymbol t, out int count)
    {
        var parts = new List<string>();
        foreach (var f in t.InstanceFields)
        {
            if (f.IsWeak)
            {
                continue;
            }

            if (f.Type.Kind == TypeKind.Struct)
            {
                parts.Add($"%ArcSlot {{ i64 {f.ByteOffset}, i32 2, i32 0, i8* bitcast (%TypeInfo* @{f.Type.Name}__ti to i8*) }}");
            }
            else if (f.Type.IsReferenceType)
            {
                parts.Add($"%ArcSlot {{ i64 {f.ByteOffset}, i32 1, i32 0, i8* null }}");
            }
        }

        count = parts.Count;
        if (count == 0)
        {
            return "%ArcSlot* null";
        }

        var sym = $"@{t.Name}__slots";
        _mod.AppendLine($"{sym} = private constant [{count} x %ArcSlot] [ {string.Join(", ", parts)} ]");
        return $"%ArcSlot* getelementptr ([{count} x %ArcSlot], [{count} x %ArcSlot]* {sym}, i64 0, i64 0)";
    }

    private void EmitStructTypeInfo(TypeSymbol t)
    {
        var name = EmitTypeName(t);
        var slots = EmitSlots(t, out var count);
        _mod.AppendLine(
            $"@{t.Name}__ti = constant %TypeInfo {{ " +
            $"i8* {name}, i64 {t.HeapSize}, void (i8*)* bitcast (void ({t.LlvmType}*)* @{t.Name}__drop to void (i8*)*), i8** null, i64 0, i8** null, i64 0, i8* null, {slots}, i64 {count}, void (i8*)* null }}");
    }

    private void EmitStaticRoots()
    {
        var roots = new List<string>();
        foreach (var t in _program.Types)
        {
            foreach (var f in t.StaticFields)
            {
                if (f.IsWeak)
                {
                    continue;
                }

                if (f.Type.IsReferenceType)
                {
                    roots.Add($"i8** {f.MangledStatic}");
                }
                else if (f.Type.Kind == TypeKind.Struct)
                {
                    var address = $"bitcast ({f.Type.LlvmType}* {f.MangledStatic} to i8*)";
                    AddStructRoots(roots, f.Type, address, 0);
                }
            }
        }

        _staticRootCount = roots.Count;
        if (_staticRootCount == 0)
        {
            return;
        }

        _mod.AppendLine(
            $"@arc_static_roots = private constant [{_staticRootCount} x i8**] [ {string.Join(", ", roots)} ]");
    }

    private static void AddStructRoots(List<string> roots, TypeSymbol type, string address, int offset)
    {
        foreach (var field in type.InstanceFields)
        {
            if (field.IsWeak)
            {
                continue;
            }

            var fieldOffset = offset + field.ByteOffset;
            if (field.Type.IsReferenceType)
            {
                roots.Add($"i8** bitcast (i8* getelementptr (i8, i8* {address}, i64 {fieldOffset}) to i8**)");
            }
            else if (field.Type.Kind == TypeKind.Struct)
            {
                AddStructRoots(roots, field.Type, address, fieldOffset);
            }
        }
    }

    private string SiteOperand(string site)
    {
        if (string.IsNullOrEmpty(site))
        {
            return "null";
        }

        if (_siteOperands.TryGetValue(site, out var existing))
        {
            return existing;
        }

        var escaped = LlvmBytes(site, out var nbytes);
        var g = $"@.site.{_siteOperands.Count}";
        _strings.Add($"{g} = private constant [{nbytes + 1} x i8] c\"{escaped}\\00\"");
        var operand = $"getelementptr ([{nbytes + 1} x i8], [{nbytes + 1} x i8]* {g}, i64 0, i64 0)";
        _siteOperands[site] = operand;
        return operand;
    }

    private void EmitTypeInfo(TypeSymbol t, StringBuilder fns)
    {
        var name = EmitTypeName(t);

        var k = t.Vtable.Count;
        if (k > 0)
        {
            var entries = t.Vtable.Select(m => m.IsAbstract
                ? "i8* null"
                : $"i8* bitcast ({FnPtrType(m)} @{m.MangledName} to i8*)");
            _mod.AppendLine($"@{t.Name}__vtable = private constant [{k} x i8*] [ {string.Join(", ", entries)} ]");
        }

        var s = _program.InterfaceSelectorCount;
        if (s > 0)
        {
            var entries = new List<string>();
            for (var sel = 0; sel < s; sel++)
            {
                if (t.InterfaceImpl.TryGetValue(sel, out var m) && !m.IsAbstract)
                {
                    entries.Add($"i8* bitcast ({FnPtrType(m)} @{m.MangledName} to i8*)");
                }
                else if (t.InterfaceMembership.Contains(sel))
                {
                    entries.Add($"i8* bitcast (%TypeInfo* @{t.Name}__ti to i8*)");
                }
                else
                {
                    entries.Add("i8* null");
                }
            }

            _mod.AppendLine($"@{t.Name}__itable = private constant [{s} x i8*] [ {string.Join(", ", entries)} ]");
        }

        var size = t.HeapSize;
        var vtExpr = k > 0
            ? $"i8** getelementptr ([{k} x i8*], [{k} x i8*]* @{t.Name}__vtable, i64 0, i64 0)"
            : "i8** null";
        var itExpr = s > 0
            ? $"i8** getelementptr ([{s} x i8*], [{s} x i8*]* @{t.Name}__itable, i64 0, i64 0)"
            : "i8** null";
        var baseExpr = t.BaseType != null
            ? $"i8* bitcast (%TypeInfo* @{t.BaseType.Name}__ti to i8*)"
            : "i8* null";
        var slots = EmitSlots(t, out var slotCount);

        var chainOwner = NearestFinaliser(t);
        var finaliserExpr = chainOwner != null
            ? $"void (i8*)* @{chainOwner.Name}__finalisers"
            : "void (i8*)* null";

        _mod.AppendLine(
            $"@{t.Name}__ti = constant %TypeInfo {{ " +
            $"i8* {name}, " +
            $"i64 {size}, void (i8*)* @{t.Name}__deinit, {vtExpr}, i64 {k}, {itExpr}, i64 {s}, {baseExpr}, {slots}, i64 {slotCount}, {finaliserExpr} }}");

        if (t.Finaliser != null)
        {
            EmitFinaliserChain(t, fns);
        }

        fns.AppendLine($"define void @{t.Name}__deinit(i8* %self) {{");
        fns.AppendLine("entry:");
        foreach (var f in t.InstanceFields)
        {
            EmitFieldTeardown(fns, $"%self", f);
        }

        fns.AppendLine("  ret void");
        fns.AppendLine("}");
        fns.AppendLine();
    }

    private static TypeSymbol? NearestFinaliser(TypeSymbol? type)
    {
        while (type != null && type.Finaliser == null)
        {
            type = type.BaseType;
        }

        return type;
    }

    // The runtime calls one function per object, so each class that declares a
    // finaliser gets a function that runs its own body and then the nearest
    // inherited one. That is the derived-before-base order the contract gives.
    // The runtime runs it before __deinit releases any field, so a finaliser
    // can still use its children.
    private void EmitFinaliserChain(TypeSymbol t, StringBuilder fns)
    {
        fns.AppendLine($"define void @{t.Name}__finalisers(i8* %self) {{");
        fns.AppendLine("entry:");
        // A finaliser that throws is reported and cleared before the next one
        // runs, so the inherited finaliser never starts with an exception in flight.
        fns.AppendLine($"  call void @{t.Finaliser!.MangledName}(i8* %self)");
        fns.AppendLine("  call void @arc_finaliser_exception(i8* %self)");
        var inherited = NearestFinaliser(t.BaseType);
        if (inherited != null)
        {
            fns.AppendLine($"  call void @{inherited.Name}__finalisers(i8* %self)");
        }

        fns.AppendLine("  ret void");
        fns.AppendLine("}");
        fns.AppendLine();
    }

    // Teardown of one field inside a generated function: reference fields are
    // released, weak fields zeroed, struct fields dropped recursively.
    private void EmitFieldTeardown(StringBuilder into, string selfPtr, FieldSymbol f)
    {
        var slot = FieldAddrFromBytes(into, selfPtr, f);
        if (f.Type.Kind == TypeKind.Struct)
        {
            into.AppendLine($"  call void @{f.Type.Name}__drop({f.Type.LlvmType}* {slot})");
            return;
        }

        if (!f.Type.IsReferenceType)
        {
            return;
        }

        if (f.IsWeak)
        {
            into.AppendLine($"  call void @arc_store_weak(i8** {slot}, i8* null)");
            return;
        }

        var n = NextGen();
        into.AppendLine($"  %v{n} = load i8*, i8** {slot}");
        into.AppendLine($"  call void @arc_release(i8* %v{n})");
    }

    // Unique names for generated (non-_body) code, which bypasses Inst.
    private int _genTmp;
    private int NextGen() => ++_genTmp;

    // Byte-offset field address in generated (non-_body) code. Classes address
    // fields by byte offset; structs are blobs of 8-byte slots, and a nested struct field spans several.
    // Every access is emitted as its own instruction so that the result is a
    // plain local value, usable as any pointer operand.
    private string FieldAddrFromBytes(StringBuilder into, string basePtr, FieldSymbol f)
    {
        var n = NextGen();
        if (f.Owner.Kind == TypeKind.Struct)
        {
            into.AppendLine($"  %gp{n} = getelementptr {f.Owner.LlvmType}, {f.Owner.LlvmType}* {basePtr}, i64 0, i64 {f.ByteOffset / 8}");
            into.AppendLine($"  %fp{n} = bitcast i64* %gp{n} to {f.Type.LlvmType}*");
            return $"%fp{n}";
        }

        into.AppendLine($"  %gp{n} = getelementptr i8, i8* {basePtr}, i64 {f.ByteOffset}");
        into.AppendLine($"  %fp{n} = bitcast i8* %gp{n} to {f.Type.LlvmType}*");
        return $"%fp{n}";
    }

    // Per-struct helpers. __copy gives value semantics: value fields are
    // copied byte-wise, each strong reference field is stored with
    // arc_store_strong (retains the new value, releases the old one), weak
    // fields with arc_store_weak, and nested struct fields copy recursively.
    // __drop releases the reference fields of a struct value whose storage is
    // about to be reused or abandoned. Neither ever frees the struct itself:
    // structs are inline values in locals, fields, or parameters.
    private void EmitStructHelpers(TypeSymbol t, StringBuilder fns)
    {
        var ty = t.LlvmType;

        fns.AppendLine($"define void @{t.Name}__copy({ty}* %dst, {ty}* %src) {{");
        foreach (var f in t.InstanceFields)
        {
            var dst = FieldAddrFromGep(fns, "%dst", t, f);
            var src = FieldAddrFromGep(fns, "%src", t, f);
            var n = NextGen();
            switch (f.Type.Kind)
            {
                case TypeKind.Struct:
                    fns.AppendLine($"  call void @{f.Type.Name}__copy({f.Type.LlvmType}* {dst}, {f.Type.LlvmType}* {src})");
                    break;
                case TypeKind.Primitive or TypeKind.Enum when !f.IsWeak:
                {
                    fns.AppendLine($"  %v{n} = load {f.Type.LlvmType}, {f.Type.LlvmType}* {src}");
                    fns.AppendLine($"  store {f.Type.LlvmType} %v{n}, {f.Type.LlvmType}* {dst}");
                    break;
                }
                default:
                {
                    fns.AppendLine($"  %v{n} = load i8*, i8** {src}");
                    if (f.IsWeak)
                    {
                        // A weak field slot copies weakly.
                        fns.AppendLine($"  call void @arc_store_weak(i8** {dst}, i8* %v{n})");
                    }
                    else
                    {
                        // Strong reference field (including WeakReference<T>
                        // fields: the copy owns the weak-ref object itself).
                        fns.AppendLine($"  call void @arc_store_strong(i8** {dst}, i8* %v{n})");
                    }

                    break;
                }
            }
        }

        fns.AppendLine("  ret void");
        fns.AppendLine("}");
        fns.AppendLine();

        fns.AppendLine($"define void @{t.Name}__drop({ty}* %p) {{");
        fns.AppendLine($"  %missing = icmp eq {ty}* %p, null");
        fns.AppendLine("  br i1 %missing, label %exit, label %drop");
        fns.AppendLine("drop:");
        foreach (var f in t.InstanceFields)
        {
            var slot = FieldAddrFromGep(fns, "%p", t, f);
            if (f.Type.Kind == TypeKind.Struct)
            {
                fns.AppendLine($"  call void @{f.Type.Name}__drop({f.Type.LlvmType}* {slot})");
            }
            else if (f.IsWeak)
            {
                fns.AppendLine($"  call void @arc_store_weak(i8** {slot}, i8* null)");
            }
            else if (f.Type.IsReferenceType)
            {
                var n = NextGen();
                fns.AppendLine($"  %v{n} = load i8*, i8** {slot}");
                fns.AppendLine($"  call void @arc_release(i8* %v{n})");
            }
        }

        fns.AppendLine("  br label %exit");
        fns.AppendLine("exit:");
        fns.AppendLine("  ret void");
        fns.AppendLine("}");
        fns.AppendLine();
    }

    // Field address inside a generated struct helper: typed GEP on the blob.
    private string FieldAddrFromGep(StringBuilder fns, string basePtr, TypeSymbol owner, FieldSymbol f)
    {
        var n = NextGen();
        fns.AppendLine($"  %gp{n} = getelementptr {owner.LlvmType}, {owner.LlvmType}* {basePtr}, i64 0, i64 {f.ByteOffset / 8}");
        fns.AppendLine($"  %fp{n} = bitcast i64* %gp{n} to {f.Type.LlvmType}*");
        return $"%fp{n}";
    }

    // The type of a pointer to the method's definition. An instance method
    // takes its receiver first; a static method, such as a delegate thunk,
    // already lists everything it takes.
    private static string FnPtrType(MethodSymbol m, bool receiver = true)
    {
        var ps = new List<string>();
        if (receiver)
        {
            ps.Add("i8*");
        }

        foreach (var p in m.Parameters)
        {
            ps.Add(ParameterAbiType(p));
        }

        if (m.ReturnType.Kind == TypeKind.Struct)
        {
            ps.Add(m.ReturnType.LlvmType + "*");
        }

        return $"{ReturnAbiType(m)} ({string.Join(", ", ps)})*";
    }

    private static string ParameterAbiType(ParamSymbol parameter) =>
        parameter.RefKind != RefKind.None || parameter.Type.Kind == TypeKind.Struct
            ? parameter.Type.LlvmType + "*"
            : parameter.Type.LlvmType;

    private static string ReturnAbiType(MethodSymbol method) =>
        method.ReturnType.Kind == TypeKind.Struct ? "void" : method.ReturnType.LlvmType;

    private void EmitMethod(BoundMethodBody b, StringBuilder fns)
    {
        var argumentOwners = lifetime.ArgumentOwners(b).ToHashSet();
        _curMethod = b.Method;
        _entry = [];
        _body = [];
        _stmtTemps.Clear();
        _stmtStructTemps.Clear();
        _regions.Clear();
        _hiddenSlots.Clear();
        _catchSlots.Clear();
        _pinScopes.Clear();
        _funcUnwindUsed = false;
        _localSlot.Clear();
        _tmp = 0;
        _lbl = 0;
        _terminated = false;
        _block = "entry";

        var m = b.Method;
        var ps = new List<string>();
        if (!m.IsStatic)
        {
            ps.Add(m.Owner.Kind == TypeKind.Struct ? $"{m.Owner.LlvmType}* %this" : "i8* %this");
        }

        foreach (var p in m.Parameters)
        {
            ps.Add($"{ParameterAbiType(p)} %a{p.Index}");
        }

        if (m.ReturnType.Kind == TypeKind.Struct)
        {
            ps.Add($"{m.ReturnType.LlvmType}* %struct.result");
        }

        if (!m.IsStatic)
        {
            if (m.Owner.Kind == TypeKind.Struct)
            {
                // Struct methods borrow their instance; this is a pointer to
                // the caller's storage, never a +1 reference.
                var ty = m.Owner.LlvmType;
                _entry.Add($"  %this.addr = alloca {ty}*");
                _entry.Add($"  store {ty}* %this, {ty}** %this.addr");
            }
            else
            {
                _entry.Add("  %this.addr = alloca i8*");
                _entry.Add("  store i8* %this, i8** %this.addr");
            }
        }

        foreach (var p in m.Parameters)
        {
            if (p.RefKind != RefKind.None)
            {
                _entry.Add($"  %arg{p.Index} = alloca {p.Type.LlvmType}*");
                _entry.Add($"  store {p.Type.LlvmType}* %a{p.Index}, {p.Type.LlvmType}** %arg{p.Index}");
            }
            else if (p.Type.Kind != TypeKind.Struct)
            {
                _entry.Add($"  %arg{p.Index} = alloca {p.Type.LlvmType}");
                _entry.Add($"  store {p.Type.LlvmType} %a{p.Index}, {p.Type.LlvmType}* %arg{p.Index}");
            }
        }

        foreach (var loc in b.AllLocals)
        {
            var slot = $"%loc{loc.Id}";
            _localSlot[loc.Id] = slot;
            _entry.Add($"  {slot} = alloca {loc.Type.LlvmType}");
            if (loc.Type.Kind == TypeKind.Struct && loc.Type.InstanceFields.Count > 0)
            {
                // Struct storage must start zeroed so a first drop sees null
                // reference fields; re-declaration drops the previous value.
                _entry.Add($"  store {loc.Type.LlvmType} zeroinitializer, {loc.Type.LlvmType}* {slot}");
            }
            else if (loc.Type.IsReferenceType)
            {
                _entry.Add($"  store i8* null, i8** {slot}");
            }
        }

        foreach (var owner in argumentOwners)
        {
            _entry.Add($"  %moved.arg{owner.Id} = alloca i8*");
            _entry.Add($"  store i8* null, i8** %moved.arg{owner.Id}");
        }

        var returnType = ReturnAbiType(m);
        var isVoid = returnType == "void";
        if (!isVoid)
        {
            _entry.Add($"  %retval = alloca {m.ReturnType.LlvmType}");
            if (m.ReturnType.IsReferenceType)
            {
                _entry.Add("  store i8* null, i8** %retval");
            }
        }

        var staticInitialiser = m == m.Owner.StaticFieldInitialiser;
        var explicitInitialiser = staticInitialiser && m.Owner.HasExplicitStaticConstructor;
        if (staticInitialiser)
        {
            if (explicitInitialiser)
            {
                EmitCachedTypeFailure(m);
            }

            var guard = $"@{m.MangledName}.started";
            _mod.AppendLine($"{guard} = private global i1 false");
            var started = Inst($"load i1, i1* {guard}");
            Term($"br i1 {started}, label %func.exit, label %field.init");
            Lbl("field.init");
            // Recursive access sees fields that have already been initialised.
            Do($"store i1 true, i1* {guard}");
        }
        else if (m.Owner.HasExplicitStaticConstructor
            && (m.IsStatic || m.IsConstructor || m.Owner.Kind == TypeKind.Struct))
        {
            EmitTypeInitialiserCall(m.Owner);
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

        if (_funcUnwindUsed)
        {
            // An exception that no region in this method handles. Locals and
            // hidden slots are released at func.exit as on a normal return. A
            // return statement stores %retval only after its cleanups have
            // run, so nothing is left in it here.
            Lbl("func.unwind");
            if (explicitInitialiser)
            {
                EmitTypeFailure(m);
            }

            Term("br label %func.exit");
        }

        Lbl("func.exit");
        foreach (var loc in b.AllLocals)
        {
            if (loc.Type.IsReferenceType)
            {
                var v = Inst($"load i8*, i8** %loc{loc.Id}");
                if (argumentOwners.Contains(loc))
                {
                    // Earlier exits still own the value in the original slot.
                    var moved = Inst($"load i8*, i8** %moved.arg{loc.Id}");
                    var empty = Inst($"icmp eq i8* {v}, null");
                    v = Inst($"select i1 {empty}, i8* {moved}, i8* {v}");
                }

                Do($"call void @arc_release(i8* {v})");
            }
            else if (loc.Type.Kind == TypeKind.Struct)
            {
                Do($"call void @{loc.Type.Name}__drop({loc.Type.LlvmType}* %loc{loc.Id})");
            }
        }

        foreach (var slot in _hiddenSlots)
        {
            var held = Inst($"load i8*, i8** {slot}");
            Do($"call void @arc_release(i8* {held})");
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

        fns.AppendLine($"define {returnType} @{m.MangledName}({string.Join(", ", ps)}) {{");
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

    private static string DefaultVal(TypeSymbol t) =>
        t.Kind == TypeKind.Struct ? "zeroinitializer" : t.IsReferenceType ? "null" : IsFloatType(t) ? "0.0" : "0";

    // LLVM reads a decimal floating-point constant only when it has a point, and rejects 1E-05.
    // The hexadecimal form of the IEEE bits is exact and also covers NaN and infinity.
    private static string FormatFloatConstant(double value) =>
        "0x" + BitConverter.DoubleToInt64Bits(value).ToString("X16");

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
            case BoundSwitch selection: EmitSwitch(selection); break;
            case BoundBranch branch: EmitBranch(branch); break;
            case BoundReturn r: EmitReturn(r); break;
            case BoundUsing u: EmitUsing(u); break;
            case BoundTry t: EmitTry(t); break;
            case BoundThrow th: EmitThrow(th); break;
        }
    }

    private void EmitLocalDecl(BoundLocalDecl d)
    {
        if (d.Symbol.Type.Kind == TypeKind.Struct)
        {
            EmitStructLocalDecl(d);
            return;
        }

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

    private void EmitStructLocalDecl(BoundLocalDecl d)
    {
        var ty = d.Symbol.Type.LlvmType;
        var slot = _localSlot[d.Symbol.Id];
        var fresh = d.Initializer is BoundNewObject;

        // Re-declaration inside a loop must drop the previous value before
        // the storage is reused; the first drop sees the zeroed storage.
        if (d.Symbol.Type.InstanceFields.Count > 0)
        {
            Do($"call void @{d.Symbol.Type.Name}__drop({ty}* {slot})");
            Do($"store {ty} zeroinitializer, {ty}* {slot}");
        }

        if (!fresh)
        {
            if (d.Initializer == null)
            {
                return;
            }

            var mark = _stmtTemps.Count;
            var smark = _stmtStructTemps.Count;
            var src = EmitR(d.Initializer);
            Do($"call void @{d.Symbol.Type.Name}__copy({ty}* {slot}, {ty}* {src})");
            ReleaseTemps(mark);
            ReleaseStructTemps(smark);
            return;
        }

        // Construct directly into the slot: no temporary struct, no copy.
        var n = (BoundNewObject)d.Initializer!;
        var cmark = _stmtTemps.Count;
        if (n.Ctor != null)
        {
            var pins = BeginPins();
            var (argVals, _, copies) = EmitArguments(n.Arguments, n.Ctor.Parameters, pins);
            var args = string.Join(", ", new[] { $"{ty}* {slot}" }.Concat(argVals));
            Do($"call void @{n.Ctor.MangledName}({args})");
            EmitPendingCheck();
            ReleaseArgumentCopies(copies);
            EndPins(pins);
        }

        ReleaseTemps(cmark);
    }

    private void EmitExprStmt(BoundExprStmt e)
    {
        var mark = _stmtTemps.Count;
        var smark = _stmtStructTemps.Count;
        if (e.Expression.Type.Kind == TypeKind.Struct)
        {
            // A discarded struct expression can still have effects: a
            // constructor runs, an assignment writes through a field or ref
            // parameter. Emit it, then drop any temporary it created.
            EmitR(e.Expression);
            ReleaseTemps(mark);
            ReleaseStructTemps(smark);
            return;
        }

        var v = EmitR(e.Expression);
        if (e.Expression.Type.IsReferenceType)
        {
            _stmtTemps.Add(v);
        }

        ReleaseTemps(mark);
        ReleaseStructTemps(smark);
    }

    private void EmitIf(BoundIf s)
    {
        var mark = _stmtTemps.Count;
        var smark = _stmtStructTemps.Count;
        var c = EmitR(s.Condition);
        ReleaseTemps(mark);
        ReleaseStructTemps(smark);
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
        Term($"br label %{(s.ConditionIsTop ? condL : bodyL)}");
        Lbl(condL);
        var mark = _stmtTemps.Count;
        var smark = _stmtStructTemps.Count;
        var c = EmitR(s.Condition);
        ReleaseTemps(mark);
        ReleaseStructTemps(smark);
        Term($"br i1 {c}, label %{bodyL}, label %{endL}");
        EmitLoopBody(s.Body, bodyL, condL, endL);

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
            var smark = _stmtStructTemps.Count;
            var c = EmitR(s.Condition);
            ReleaseTemps(mark);
            ReleaseStructTemps(smark);
            Term($"br i1 {c}, label %{bodyL}, label %{endL}");
        }
        else
        {
            Term($"br label %{bodyL}");
        }

        EmitLoopBody(s.Body, bodyL, stepL, endL);

        Lbl(stepL);
        if (s.Update != null)
        {
            var mark = _stmtTemps.Count;
            var smark = _stmtStructTemps.Count;
            EmitR(s.Update);
            ReleaseTemps(mark);
            ReleaseStructTemps(smark);
        }

        Term($"br label %{condL}");
        Lbl(endL);
    }

    private void EmitSwitch(BoundSwitch selection)
    {
        var labels = selection.Sections.Select(_ => L("switch.case")).ToArray();
        var end = L("switch.end");
        var noMatch = L("switch.nomatch");
        var mark = _stmtTemps.Count;
        var structMark = _stmtStructTemps.Count;
        _patternInputs.Add(selection.Input, CapturePatternInput(selection.Value));
        var inputMark = _stmtTemps.Count;
        var inputStructMark = _stmtStructTemps.Count;
        for (var i = 0; i < selection.Sections.Count; i++)
        {
            foreach (var test in selection.Sections[i].Tests)
            {
                var matched = EmitR(test);
                ReleaseTemps(inputMark);
                ReleaseStructTemps(inputStructMark);
                var next = L("switch.next");
                Term($"br i1 {matched}, label %{labels[i]}, label %{next}");
                Lbl(next);
            }
        }

        var fallback = selection.Sections.FindIndex(section => section.IsDefault);
        Term($"br label %{(fallback >= 0 ? labels[fallback] : noMatch)}");
        _patternInputs.Remove(selection.Input);

        var references = _stmtTemps.GetRange(mark, inputMark - mark);
        var values = _stmtStructTemps.GetRange(structMark, inputStructMark - structMark);
        _stmtTemps.RemoveRange(mark, references.Count);
        _stmtStructTemps.RemoveRange(structMark, values.Count);
        void DropInput()
        {
            for (var i = references.Count - 1; i >= 0; i--)
            {
                Do($"call void @arc_release(i8* {references[i]})");
            }

            for (var i = values.Count - 1; i >= 0; i--)
            {
                var (type, address) = values[i];
                Do($"call void @{type.Name}__drop({type.LlvmType}* {address})");
            }
        }

        _branchTargets.Push((end, null, _regions.Count));
        for (var i = 0; i < labels.Length; i++)
        {
            Lbl(labels[i]);
            DropInput();
            EmitStmt(selection.Sections[i].Body);
            if (!_terminated)
            {
                Term($"br label %{end}");
            }
        }

        _branchTargets.Pop();
        if (fallback < 0)
        {
            Lbl(noMatch);
            DropInput();
            Term($"br label %{end}");
        }

        Lbl(end);
    }

    private void EmitBranch(BoundBranch branch)
    {
        var target = branch.IsContinue
            ? _branchTargets.First(candidate => candidate.Continue != null)
            : _branchTargets.Peek();
        EmitRegionCleanups(target.RegionDepth);
        if (!_terminated)
        {
            Term($"br label %{(branch.IsContinue ? target.Continue : target.Break)}");
        }
    }

    private void EmitLoopBody(BoundStmt body, string bodyLabel, string continueLabel, string breakLabel)
    {
        Lbl(bodyLabel);
        _branchTargets.Push((breakLabel, continueLabel, _regions.Count));
        EmitStmt(body);
        _branchTargets.Pop();
        if (!_terminated)
        {
            Term($"br label %{continueLabel}");
        }
    }

    private void EmitReturn(BoundReturn r)
    {
        var mark = _stmtTemps.Count;
        var smark = _stmtStructTemps.Count;

        var type = _curMethod.ReturnType;
        string? reference = null;
        Action? abandonReturn = null;
        if (r.Value != null)
        {
            var v = EmitR(r.Value);
            if (type.Kind == TypeKind.Struct)
            {
                CopyStruct(type, "%struct.result", v);
                abandonReturn = () =>
                {
                    Do($"call void @{type.Name}__drop({type.LlvmType}* %struct.result)");
                    Do($"store {type.LlvmType} zeroinitializer, {type.LlvmType}* %struct.result");
                };
            }
            else if (type.IsReferenceType)
            {
                reference = v;
                abandonReturn = () => Do($"call void @arc_release(i8* {v})");
            }
            else
            {
                StoreRet(v, type);
            }
        }

        ReleaseTemps(mark);
        ReleaseStructTemps(smark);
        EmitRegionCleanups(0, abandonReturn);
        if (_terminated)
        {
            return;
        }

        if (reference != null)
        {
            StoreRet(reference, type);
        }

        Term("br label %func.exit");
    }

    // Regions still open run their cleanup on the way out, innermost first,
    // mirroring how C# finally blocks run before control leaves. While one
    // cleanup is emitted, the regions it was inside are closed, so an exception
    // it throws goes to the enclosing handler.
    private void EmitRegionCleanups(int depth, Action? abandonReturn = null)
    {
        for (var i = _regions.Count - 1; i >= depth && !_terminated; i--)
        {
            var region = _regions[i];
            if (region.Cleanup == null)
            {
                continue;
            }

            var closed = _regions.GetRange(i, _regions.Count - i);
            _regions.RemoveRange(i, _regions.Count - i);
            if (abandonReturn == null)
            {
                region.Cleanup();
            }
            else
            {
                // Intercept only exceptions that escape this cleanup. Each
                // cleanup has its own enclosing unwind target.
                var failure = new Region { Label = L("return.abandoned") };
                _regions.Add(failure);
                region.Cleanup();
                _regions.RemoveAt(_regions.Count - 1);
                if (failure.Used)
                {
                    var resume = _terminated ? null : L("return.resume");
                    if (resume != null)
                    {
                        Term($"br label %{resume}");
                    }

                    Lbl(failure.Label);
                    abandonReturn();
                    Term($"br label %{UnwindTarget()}");
                    if (resume != null)
                    {
                        Lbl(resume);
                    }
                }
            }
            _regions.AddRange(closed);
        }
    }

    private string NewHiddenSlot()
    {
        var slot = $"%hidden{_hiddenSlots.Count}";
        _entry.Add($"  {slot} = alloca i8*");
        _entry.Add($"  store i8* null, i8** {slot}");
        _hiddenSlots.Add(slot);
        return slot;
    }

    private string UnwindTarget()
    {
        if (_regions.Count == 0)
        {
            _funcUnwindUsed = true;
            return "func.unwind";
        }

        var region = _regions[^1];
        region.Used = true;
        return region.Label;
    }

    // Tests the pending exception after a call or runtime check. The branch to
    // the unwind target first releases everything the statement still owns.
    // 'extra' lists owned values that are not yet in _stmtTemps.
    private void EmitPendingCheck(IEnumerable<string>? extra = null)
    {
        var pending = Inst("load i8*, i8** @arc_exception");
        var has = Inst($"icmp ne i8* {pending}, null");
        string unwindL = L("exc"), continueL = L("cont");
        Term($"br i1 {has}, label %{unwindL}, label %{continueL}");
        Lbl(unwindL);
        if (extra != null)
        {
            foreach (var value in extra)
            {
                Do($"call void @arc_release(i8* {value})");
            }
        }

        for (var scope = _pinScopes.Count - 1; scope >= 0; scope--)
        {
            ReleasePins(_pinScopes[scope]);
        }

        for (var i = _stmtTemps.Count - 1; i >= 0; i--)
        {
            Do($"call void @arc_release(i8* {_stmtTemps[i]})");
        }

        for (var i = _stmtStructTemps.Count - 1; i >= 0; i--)
        {
            var (type, addr) = _stmtStructTemps[i];
            Do($"call void @{type.Name}__drop({type.LlvmType}* {addr})");
        }

        Term($"br label %{UnwindTarget()}");
        Lbl(continueL);
    }

    private bool NeedsNullCheck(BoundExpr receiver) =>
        receiver.Type.IsReferenceType
        && receiver is not (BoundThis or BoundNewObject or BoundNewArray or BoundLiteral { LitKind: LiteralKind.String });

    private void EmitNullCheck(BoundExpr receiver, string value)
    {
        if (!NeedsNullCheck(receiver))
        {
            return;
        }

        Do($"call void @arc_null_check(i8* {value})");
        EmitPendingCheck();
    }

    private List<string> BeginPins()
    {
        var pins = new List<string>();
        _pinScopes.Add(pins);
        return pins;
    }

    private void EndPins(List<string> pins)
    {
        ReleasePins(pins);
        _pinScopes.RemoveAt(_pinScopes.Count - 1);
    }

    private void EmitThrow(BoundThrow s)
    {
        string exception;
        if (s.Value != null)
        {
            var mark = _stmtTemps.Count;
            var smark = _stmtStructTemps.Count;
            exception = EmitR(s.Value);
            ReleaseTemps(mark);
            ReleaseStructTemps(smark);
        }
        else if (_catchSlots.Count > 0)
        {
            // 'throw;' passes on the exception the enclosing catch block holds.
            exception = Inst($"load i8*, i8** {_catchSlots[^1]}");
            Do($"call void @arc_retain(i8* {exception})");
        }
        else
        {
            exception = "null";
        }

        // arc_throw takes the reference. Throwing null raises NullReferenceException.
        Do($"call void @arc_throw(i8* {exception})");
        Term($"br label %{UnwindTarget()}");
    }

    private void EmitTry(BoundTry t)
    {
        if (t.Finally == null)
        {
            EmitTryCatch(t.Body, t.Catches);
        }
        else if (t.Catches.Count == 0)
        {
            EmitProtected(() => EmitStmt(t.Body), () => EmitStmt(t.Finally));
        }
        else
        {
            // try { try { body } catch { ... } } finally { ... }
            EmitProtected(() => EmitTryCatch(t.Body, t.Catches), () => EmitStmt(t.Finally));
        }
    }

    // Emits a body whose cleanup has to run however control leaves it: by
    // falling off the end, by a return, or by an exception. The cleanup is
    // emitted once for each of those paths. On the exception path the exception
    // is set aside while the cleanup runs, so that code such as Dispose or a
    // finally block does not see it as its own. If the cleanup throws, that
    // exception replaces the one that was set aside.
    private void EmitProtected(Action emitBody, Action cleanup)
    {
        var region = new Region { Label = L("unwind"), Cleanup = cleanup };
        _regions.Add(region);
        emitBody();
        _regions.Remove(region);

        var endL = L("protected.end");
        if (!_terminated)
        {
            cleanup();
            if (!_terminated)
            {
                Term($"br label %{endL}");
            }
        }

        if (region.Used)
        {
            Lbl(region.Label);
            var saved = NewHiddenSlot();
            var pending = Inst("load i8*, i8** @arc_exception");
            Do("store i8* null, i8** @arc_exception");
            Do($"call void @arc_assign_take(i8** {saved}, i8* {pending})");
            cleanup();
            var restored = Inst($"load i8*, i8** {saved}");
            Do($"store i8* null, i8** {saved}");
            Do($"store i8* {restored}, i8** @arc_exception");
            Term($"br label %{UnwindTarget()}");
        }

        Lbl(endL);
    }

    private void EmitTryCatch(BoundBlock body, List<BoundCatch> catches)
    {
        var region = new Region { Label = L("catch.dispatch") };
        _regions.Add(region);
        EmitStmt(body);
        _regions.Remove(region);

        var endL = L("try.end");
        if (!_terminated)
        {
            Term($"br label %{endL}");
        }

        if (region.Used)
        {
            // The catch clauses are outside the region: an exception thrown in
            // one goes to the enclosing handler.
            Lbl(region.Label);
            var pending = Inst("load i8*, i8** @arc_exception");
            var caughtAll = false;
            foreach (var clause in catches)
            {
                var bodyL = L("catch.body");
                string? nextL = null;
                if (clause.ExceptionType == null)
                {
                    caughtAll = true;
                    Term($"br label %{bodyL}");
                }
                else
                {
                    nextL = L("catch.next");
                    var test = Inst($"call i32 @arc_is_instance(i8* {pending}, %TypeInfo* @{clause.ExceptionType.Name}__ti)");
                    var matches = Inst($"icmp ne i32 {test}, 0");
                    Term($"br i1 {matches}, label %{bodyL}, label %{nextL}");
                }

                Lbl(bodyL);
                EmitCatchBody(clause, endL);
                if (caughtAll)
                {
                    break;
                }

                Lbl(nextL!);
            }

            if (!caughtAll)
            {
                Term($"br label %{UnwindTarget()}");
            }
        }

        Lbl(endL);
    }

    // The catch block owns the exception from here on. It holds one reference
    // in a hidden slot, which 'throw;' reads, and a second in the catch
    // variable. Both are released when the block completes normally, and at
    // func.exit if it leaves by a return or another exception.
    private void EmitCatchBody(BoundCatch clause, string endL)
    {
        var held = NewHiddenSlot();
        var exception = Inst("load i8*, i8** @arc_exception");
        Do("store i8* null, i8** @arc_exception");
        Do($"call void @arc_assign_take(i8** {held}, i8* {exception})");
        string? variableSlot = null;
        if (clause.Variable != null)
        {
            variableSlot = _localSlot[clause.Variable.Id];
            Do($"call void @arc_retain(i8* {exception})");
            Do($"call void @arc_assign_take(i8** {variableSlot}, i8* {exception})");
        }

        _catchSlots.Add(held);
        EmitStmt(clause.Body);
        _catchSlots.RemoveAt(_catchSlots.Count - 1);
        if (_terminated)
        {
            return;
        }

        if (variableSlot != null)
        {
            Do($"call void @arc_assign_take(i8** {variableSlot}, i8* null)");
        }

        Do($"call void @arc_assign_take(i8** {held}, i8* null)");
        Term($"br label %{endL}");
    }

    private void EmitUsing(BoundUsing u)
    {
        // 1. initialise the resource slot; the initializer yields an owned (+1)
        //    value that is transferred into the slot like any local declaration
        if (u.ResourceInit != null)
        {
            var mark = _stmtTemps.Count;
            var smark = _stmtStructTemps.Count;
            var v = EmitR(u.ResourceInit);
            var slot = _localSlot[u.Resource.Id];
            if (u.Resource.Type.IsReferenceType)
            {
                Do($"call void @arc_assign_take(i8** {slot}, i8* {v})");
            }
            else
            {
                Do($"store {u.Resource.Type.LlvmType} {v}, {u.Resource.Type.LlvmType}* {slot}");
            }

            ReleaseTemps(mark);
            ReleaseStructTemps(smark);
        }

        // 2. body and cleanup. The cleanup runs on the normal path, before a
        //    return, and when an exception leaves the body.
        EmitProtected(() => EmitStmt(u.Body), () => EmitUsingCleanup(u));
    }

    private void EmitUsingCleanup(BoundUsing u)
    {
        var slot = _localSlot[u.Resource.Id];
        if (u.Dispose != null)
        {
            // Dispose is skipped for a null resource, as C# skips Dispose when
            // the resource is null. The receiver is read (+1) for the call and
            // released again as a temporary, so the read is balanced; the
            // slot's own +1 is released below.
            var cur = Inst($"load i8*, i8** {slot}");
            var notNull = Inst($"icmp ne i8* {cur}, null");
            string callL = L("using.call"), endL = L("using.end");
            Term($"br i1 {notNull}, label %{callL}, label %{endL}");
            Lbl(callL);
            var mark = _stmtTemps.Count;
            EmitCall(new BoundCall
            {
                Receiver = new BoundLocal { Symbol = u.Resource, Type = u.Resource.Type },
                Method = u.Dispose,
                Arguments = [],
                Type = u.Dispose.ReturnType,
                Virtual = u.DisposeVirtual,
                Interface = u.DisposeInterface,
                Selector = u.DisposeSelector
            });
            ReleaseTemps(mark);
            Term($"br label %{endL}");
            Lbl(endL);
        }

        // release the resource and clear the slot, so the func.exit release of
        // this local becomes a no-op on paths that reach here
        var v = Inst($"load i8*, i8** {slot}");
        Do($"call void @arc_release(i8* {v})");
        Do($"store i8* null, i8** {slot}");
    }

    private void ReleaseTemps(int mark)
    {
        for (var i = _stmtTemps.Count - 1; i >= mark; i--)
        {
            Do($"call void @arc_release(i8* {_stmtTemps[i]})");
        }

        _stmtTemps.RemoveRange(mark, _stmtTemps.Count - mark);
    }

    private void ReleaseStructTemps(int mark)
    {
        for (var i = _stmtStructTemps.Count - 1; i >= mark; i--)
        {
            var (type, addr) = _stmtStructTemps[i];
            Do($"call void @{type.Name}__drop({type.LlvmType}* {addr})");
        }

        _stmtStructTemps.RemoveRange(mark, _stmtStructTemps.Count - mark);
    }

    private string EmitR(BoundExpr e)
    {
        switch (e)
        {
            case BoundLiteral l: return EmitLiteral(l);
            case BoundLocal lo:
                // A struct local is its own storage: it evaluates to the
                // address of its blob, never a loaded value.
                if (lo.Type.Kind == TypeKind.Struct)
                {
                    return _localSlot[lo.Symbol.Id];
                }

                var slot = _localSlot[lo.Symbol.Id];
                if (lifetime.IsMove(lo) || lifetime.IsArgumentMove(lo))
                {
                    var moved = Inst($"load i8*, i8** {slot}");
                    Do($"store i8* null, i8** {slot}");
                    if (lifetime.IsArgumentMove(lo))
                    {
                        Do($"store i8* {moved}, i8** %moved.arg{lo.Symbol.Id}");
                    }

                    return moved;
                }

                return lifetime.IsBorrowed(lo)
                    ? Inst($"load i8*, i8** {slot}")
                    : EmitLoadSlot(slot, lo.Type);
            case BoundParam p:
                return lifetime.IsBorrowed(p)
                    ? Inst($"load i8*, i8** %arg{p.Symbol.Index}")
                    : EmitLoadParam(p);
            case BoundThis:
            {
                if (_curMethod.Owner.Kind == TypeKind.Struct)
                {
                    var ty = _curMethod.Owner.LlvmType;
                    return Inst($"load {ty}*, {ty}** %this.addr");
                }

                Do("call void @arc_retain(i8* %this)");
                return Inst("load i8*, i8** %this.addr");
            }
            case BoundFieldAccess fa: return EmitFieldAccess(fa);
            case BoundLength len: return EmitLength(len);
            case BoundCall c: return EmitCall(c);
            case BoundNewObject n: return EmitNewObject(n);
            case BoundNewArray a: return EmitNewArray(a);
            case BoundIndex ix: return EmitIndexRead(ix);
            case BoundArraySlice slice: return EmitArraySlice(slice);
            case BoundBinary b: return EmitBinary(b);
            case BoundUnary u: return EmitUnary(u);
            case BoundAssign asn: return EmitAssign(asn);
            case BoundIncrement increment: return EmitIncrement(increment);
            case BoundSequence sequence: return EmitSequence(sequence);
            case BoundOperatorUpdate update: return EmitOperatorUpdate(update);
            case BoundNumericCompound compound: return EmitNumericCompound(compound);
            case BoundStringCompound compound: return EmitStringCompound(compound);
            case BoundDelegateCompound compound: return EmitDelegateCompound(compound);
            case BoundFunctionAddress address:
                return $"ptrtoint ({FnPtrType(address.Method, receiver: false)} @{address.Method.MangledName} to i64)";
            case BoundConversion cv: return EmitConversion(cv);
            case BoundIs i: return EmitIs(i);
            case BoundUnbox unbox: return EmitUnbox(unbox);
            case BoundPatternTest pattern: return EmitPatternTest(pattern);
            case BoundChoice choice: return EmitChoice(choice);
            case BoundSwitchFailure failure: return EmitSwitchFailure(failure);
            case BoundPatternInput input: return EmitPatternInput(input);
            case BoundPatternBinding binding: return EmitPatternBinding(binding);
            case BoundAs a: return EmitAs(a);
            case BoundSameType same: return EmitSameType(same);
            case BoundDecimalOp decimalOp: return EmitDecimalOp(decimalOp);
            case BoundByRefArg br: return EmitR(br.Lvalue);
            case BoundWeakRefNew wn: return EmitWeakRefNew(wn);
            case BoundWeakRefTryGet wt: return EmitWeakRefTryGet(wt);
            case BoundWeakRefSet ws: return EmitWeakRefSet(ws);
            case BoundConsoleCall cc: return EmitConsole(cc);
            case BoundGcCall gc: return EmitGc(gc);
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
            case Syntax.LiteralKind.UInt: return ((uint)l.IntValue).ToString();
            case Syntax.LiteralKind.ULong: return ((ulong)l.IntValue).ToString();
            case Syntax.LiteralKind.Float:
            {
                var d = FormatFloatConstant((double)l.FloatValue);
                return Inst($"fptrunc double {d} to float");
            }
            case Syntax.LiteralKind.Double:
                return FormatFloatConstant(l.FloatValue);
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
                return Inst($"call i8* @arc_str_lit(i8* {ptr}, i64 {len}, i8* {SiteOperand(l.Site)})");
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

    private string EmitLoadParam(BoundParam p)
    {
        if (p.Type.Kind == TypeKind.Struct)
        {
            // A struct parameter is storage, not a loadable scalar: evaluate
            // it to the address of the value (the caller's storage when it is
            // passed by reference).
            if (!p.ByRef)
            {
                return $"%a{p.Symbol.Index}";
            }

            return Inst($"load {p.Type.LlvmType}*, {p.Type.LlvmType}** %arg{p.Symbol.Index}");
        }

        if (!p.ByRef)
        {
            return EmitLoadSlot($"%arg{p.Symbol.Index}", p.Type);
        }

        var ptr = Inst($"load {p.Type.LlvmType}*, {p.Type.LlvmType}** %arg{p.Symbol.Index}");
        var v = Inst($"load {p.Type.LlvmType}, {p.Type.LlvmType}* {ptr}");
        if (p.Type.IsReferenceType)
        {
            Do($"call void @arc_retain(i8* {v})");
        }

        return v;
    }

    // Address a ref/out/in slot. When the slot lives inside a heap object, that
    // object is pinned for the rest of the call: stack locals, static fields,
    // and by-ref parameters borrowed from the caller already outlive it.
    private string EmitManagedAddress(BoundExpr lval, List<string> pins)
    {
        switch (lval)
        {
            case BoundLocal lo:
                return _localSlot[lo.Symbol.Id];
            case BoundParam p:
                if (p.ByRef)
                {
                    return Inst($"load {p.Type.LlvmType}*, {p.Type.LlvmType}** %arg{p.Symbol.Index}");
                }

                return p.Type.Kind == TypeKind.Struct ? $"%a{p.Symbol.Index}" : $"%arg{p.Symbol.Index}";
            case BoundThis th when th.Type.Kind == TypeKind.Struct:
            {
                var ty = th.Type.LlvmType;
                return Inst($"load {ty}*, {ty}** %this.addr");
            }
            case BoundFieldAccess { Field.IsStatic: true } fa:
                return StaticFieldAddress(fa.Field);
            case BoundFieldAccess fa:
            {
                var receiver = fa.Receiver!;
                string basePtr;
                if (receiver.Type.IsReferenceType)
                {
                    basePtr = EmitR(receiver);
                    _stmtTemps.Add(basePtr);
                    EmitNullCheck(receiver, basePtr);
                    PinOwner(pins, basePtr);
                }
                else if (receiver.Type.Kind == TypeKind.Struct)
                {
                    basePtr = EmitManagedAddress(receiver, pins);
                }
                else
                {
                    basePtr = EmitR(receiver);
                }

                return FieldAddr(basePtr, receiver.Type, fa.Field);
            }
            case BoundIndex ix:
            {
                var arr = EmitR(ix.Receiver);
                _stmtTemps.Add(arr);
                PinOwner(pins, arr);
                var idx = EmitR(ix.Index);
                return ElemAddr(arr, idx, ix.Type);
            }
        }

        return "null";
    }

    private void PinOwner(List<string> pins, string owner)
    {
        Do($"call void @arc_retain(i8* {owner})");
        pins.Add(owner);
    }

    private void ReleasePins(List<string> pins)
    {
        for (var i = pins.Count - 1; i >= 0; i--)
        {
            Do($"call void @arc_release(i8* {pins[i]})");
        }
    }

    private string FieldAddr(string basePtr, TypeSymbol recvType, FieldSymbol f)
    {
        if (recvType.Kind == TypeKind.Struct)
        {
            // Struct storage is an opaque blob of 8-byte slots: address the
            // field by its slot index, then cast to the field's type.
            var sp = Inst($"getelementptr {recvType.LlvmType}, {recvType.LlvmType}* {basePtr}, i64 0, i64 {f.ByteOffset / 8}");
            return Inst($"bitcast i64* {sp} to {f.Type.LlvmType}*");
        }

        var p = Inst($"getelementptr i8, i8* {basePtr}, i64 {f.ByteOffset}");
        return Inst($"bitcast i8* {p} to {f.Type.LlvmType}*");
    }

    private string EmitFieldAccess(BoundFieldAccess fa)
    {
        // A struct-typed field is addressed, not loaded: its value lives
        // inline in the owner.
        if (fa.Type.Kind == TypeKind.Struct)
        {
            return EmitStructFieldAddress(fa);
        }

        return EmitFieldRead(fa);
    }

    private string EmitStructFieldAddress(BoundFieldAccess fa)
    {
        if (fa.Field.IsStatic)
        {
            return StaticFieldAddress(fa.Field);
        }

        var receiver = fa.Receiver!;
        var recv = EmitR(receiver);
        if (receiver.Type.IsReferenceType)
        {
            _stmtTemps.Add(recv);
            EmitNullCheck(receiver, recv);
        }

        return FieldAddr(recv, receiver.Type, fa.Field);
    }

    private string StaticFieldAddress(FieldSymbol field)
    {
        EmitTypeInitialiserCall(field.Owner);
        return field.MangledStatic;
    }

    private void EmitTypeInitialiserCall(TypeSymbol owner)
    {
        if (owner.StaticFieldInitialiser is { } initialiser && _curMethod != initialiser)
        {
            Do($"call void @{initialiser.MangledName}()");
            EmitPendingCheck();
        }
    }

    private static string TypeFailureSlot(MethodSymbol initialiser) => $"@{initialiser.MangledName}.failure";

    private void EmitCachedTypeFailure(MethodSymbol initialiser)
    {
        var slot = TypeFailureSlot(initialiser);
        _mod.AppendLine($"{slot} = private global i8* null");
        var cached = Inst($"load i8*, i8** {slot}");
        var hasFailure = Inst($"icmp ne i8* {cached}, null");
        Term($"br i1 {hasFailure}, label %type.failed, label %type.check");
        Lbl("type.failed");
        Do($"call void @arc_retain(i8* {cached})");
        Do($"store i8* {cached}, i8** @arc_exception");
        Term("br label %func.exit");
        Lbl("type.check");
    }

    private void EmitTypeFailure(MethodSymbol initialiser)
    {
        var type = _program.Types.Single(candidate => candidate.Name == "System.TypeInitializationException");
        var constructor = type.Methods.Single(candidate => candidate.IsConstructor && candidate.Parameters.Count == 2);
        var cause = Inst("load i8*, i8** @arc_exception");
        Do("store i8* null, i8** @arc_exception");
        var site = SiteOperand(initialiser.Owner.Name);
        var wrapper = Inst($"call i8* @arc_alloc(%TypeInfo* @{type.Name}__ti, i8* {site})");
        var length = Encoding.UTF8.GetByteCount(initialiser.Owner.Name);
        var name = Inst($"call i8* @arc_str_lit(i8* {site}, i64 {length}, i8* {site})");
        Do($"call void @{constructor.MangledName}(i8* {wrapper}, i8* {name}, i8* {cause})");
        Do($"call void @arc_release(i8* {name})");
        Do($"call void @arc_release(i8* {cause})");
        Do($"call void @arc_store_strong(i8** {TypeFailureSlot(initialiser)}, i8* {wrapper})");
        Do($"store i8* {wrapper}, i8** @arc_exception");
    }

    private string EmitFieldRead(BoundFieldAccess fa)
    {
        if (fa.Field.IsStatic)
        {
            var g = StaticFieldAddress(fa.Field);
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

        var receiver = fa.Receiver!;
        var recv = EmitR(receiver);
        TrackReferenceTemp(receiver, recv);
        EmitNullCheck(receiver, recv);

        var addr = FieldAddr(recv, receiver.Type, fa.Field);
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
        TrackReferenceTemp(len.Receiver, recv);
        EmitNullCheck(len.Receiver, recv);
        var l = Inst($"call i64 @arc_array_length(i8* {recv})");
        return Inst($"trunc i64 {l} to i32");
    }

    private string EmitCall(BoundCall c)
    {
        // Pins keep the heap object behind an interior reference alive until
        // this call returns. The callee can drop every named reference to that
        // object; the pin is a retain it cannot see, released only afterwards.
        var pins = BeginPins();
        string? recv = null;
        if (c.Receiver != null)
        {
            if (c.Receiver.Type.Kind == TypeKind.Struct)
            {
                recv = EmitStructReceiver(c.Receiver, pins);
            }
            else
            {
                recv = EmitR(c.Receiver);
                TrackReferenceTemp(c.Receiver, recv);
            }
        }

        var (argVals, lastValue, copies) = EmitArguments(c.Arguments, c.Method.Parameters, pins, c.ReturnsAssignedValue);
        if (c.Receiver != null && recv != null)
        {
            // A call on null fails after the arguments have been evaluated.
            EmitNullCheck(c.Receiver, recv);
        }

        var m = c.Method;
        var ret = ReturnAbiType(m);
        var isVoid = ret == "void";
        string? structResult = null;
        if (m.ReturnType.Kind == TypeKind.Struct)
        {
            structResult = NewStructTemp(m.ReturnType);
            argVals = [..argVals, $"{m.ReturnType.LlvmType}* {structResult}"];
        }

        string result;

        if (c.ThroughDelegate || c.Virtual || c.Interface)
        {
            string fn;
            var receiverArgument = recv;
            if (c.ThroughDelegate)
            {
                // The node's target is the first argument of its function. The
                // node keeps both alive for the whole call, so neither is retained.
                var delegateType = c.Receiver!.Type;
                var targetSlot = FieldAddr(recv!, delegateType, delegateType.DelegateTarget!);
                receiverArgument = Inst($"load i8*, i8** {targetSlot}");
                var functionSlot = FieldAddr(recv!, delegateType, delegateType.DelegateFunction!);
                var function = Inst($"load i64, i64* {functionSlot}");
                fn = Inst($"inttoptr i64 {function} to {FnPtrType(m)}");
            }
            else
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
                fn = Inst($"bitcast i8* {fp} to {FnPtrType(m)}");
            }

            var callArgs = string.Join(", ", new[] { $"i8* {receiverArgument}" }.Concat(argVals));
            if (isVoid)
            {
                Do($"call void {fn}({callArgs})");
                result = "";
            }
            else
            {
                result = Inst($"call {ret} {fn}({callArgs})");
            }
        }
        else
        {
            var parts = new List<string>();
            if (!m.IsStatic)
            {
                parts.Add(m.Owner.Kind == TypeKind.Struct
                    ? $"{m.Owner.LlvmType}* {recv}"
                    : $"i8* {recv}");
            }

            parts.AddRange(argVals);
            var args = string.Join(", ", parts);
            if (isVoid)
            {
                Do($"call void @{m.MangledName}({args})");
                result = "";
            }
            else
            {
                result = Inst($"call {ret} @{m.MangledName}({args})");
            }
        }

        EmitPendingCheck();
        ReleaseArgumentCopies(copies);
        EndPins(pins);
        if (c.ReturnsAssignedValue)
        {
            if (c.Type.IsReferenceType)
            {
                Do($"call void @arc_retain(i8* {lastValue})");
            }

            return lastValue;
        }

        return structResult ?? result;
    }

    private (string[] Values, string LastValue, List<(TypeSymbol Type, string Addr)> Copies)
        EmitArguments(List<BoundExpr> args, List<ParamSymbol> parameters, List<string> pins, bool returnsAssignedValue = false)
    {
        var lastValue = "";
        var copies = new List<(TypeSymbol Type, string Addr)>();
        var argVals = new string[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            var ordinal = i;
            if (a is BoundNamedArgument named)
            {
                ordinal = named.Ordinal;
                a = named.Value;
            }

            var p = ordinal < parameters.Count ? parameters[ordinal] : null;
            if (p != null && p.RefKind != RefKind.None && a is BoundByRefArg bra)
            {
                var addr = bra.Kind == RefKind.In ? EmitInAddress(bra.Lvalue, pins) : EmitManagedAddress(bra.Lvalue, pins);
                argVals[ordinal] = $"{p.Type.LlvmType}* {addr}";
            }
            else if (p?.Type.Kind == TypeKind.Struct)
            {
                var source = EmitR(a);
                var copy = NewStructTemp(p.Type);
                CopyStruct(p.Type, copy, source);
                copies.Add((p.Type, copy));
                argVals[ordinal] = $"{p.Type.LlvmType}* {copy}";
                if (returnsAssignedValue && ordinal == args.Count - 1)
                {
                    lastValue = NewStructTemp(p.Type);
                    CopyStruct(p.Type, lastValue, copy);
                }
            }
            else
            {
                var v = EmitR(a);
                TrackReferenceTemp(a, v);

                argVals[ordinal] = $"{a.Type.LlvmType} {v}";
                if (ordinal == args.Count - 1)
                {
                    lastValue = v;
                }
            }
        }

        return (argVals, lastValue, copies);
    }

    private string NewStructTemp(TypeSymbol type)
    {
        var address = T();
        var ty = type.LlvmType;
        _entry.Add($"  {address} = alloca {ty}");
        Do($"store {ty} zeroinitializer, {ty}* {address}");
        _stmtStructTemps.Add((type, address));
        return address;
    }

    private void CopyStruct(TypeSymbol type, string destination, string source)
    {
        var ty = type.LlvmType;
        Do($"call void @{type.Name}__copy({ty}* {destination}, {ty}* {source})");
    }

    private void ReleaseArgumentCopies(List<(TypeSymbol Type, string Addr)> copies)
    {
        for (var i = copies.Count - 1; i >= 0; i--)
        {
            var (type, address) = copies[i];
            Do($"call void @{type.Name}__drop({type.LlvmType}* {address})");
            _stmtStructTemps.Remove(copies[i]);
        }
    }

    private string EmitInAddress(BoundExpr value, List<string> pins)
    {
        if (value is BoundLocal or BoundParam or BoundFieldAccess or BoundIndex or BoundThis { Type.Kind: TypeKind.Struct })
        {
            return EmitManagedAddress(value, pins);
        }

        var result = EmitR(value);
        if (value.Type.Kind == TypeKind.Struct)
        {
            return result;
        }

        TrackReferenceTemp(value, result);
        var address = T();
        var type = value.Type.LlvmType;
        _entry.Add($"  {address} = alloca {type}");
        Do($"store {type} {result}, {type}* {address}");
        return address;
    }

    private void TrackReferenceTemp(BoundExpr expression, string value)
    {
        if (expression.Type.IsReferenceType && !lifetime.IsBorrowed(expression) && !lifetime.IsArgumentMove(expression))
        {
            _stmtTemps.Add(value);
        }
    }

    // A struct method's this is an interior pointer into the caller's storage.
    private string EmitStructReceiver(BoundExpr receiver, List<string> pins)
    {
        if (receiver is BoundLocal or BoundParam or BoundFieldAccess or BoundIndex or BoundThis)
        {
            return EmitManagedAddress(receiver, pins);
        }

        return EmitR(receiver);
    }

    private string EmitNewObject(BoundNewObject n)
    {
        if (n.Type.Kind == TypeKind.Struct)
        {
            return EmitNewObjectValue(n);
        }

        var pins = BeginPins();
        var (argVals, _, copies) = n.Ctor == null
            ? (Array.Empty<string>(), "", new List<(TypeSymbol Type, string Addr)>())
            : EmitArguments(n.Arguments, n.Ctor.Parameters, pins);
        if (n.Type.HasExplicitStaticConstructor)
        {
            EmitTypeInitialiserCall(n.Type);
        }

        var obj = Inst($"call i8* @arc_alloc(%TypeInfo* @{n.Type.Name}__ti, i8* {SiteOperand(n.Site)})");
        if (n.Ctor != null)
        {
            // While its constructor runs, the new object belongs to the
            // statement and is released if the constructor throws.
            var objIndex = _stmtTemps.Count;
            _stmtTemps.Add(obj);
            var args = string.Join(", ", new[] { $"i8* {obj}" }.Concat(argVals));
            Do($"call void @{n.Ctor.MangledName}({args})");
            EmitPendingCheck();
            ReleaseArgumentCopies(copies);
            _stmtTemps.RemoveAt(objIndex);
        }

        EndPins(pins);
        return obj;
    }

    // Construct a struct into a fresh temporary blob and return its address.
    // The temporary is tracked so the enclosing statement drops its reference
    // fields once the value has been consumed.
    private string EmitNewObjectValue(BoundNewObject n)
    {
        var ty = n.Type.LlvmType;
        var temp = NewStructTemp(n.Type);

        if (n.Ctor != null)
        {
            var pins = BeginPins();
            var (argVals, _, copies) = EmitArguments(n.Arguments, n.Ctor.Parameters, pins);
            var args = string.Join(", ", new[] { $"{ty}* {temp}" }.Concat(argVals));
            Do($"call void @{n.Ctor.MangledName}({args})");
            EmitPendingCheck();
            ReleaseArgumentCopies(copies);
            EndPins(pins);
        }

        return temp;
    }

    private string EmitNewArray(BoundNewArray a)
    {
        var size = EmitR(a.Size);
        var s64 = Inst($"sext i32 {size} to i64");
        var elementKind = a.ElementType.IsReferenceType ? 1 : a.ElementType.Kind == TypeKind.Struct ? 2 : 0;
        var elemTi = a.ElementType.Kind is TypeKind.Class or TypeKind.Struct
            ? $"%TypeInfo* @{a.ElementType.Name}__ti"
            : "%TypeInfo* null";
        var array = Inst($"call i8* @arc_array_new(i64 {s64}, i32 {elementKind}, {elemTi}, i8* {SiteOperand(a.Site)})");
        if (a.Initializers.Count == 0)
        {
            return array;
        }

        var tracked = _stmtTemps.Count;
        _stmtTemps.Add(array);
        for (var i = 0; i < a.Initializers.Count; i++)
        {
            var element = EmitR(a.Initializers[i]);
            var address = ElemAddr(array, i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                a.ElementType, checkBounds: false);
            if (a.ElementType.IsReferenceType)
            {
                Do($"call void @arc_assign_take(i8** {address}, i8* {element})");
            }
            else if (a.ElementType.Kind == TypeKind.Struct)
            {
                CopyStruct(a.ElementType, address, element);
            }
            else
            {
                var type = a.ElementType.LlvmType;
                Do($"store {type} {element}, {type}* {address}");
            }
        }

        UntrackInFlight(tracked);
        return array;
    }

    private string EmitArraySlice(BoundArraySlice slice)
    {
        var array = EmitR(slice.Receiver);
        TrackReferenceTemp(slice.Receiver, array);
        var start = EmitR(slice.Start);
        var length = EmitR(slice.Length);
        var wideLength = Inst($"sext i32 {length} to i64");
        var result = Inst($"call i8* @arc_array_slice_new(i8* {array}, i64 {wideLength}, i8* {SiteOperand(slice.Site)})");

        var slot = T();
        _entry.Add($"  {slot} = alloca i32");
        Do($"store i32 0, i32* {slot}");
        var condition = L("slice.cond");
        var body = L("slice.body");
        var end = L("slice.end");
        Term($"br label %{condition}");
        Lbl(condition);
        var index = Inst($"load i32, i32* {slot}");
        var more = Inst($"icmp slt i32 {index}, {length}");
        Term($"br i1 {more}, label %{body}, label %{end}");
        Lbl(body);
        var sourceIndex = Inst($"add i32 {start}, {index}");
        var element = slice.ElementType;
        var source = ElemAddr(array, sourceIndex, element, checkBounds: false);
        var destination = ElemAddr(result, index, element, checkBounds: false);
        if (element.Kind == TypeKind.Struct)
        {
            CopyStruct(element, destination, source);
        }
        else
        {
            var type = element.LlvmType;
            var value = Inst($"load {type}, {type}* {source}");
            if (element.IsReferenceType)
            {
                Do($"call void @arc_store_strong(i8** {destination}, i8* {value})");
            }
            else
            {
                Do($"store {type} {value}, {type}* {destination}");
            }
        }

        var next = Inst($"add i32 {index}, 1");
        Do($"store i32 {next}, i32* {slot}");
        Term($"br label %{condition}");
        Lbl(end);
        return result;
    }

    private string ElemAddr(string arr, string idx, TypeSymbol elem, bool checkBounds = true)
    {
        var i64 = Inst($"sext i32 {idx} to i64");
        if (checkBounds && _boundsChecks)
        {
            Do($"call void @arc_bounds_check(i8* {arr}, i64 {i64})");
            EmitPendingCheck();
        }

        var mul = Inst($"mul i64 {i64}, {Math.Max(8, elem.SlotSize)}");
        var off = Inst($"add i64 {mul}, 40");
        var p = Inst($"getelementptr i8, i8* {arr}, i64 {off}");
        return Inst($"bitcast i8* {p} to {elem.LlvmType}*");
    }

    private string EmitIndexRead(BoundIndex ix)
    {
        var arr = EmitR(ix.Receiver);
        TrackReferenceTemp(ix.Receiver, arr);
        var idx = EmitR(ix.Index);
        var addr = ElemAddr(arr, idx, ix.Type);
        if (ix.Type.Kind == TypeKind.Struct)
        {
            return addr;
        }

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
            case BinKind.BoolArith: return EmitIntegerArithmetic(b, 1);
            case BinKind.BoolCmp: return Inst($"icmp {CmpOp(b.Op, false)} i1 {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.IntArith: return EmitIntegerArithmetic(b, int.Parse(b.Left.Type.LlvmType.AsSpan(1)));
            case BinKind.LongArith: return EmitIntegerArithmetic(b, 64);
            case BinKind.FloatArith: return Inst($"{FloatArithOp(b.Op)} float {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.DoubleArith: return Inst($"{FloatArithOp(b.Op)} double {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.IntCmp: return Inst($"icmp {CmpOp(b.Op, b.IsUnsigned)} {b.Left.Type.LlvmType} {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.LongCmp: return Inst($"icmp {CmpOp(b.Op, b.IsUnsigned)} i64 {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.FloatCmp: return Inst($"fcmp {FloatCmpOp(b.Op)} float {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.DoubleCmp: return Inst($"fcmp {FloatCmpOp(b.Op)} double {EmitR(b.Left)}, {EmitR(b.Right)}");
            case BinKind.StrEq:
            case BinKind.RefEq:
            {
                var l = EmitR(b.Left);
                TrackReferenceTemp(b.Left, l);

                var r = EmitR(b.Right);
                TrackReferenceTemp(b.Right, r);

                if (b.Kind == BinKind.StrEq)
                {
                    var equal = Inst($"call i32 @arc_str_equal(i8* {l}, i8* {r})");
                    return Inst($"icmp {(b.Op == TokenKind.EqualsEquals ? "ne" : "eq")} i32 {equal}, 0");
                }

                return Inst($"icmp {(b.Op == TokenKind.EqualsEquals ? "eq" : "ne")} i8* {l}, {r}");
            }
            case BinKind.DelegateCombine:
            case BinKind.DelegateRemove:
            case BinKind.DelegateEq:
            {
                var l = EmitR(b.Left);
                TrackReferenceTemp(b.Left, l);
                var r = EmitR(b.Right);
                TrackReferenceTemp(b.Right, r);
                if (b.Kind == BinKind.DelegateEq)
                {
                    var equal = Inst($"call i32 @arc_delegate_equal(i8* {l}, i8* {r})");
                    return Inst($"icmp {(b.Op == TokenKind.EqualsEquals ? "ne" : "eq")} i32 {equal}, 0");
                }

                var name = b.Kind == BinKind.DelegateCombine ? "arc_delegate_combine" : "arc_delegate_remove";
                return Inst($"call i8* @{name}(i8* {l}, i8* {r})");
            }
            case BinKind.BoolLogic: return EmitLogic(b);
            case BinKind.StrConcat: return EmitConcat(b);
            default: return "0";
        }
    }

    private string EmitIntegerArithmetic(BoundBinary b, int bits)
    {
        return EmitIntegerArithmetic(b.Op, b.IsUnsigned, bits, EmitR(b.Left), EmitR(b.Right), b.IsChecked);
    }

    private string EmitIntegerArithmetic(TokenKind op, bool unsigned, int bits, string left, string right, bool isChecked = false)
    {
        if (isChecked && op is TokenKind.Plus or TokenKind.Minus or TokenKind.Star)
        {
            var narrowType = $"i{bits}";
            var wideType = $"i{bits * 2}";
            var extend = unsigned ? "zext" : "sext";
            var wideLeft = Inst($"{extend} {narrowType} {left} to {wideType}");
            var wideRight = Inst($"{extend} {narrowType} {right} to {wideType}");
            var wideResult = Inst($"{ArithOp(op, unsigned)} {wideType} {wideLeft}, {wideRight}");
            var result = Inst($"trunc {wideType} {wideResult} to {narrowType}");
            var restored = Inst($"{extend} {narrowType} {result} to {wideType}");
            EmitOverflowCheck(Inst($"icmp ne {wideType} {wideResult}, {restored}"));
            return result;
        }

        if (op is TokenKind.LeftShift or TokenKind.RightShift or TokenKind.UnsignedRightShift)
        {
            right = Inst($"and i32 {right}, {bits - 1}");
            if (bits == 64)
            {
                right = Inst($"zext i32 {right} to i64");
            }
        }

        if (op is TokenKind.Slash or TokenKind.Percent && !IsSafeDivisor(right))
        {
            // Hardware division traps on zero and on the minimum value divided
            // by -1. The runtime turns both into exceptions.
            var widen = unsigned ? "zext" : "sext";
            var wideLeft = bits == 64 ? left : Inst($"{widen} i32 {left} to i64");
            var wideRight = bits == 64 ? right : Inst($"{widen} i32 {right} to i64");
            Do($"call void @arc_div_check(i64 {wideLeft}, i64 {wideRight}, i32 {(unsigned ? 0 : bits)})");
            EmitPendingCheck();
        }

        return Inst($"{ArithOp(op, unsigned)} i{bits} {left}, {right}");
    }

    private void EmitOverflowCheck(string overflowed)
    {
        Do($"call void @arc_overflow_check(i32 {ZeroExtendBool(overflowed)})");
        EmitPendingCheck();
    }

    // A literal divisor other than zero and -1 cannot fail.
    private static bool IsSafeDivisor(string operand) =>
        long.TryParse(operand, out var value) && value is not (0 or -1);

    private static string ArithOp(TokenKind op, bool unsigned) => op switch
    {
        TokenKind.Plus => "add",
        TokenKind.Minus => "sub",
        TokenKind.Star => "mul",
        TokenKind.Slash => unsigned ? "udiv" : "sdiv",
        TokenKind.Percent => unsigned ? "urem" : "srem",
        TokenKind.Amp => "and",
        TokenKind.Pipe => "or",
        TokenKind.Caret => "xor",
        TokenKind.LeftShift => "shl",
        TokenKind.RightShift => unsigned ? "lshr" : "ashr",
        TokenKind.UnsignedRightShift => "lshr",
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

    private static string CmpOp(TokenKind op, bool unsigned) => op switch
    {
        TokenKind.EqualsEquals => "eq",
        TokenKind.BangEquals => "ne",
        TokenKind.Less => unsigned ? "ult" : "slt",
        TokenKind.LessEquals => unsigned ? "ule" : "sle",
        TokenKind.Greater => unsigned ? "ugt" : "sgt",
        TokenKind.GreaterEquals => unsigned ? "uge" : "sge",
        _ => "eq"
    };

    private static string FloatCmpOp(TokenKind op) => op switch
    {
        TokenKind.EqualsEquals => "oeq",
        TokenKind.BangEquals => "une",
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
        var lhsBlock = _block;
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
        var mark = _stmtTemps.Count;
        var structMark = _stmtStructTemps.Count;
        var r = EmitR(b.Right);
        Do($"store i1 {r}, i1* {slot}");
        var rhsBlock = _block;
        Term($"br label %{endL}");
        Lbl(endL);
        // A skipped operand contributes no owned temporary to the statement.
        for (var i = mark; i < _stmtTemps.Count; i++)
        {
            _stmtTemps[i] = Inst($"phi i8* [ null, %{lhsBlock} ], [ {_stmtTemps[i]}, %{rhsBlock} ]");
        }

        for (var i = structMark; i < _stmtStructTemps.Count; i++)
        {
            var (type, addr) = _stmtStructTemps[i];
            _stmtStructTemps[i] = (type, Inst($"phi {type.LlvmType}* [ null, %{lhsBlock} ], [ {addr}, %{rhsBlock} ]"));
        }

        return Inst($"load i1, i1* {slot}");
    }

    private string EmitConcat(BoundBinary b)
    {
        // The left operand is tracked before the right one is evaluated, which
        // can throw.
        var l = EmitStringOperand(b.Left);
        _stmtTemps.Add(l);
        var r = EmitStringOperand(b.Right);
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
            return Inst($"call i8* @arc_str_from_bool(i32 {ZeroExtendBool(v)})");
        }

        if (e.Type.Name == "char")
        {
            return Inst($"call i8* @arc_str_from_char(i32 {v})");
        }

        if (e.Type.LlvmType == "i64")
        {
            var convert = e.Type.IsUnsignedInteger ? "arc_str_from_uint" : "arc_str_from_int";
            return Inst($"call i8* @{convert}(i64 {v})");
        }

        if (e.Type.LlvmType == "i32")
        {
            var x = Inst($"{(e.Type.IsUnsignedInteger ? "zext" : "sext")} i32 {v} to i64");
            return Inst($"call i8* @arc_str_from_int(i64 {x})");
        }

        if (e.Type.LlvmType is "i8" or "i16")
        {
            var x = Inst($"{(e.Type.IsUnsignedInteger ? "zext" : "sext")} {e.Type.LlvmType} {v} to i64");
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

        if (u.Op == TokenKind.Tilde)
        {
            return Inst($"xor {u.Type.LlvmType} {v}, -1");
        }

        if (u.Type.LlvmType is "float" or "double")
        {
            return Inst($"fneg {u.Type.LlvmType} {v}");
        }

        return EmitIntegerArithmetic(TokenKind.Minus, u.Type.IsUnsignedInteger,
            int.Parse(u.Type.LlvmType.AsSpan(1)), "0", v, u.IsChecked);
    }

    private string EmitOperatorUpdate(BoundOperatorUpdate update)
    {
        var type = update.Type;
        var pins = BeginPins();
        string? array = null;
        string? index = null;
        string address;
        if (update.Target is BoundIndex element && type.IsReferenceType)
        {
            array = EmitR(element.Receiver);
            _stmtTemps.Add(array);
            PinOwner(pins, array);
            index = EmitR(element.Index);
            address = ElemAddr(array, index, element.Type);
        }
        else
        {
            address = EmitManagedAddress(update.Target, pins);
        }

        string before;
        if (type.Kind == TypeKind.Struct)
        {
            before = NewStructTemp(type);
            CopyStruct(type, before, address);
        }
        else
        {
            before = EmitLoadSlot(address, type);
            if (type.IsReferenceType)
            {
                _stmtTemps.Add(before);
            }
        }

        _patternInputs.Add(update.Input, before);
        var after = EmitR(update.Result);
        _patternInputs.Remove(update.Input);
        var result = update.IsPostfix ? before : after;
        if (type.IsReferenceType)
        {
            if (array != null && index != null)
            {
                var pending = _stmtTemps.Count;
                _stmtTemps.Add(after);
                var wideIndex = Inst($"sext i32 {index} to i64");
                Do($"call void @arc_array_store_check(i8* {array}, i64 {wideIndex}, i8* {after})");
                EmitPendingCheck();
                UntrackInFlight(pending);
            }

            Do($"call void @arc_assign_take(i8** {address}, i8* {after})");
            Do($"call void @arc_retain(i8* {result})");
        }
        else if (type.Kind == TypeKind.Struct)
        {
            CopyStruct(type, address, after);
        }
        else
        {
            Do($"store {type.LlvmType} {after}, {type.LlvmType}* {address}");
        }

        EndPins(pins);
        return result;
    }

    private string EmitStringCompound(BoundStringCompound compound)
    {
        var pins = BeginPins();
        var address = EmitManagedAddress(compound.Target, pins);
        var left = EmitLoadSlot(address, compound.Type);
        _stmtTemps.Add(left);
        var right = EmitStringOperand(compound.Right);
        _stmtTemps.Add(right);
        var result = Inst($"call i8* @arc_str_concat(i8* {left}, i8* {right})");
        Do($"call void @arc_assign_take(i8** {address}, i8* {result})");
        Do($"call void @arc_retain(i8* {result})");
        EndPins(pins);
        return result;
    }

    private string EmitDelegateCompound(BoundDelegateCompound compound)
    {
        var pins = BeginPins();
        var address = EmitManagedAddress(compound.Target, pins);
        var left = EmitLoadSlot(address, compound.Type);
        _stmtTemps.Add(left);
        var right = EmitR(compound.Right);
        TrackReferenceTemp(compound.Right, right);
        var name = compound.IsRemove ? "arc_delegate_remove" : "arc_delegate_combine";
        var result = Inst($"call i8* @{name}(i8* {left}, i8* {right})");
        Do($"call void @arc_assign_take(i8** {address}, i8* {result})");
        Do($"call void @arc_retain(i8* {result})");
        EndPins(pins);
        return result;
    }

    private string EmitNumericCompound(BoundNumericCompound compound)
    {
        var pins = BeginPins();
        var address = EmitManagedAddress(compound.Target, pins);
        var storage = compound.Type;
        var operation = compound.OperationType;
        var before = Inst($"load {storage.LlvmType}, {storage.LlvmType}* {address}");
        var left = EmitNumericConversion(before, storage, operation);
        var right = EmitR(compound.Right);
        if (compound.Op is not (TokenKind.LeftShift or TokenKind.RightShift or TokenKind.UnsignedRightShift))
        {
            right = EmitNumericConversion(right, compound.Right.Type, operation);
        }
        var combined = operation.IsFloat
            ? Inst($"{FloatArithOp(compound.Op)} {operation.LlvmType} {left}, {right}")
            : EmitIntegerArithmetic(compound.Op, operation.IsUnsignedInteger,
                int.Parse(operation.LlvmType.AsSpan(1)), left, right, compound.IsChecked);
        var after = EmitNumericConversion(combined, operation, storage, compound.IsChecked);

        Do($"store {storage.LlvmType} {after}, {storage.LlvmType}* {address}");
        EndPins(pins);
        return after;
    }

    private string EmitIncrement(BoundIncrement increment)
    {
        var pins = BeginPins();
        var address = EmitManagedAddress(increment.Target, pins);
        var type = increment.Type.LlvmType;
        var before = Inst($"load {type}, {type}* {address}");
        var op = increment.IsDecrement ? TokenKind.Minus : TokenKind.Plus;
        var after = type is "float" or "double"
            ? Inst($"{FloatArithOp(op)} {type} {before}, 1.0")
            : EmitIntegerArithmetic(op, increment.Type.IsUnsignedInteger,
                int.Parse(type.AsSpan(1)), before, "1", increment.IsChecked);
        if (increment.Type.Name == "char")
        {
            if (increment.IsChecked)
            {
                EmitOverflowCheck(Inst($"icmp ugt i32 {after}, 65535"));
            }

            after = Inst($"and i32 {after}, 65535");
        }

        Do($"store {type} {after}, {type}* {address}");
        EndPins(pins);
        return increment.IsPostfix ? before : after;
    }

    private string EmitAssign(BoundAssign a)
    {
        if (a.Type.Kind == TypeKind.Struct)
        {
            return EmitStructAssign(a);
        }

        var value = EmitR(a.Value);

        // Evaluating a field or element target can throw, and the value is an
        // owned reference that nothing else tracks yet.
        var inFlight = -1;
        if (a.Type.IsReferenceType && a.Target is BoundFieldAccess or BoundIndex)
        {
            inFlight = _stmtTemps.Count;
            _stmtTemps.Add(value);
        }

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
            {
                string addr;
                if (p.ByRef)
                {
                    addr = Inst($"load {p.Type.LlvmType}*, {p.Type.LlvmType}** %arg{p.Symbol.Index}");
                }
                else
                {
                    addr = $"%arg{p.Symbol.Index}";
                }

                if (p.Type.IsReferenceType)
                {
                    var pp = Inst($"bitcast {p.Type.LlvmType}* {addr} to i8**");
                    Do($"call void @arc_assign_take(i8** {pp}, i8* {value})");
                    Do($"call void @arc_retain(i8* {value})");
                }
                else
                {
                    Do($"store {p.Type.LlvmType} {value}, {p.Type.LlvmType}* {addr}");
                }

                return value;
            }
            case BoundFieldAccess fa:
                return EmitFieldStore(fa, value, inFlight);
            case BoundIndex ix:
            {
                var arr = EmitR(ix.Receiver);
                _stmtTemps.Add(arr);
                var idx = EmitR(ix.Index);
                var i64 = Inst($"sext i32 {idx} to i64");
                if (ix.Type.IsReferenceType)
                {
                    Do($"call void @arc_array_store_check(i8* {arr}, i64 {i64}, i8* {value})");
                    EmitPendingCheck();
                    var addr = ElemAddr(arr, idx, ix.Type, checkBounds: false);
                    var pp = Inst($"bitcast {ix.Type.LlvmType}* {addr} to i8**");
                    UntrackInFlight(inFlight);
                    Do($"call void @arc_assign_take(i8** {pp}, i8* {value})");
                    Do($"call void @arc_retain(i8* {value})");
                }
                else
                {
                    var addr = ElemAddr(arr, idx, ix.Type);
                    Do($"store {ix.Type.LlvmType} {value}, {ix.Type.LlvmType}* {addr}");
                }

                return value;
            }
        }

        return value;
    }

    // Struct assignment gives value semantics: copy into the target's storage.
    // The source is a borrow (a local, field, parameter) or a fresh temporary
    // tracked in _stmtStructTemps, which the enclosing statement drops.
    private string EmitStructAssign(BoundAssign a)
    {
        var ty = a.Type.LlvmType;
        var src = EmitR(a.Value);
        var dst = a.Target switch
        {
            BoundLocal lo => _localSlot[lo.Symbol.Id],
            BoundParam p => p.ByRef
                ? Inst($"load {p.Type.LlvmType}*, {p.Type.LlvmType}** %arg{p.Symbol.Index}")
                : $"%a{p.Symbol.Index}",
            BoundFieldAccess fa => EmitStructFieldAddress(fa),
            BoundIndex index => EmitIndexRead(index),
            _ => "null"
        };
        Do($"call void @{a.Type.Name}__copy({ty}* {dst}, {ty}* {src})");
        return src;
    }

    private void UntrackInFlight(int index)
    {
        if (index >= 0)
        {
            _stmtTemps.RemoveAt(index);
        }
    }

    private string EmitFieldStore(BoundFieldAccess fa, string value, int inFlight)
    {
        string addr;
        if (fa.Field.IsStatic)
        {
            addr = StaticFieldAddress(fa.Field);
        }
        else
        {
            var receiver = fa.Receiver!;
            var recv = EmitR(receiver);
            if (receiver.Type.IsReferenceType)
            {
                _stmtTemps.Add(recv);
            }

            EmitNullCheck(receiver, recv);
            addr = FieldAddr(recv, receiver.Type, fa.Field);
        }

        // No check point follows, so the caller owns the value again.
        UntrackInFlight(inFlight);

        if (fa.Field.Type.Kind == TypeKind.Struct)
        {
            Do($"call void @{fa.Field.Type.Name}__copy({addr}, {value})");
            return value;
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
            if (cv.CastCheck)
            {
                // The operand is an owned value that nothing tracks yet, so the
                // unwind path has to release it.
                Do($"call void @arc_cast_check(i8* {v}, %TypeInfo* @{to.Name}__ti)");
                EmitPendingCheck(new[] { v });
            }

            return v;
        }

        return EmitNumericConversion(v, from, to, cv.IsChecked);
    }

    private string EmitNumericConversion(string v, TypeSymbol from, TypeSymbol to, bool isChecked = false)
    {
        if (isChecked && from != to && to.IsInteger)
        {
            EmitCheckedConversion(v, from, to);
        }

        // integer extension/truncation
        if (from.IsInteger && to.IsInteger)
        {
            var fromBits = int.Parse(from.LlvmType.TrimStart('i'));
            var toBits = int.Parse(to.LlvmType.TrimStart('i'));
            if (to.Name == "char" && from.Name != "char")
            {
                var narrow = fromBits switch
                {
                    > 16 => Inst($"trunc {from.LlvmType} {v} to i16"),
                    < 16 => Inst($"{(from.IsUnsignedInteger ? "zext" : "sext")} {from.LlvmType} {v} to i16"),
                    _ => v
                };
                return Inst($"zext i16 {narrow} to i32");
            }

            if (fromBits < toBits)
            {
                return Inst($"{(from.IsUnsignedInteger ? "zext" : "sext")} {from.LlvmType} {v} to {to.LlvmType}");
            }

            if (fromBits > toBits)
            {
                return Inst($"trunc {from.LlvmType} {v} to {to.LlvmType}");
            }

            return v; // same-width reinterpretation (e.g., int <-> uint)
        }

        // integer -> floating point
        if (from.IsInteger && to.IsFloat)
        {
            var op = from.IsUnsignedInteger ? "uitofp" : "sitofp";
            return Inst($"{op} {from.LlvmType} {v} to {to.LlvmType}");
        }

        // floating point -> integer
        if (from.IsFloat && to.IsInteger)
        {
            var op = to.IsUnsignedInteger || to.Name == "char" ? "fptoui" : "fptosi";
            return Inst($"{op} {from.LlvmType} {v} to {to.LlvmType}");
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

    private void EmitCheckedConversion(string value, TypeSymbol from, TypeSymbol to)
    {
        var unsigned = to.IsUnsignedInteger || to.Name == "char";
        var bits = to.Name == "char" ? 16 : int.Parse(to.LlvmType.AsSpan(1));
        if (from.IsInteger)
        {
            var sourceExtend = from.IsUnsignedInteger || from.Name == "char" ? "zext" : "sext";
            var destinationExtend = unsigned ? "zext" : "sext";
            var wide = Inst($"{sourceExtend} {from.LlvmType} {value} to i128");
            var narrow = Inst($"trunc i128 {wide} to i{bits}");
            var restored = Inst($"{destinationExtend} i{bits} {narrow} to i128");
            EmitOverflowCheck(Inst($"icmp ne i128 {wide}, {restored}"));
        }
        else if (from.IsFloat)
        {
            var type = from.LlvmType;
            var intrinsic = type == "float" ? "f32" : "f64";
            var truncated = Inst($"call {type} @llvm.trunc.{intrinsic}({type} {value})");
            var bound = Math.ScaleB(1.0, unsigned ? bits : bits - 1);
            var lower = unsigned ? 0.0 : -bound;
            var min = FormatFloatConstant(lower);
            var max = FormatFloatConstant(bound);
            var aboveMin = Inst($"fcmp oge {type} {truncated}, {min}");
            var belowMax = Inst($"fcmp olt {type} {truncated}, {max}");
            var inRange = Inst($"and i1 {aboveMin}, {belowMax}");
            EmitOverflowCheck(Inst($"xor i1 {inRange}, true"));
        }
    }

    private string EmitChoice(BoundChoice choice)
    {
        var test = EmitR(choice.Condition);
        var result = choice.Type.Kind == TypeKind.Struct ? NewStructTemp(choice.Type) : null;
        var trueLabel = L("choice.true");
        var falseLabel = L("choice.false");
        var end = L("choice.end");
        Term($"br i1 {test}, label %{trueLabel}, label %{falseLabel}");

        var incoming = new List<(string Value, string Label, List<string> References, List<(TypeSymbol Type, string Addr)> Structs)>();
        void EmitArm(string label, BoundExpr arm)
        {
            Lbl(label);
            var armMark = _stmtTemps.Count;
            var armStructMark = _stmtStructTemps.Count;
            var value = EmitR(arm);
            if (_terminated)
            {
                _stmtTemps.RemoveRange(armMark, _stmtTemps.Count - armMark);
                _stmtStructTemps.RemoveRange(armStructMark, _stmtStructTemps.Count - armStructMark);
                return;
            }

            if (result != null)
            {
                CopyStruct(choice.Type, result, value);
            }

            var references = _stmtTemps.GetRange(armMark, _stmtTemps.Count - armMark);
            var structs = _stmtStructTemps.GetRange(armStructMark, _stmtStructTemps.Count - armStructMark);
            _stmtTemps.RemoveRange(armMark, references.Count);
            _stmtStructTemps.RemoveRange(armStructMark, structs.Count);
            incoming.Add((value, _block, references, structs));
            Term($"br label %{end}");
        }

        EmitArm(trueLabel, choice.WhenTrue);
        EmitArm(falseLabel, choice.WhenFalse);
        Lbl(end);
        if (incoming.Count == 0)
        {
            Term("unreachable");
            return "undef";
        }

        for (var i = 0; i < incoming.Count; i++)
        {
            var arm = incoming[i];
            string MergeTemp(string type, string value) => incoming.Count == 1
                ? value
                : Inst($"phi {type} [ {value}, %{arm.Label} ], [ null, %{incoming[1 - i].Label} ]");

            foreach (var reference in arm.References)
            {
                _stmtTemps.Add(MergeTemp("i8*", reference));
            }

            foreach (var (type, address) in arm.Structs)
            {
                _stmtStructTemps.Add((type, MergeTemp(type.LlvmType + "*", address)));
            }
        }

        if (result != null)
        {
            return result;
        }

        return incoming.Count == 1
            ? incoming[0].Value
            : Inst($"phi {choice.Type.LlvmType} {string.Join(", ", incoming.Select(arm => $"[ {arm.Value}, %{arm.Label} ]"))}");
    }

    private string EmitSwitchFailure(BoundSwitchFailure failure)
    {
        var exception = EmitR(failure.Exception);
        Do($"call void @arc_throw(i8* {exception})");
        EmitPendingCheck();
        Term("unreachable");
        return "undef";
    }

    private string EmitSequence(BoundSequence sequence)
    {
        foreach (var effect in sequence.Effects)
        {
            var value = EmitR(effect);
            if (effect.Type.IsReferenceType)
            {
                _stmtTemps.Add(value);
            }
        }

        return EmitR(sequence.Value);
    }

    private string EmitPatternTest(BoundPatternTest pattern)
    {
        _patternInputs.Add(pattern.Input, CapturePatternInput(pattern.Value));
        var matched = EmitR(pattern.Test);
        _patternInputs.Remove(pattern.Input);
        return matched;
    }

    private string CapturePatternInput(BoundExpr expression)
    {
        var value = EmitR(expression);
        TrackReferenceTemp(expression, value);
        if (expression.Type.Kind != TypeKind.Struct)
        {
            return value;
        }

        var copy = NewStructTemp(expression.Type);
        CopyStruct(expression.Type, copy, value);
        return copy;
    }

    private string EmitPatternInput(BoundPatternInput input)
    {
        var value = _patternInputs[input];
        if (input.Type.IsReferenceType)
        {
            Do($"call void @arc_retain(i8* {value})");
        }

        return value;
    }

    private string EmitPatternBinding(BoundPatternBinding binding)
    {
        var matched = EmitR(binding.Test);
        var assign = L("pattern.bind");
        var end = L("pattern.end");
        Term($"br i1 {matched}, label %{assign}, label %{end}");
        Lbl(assign);
        EmitLocalDecl(new BoundLocalDecl { Symbol = binding.Local, Initializer = binding.Value });
        Term($"br label %{end}");
        Lbl(end);
        return matched;
    }

    private string EmitUnbox(BoundUnbox unbox)
    {
        var value = EmitR(unbox.Operand);
        TrackReferenceTemp(unbox.Operand, value);
        EmitNullCheck(unbox.Operand, value);
        var field = unbox.BoxedValue;
        Do($"call void @arc_cast_check(i8* {value}, %TypeInfo* @{field.Owner.Name}__ti)");
        EmitPendingCheck();
        var source = FieldAddr(value, field.Owner, field);
        var copy = NewStructTemp(unbox.Type);
        CopyStruct(unbox.Type, copy, source);
        return copy;
    }

    private string EmitIs(BoundIs i)
    {
        var v = EmitR(i.Operand);
        TrackReferenceTemp(i.Operand, v);
        var r = i.TestType.Kind == TypeKind.Interface
            ? Inst($"call i32 @arc_has_interface(i8* {v}, i64 {i.TestType.InterfaceTestSlot})")
            : Inst($"call i32 @arc_is_instance(i8* {v}, %TypeInfo* @{i.TestType.Name}__ti)");
        return Inst($"icmp ne i32 {r}, 0");
    }

    private string EmitSameType(BoundSameType same)
    {
        var left = EmitR(same.Left);
        TrackReferenceTemp(same.Left, left);
        var right = EmitR(same.Right);
        TrackReferenceTemp(same.Right, right);
        return Inst($"icmp eq %TypeInfo* {LoadTypeInfo(left)}, {LoadTypeInfo(right)}");
    }

    // A decimal is passed to the runtime by address. The runtime raises through
    // arc_raise like the other checks, so every operation that can fail is
    // followed by a pending-exception check.
    private string EmitDecimalOp(BoundDecimalOp op)
    {
        string AddressOf(BoundExpr argument)
        {
            var value = EmitR(argument);
            return Inst($"bitcast {argument.Type.LlvmType}* {value} to i8*");
        }

        string NewResult(out string bytes)
        {
            var temp = NewStructTemp(op.Type);
            bytes = Inst($"bitcast {op.Type.LlvmType}* {temp} to i8*");
            return temp;
        }

        string result;
        switch (op.Op)
        {
            case DecimalOp.Constant:
            {
                result = NewResult(out var bytes);
                Do($"call void @arc_dec_set(i8* {bytes}, i64 {op.Low}, i64 {op.High})");
                return result;
            }

            case DecimalOp.Add or DecimalOp.Subtract or DecimalOp.Multiply or DecimalOp.Divide or DecimalOp.Remainder:
            {
                var function = op.Op switch
                {
                    DecimalOp.Add => "arc_dec_add",
                    DecimalOp.Subtract => "arc_dec_sub",
                    DecimalOp.Multiply => "arc_dec_mul",
                    DecimalOp.Divide => "arc_dec_div",
                    _ => "arc_dec_rem"
                };
                var left = AddressOf(op.Arguments[0]);
                var right = AddressOf(op.Arguments[1]);
                result = NewResult(out var bytes);
                Do($"call void @{function}(i8* {left}, i8* {right}, i8* {bytes})");
                EmitPendingCheck();
                return result;
            }

            case DecimalOp.Negate:
            {
                var operand = AddressOf(op.Arguments[0]);
                result = NewResult(out var bytes);
                Do($"call void @arc_dec_neg(i8* {operand}, i8* {bytes})");
                return result;
            }

            case DecimalOp.Compare:
            {
                var left = AddressOf(op.Arguments[0]);
                var right = AddressOf(op.Arguments[1]);
                return Inst($"call i32 @arc_dec_cmp(i8* {left}, i8* {right})");
            }

            case DecimalOp.FromInteger or DecimalOp.FromUnsigned or DecimalOp.FromDouble:
            {
                var argument = EmitR(op.Arguments[0]);
                result = NewResult(out var bytes);
                var (function, type) = op.Op switch
                {
                    DecimalOp.FromInteger => ("arc_dec_from_i64", "i64"),
                    DecimalOp.FromUnsigned => ("arc_dec_from_u64", "i64"),
                    _ => ("arc_dec_from_double", "double")
                };
                Do($"call void @{function}({type} {argument}, i8* {bytes})");
                EmitPendingCheck();
                return result;
            }

            case DecimalOp.ToInteger:
            {
                var operand = AddressOf(op.Arguments[0]);
                var value = Inst($"call i64 @arc_dec_to_i64(i8* {operand}, i64 {op.Low}, i64 {op.High})");
                EmitPendingCheck();
                return value;
            }

            case DecimalOp.ToUnsigned:
            {
                var operand = AddressOf(op.Arguments[0]);
                var value = Inst($"call i64 @arc_dec_to_u64(i8* {operand})");
                EmitPendingCheck();
                return value;
            }

            case DecimalOp.ToDouble:
            {
                var operand = AddressOf(op.Arguments[0]);
                return Inst($"call double @arc_dec_to_double(i8* {operand})");
            }

            default:
            {
                var operand = AddressOf(op.Arguments[0]);
                return Inst($"call i8* @arc_dec_to_str(i8* {operand})");
            }
        }
    }
    private string LoadTypeInfo(string objectPointer)
    {
        var address = Inst($"getelementptr i8, i8* {objectPointer}, i64 16");
        var typed = Inst($"bitcast i8* {address} to %TypeInfo**");
        return Inst($"load %TypeInfo*, %TypeInfo** {typed}");
    }

    private string EmitAs(BoundAs a)
    {
        var v = EmitR(a.Operand);
        if (a.Operand.Type.IsReferenceType)
        {
            _stmtTemps.Add(v);
        }

        var r = Inst($"call i32 @arc_is_instance(i8* {v}, %TypeInfo* @{a.TestType.Name}__ti)");
        var ok = Inst($"icmp ne i32 {r}, 0");
        var result = Inst($"select i1 {ok}, i8* {v}, i8* null");
        // The operand's retain is already scheduled for statement cleanup.
        Do($"call void @arc_retain(i8* {result})");
        return result;
    }

    private string EmitWeakRefNew(BoundWeakRefNew n)
    {
        var target = EmitR(n.Target);
        if (n.Target.Type.IsReferenceType)
        {
            _stmtTemps.Add(target);
        }

        return Inst($"call i8* @arc_weakref_new(i8* {target}, i8* {SiteOperand(n.Site)})");
    }

    private string EmitWeakRefTryGet(BoundWeakRefTryGet n)
    {
        var wr = EmitR(n.WeakRef);
        _stmtTemps.Add(wr);
        var got = Inst($"call i8* @arc_weakref_try_get(i8* {wr})");
        StoreRefLValue(n.OutTarget, got);
        return Inst($"icmp ne i8* {got}, null");
    }

    private string EmitGc(BoundGcCall c)
    {
        // Collect and WaitForPendingFinalizers have nothing to do under ARC.
        if (c.Which != GcKind.SuppressFinalize)
        {
            return "";
        }

        var target = EmitR(c.Argument!);
        _stmtTemps.Add(target);
        Do($"call void @arc_suppress_finalize(i8* {target})");
        return "";
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
                // The value is owned and untracked while the target is evaluated.
                var inFlight = _stmtTemps.Count;
                _stmtTemps.Add(value);
                string addr;
                if (fa.Field.IsStatic)
                {
                    addr = StaticFieldAddress(fa.Field);
                }
                else
                {
                    var receiver = fa.Receiver!;
                    var recv = EmitR(receiver);
                    if (receiver.Type.IsReferenceType)
                    {
                        _stmtTemps.Add(recv);
                    }

                    EmitNullCheck(receiver, recv);
                    addr = FieldAddr(recv, receiver.Type, fa.Field);
                }

                var pp = Inst($"bitcast {fa.Field.Type.LlvmType}* {addr} to i8**");
                UntrackInFlight(inFlight);
                Do($"call void @arc_assign_take(i8** {pp}, i8* {value})");
                break;
            }
            case BoundIndex ix:
            {
                var inFlight = _stmtTemps.Count;
                _stmtTemps.Add(value);
                var arr = EmitR(ix.Receiver);
                _stmtTemps.Add(arr);
                var idx = EmitR(ix.Index);
                var addr = ElemAddr(arr, idx, ix.Type);
                var pp = Inst($"bitcast {ix.Type.LlvmType}* {addr} to i8**");
                UntrackInFlight(inFlight);
                Do($"call void @arc_assign_take(i8** {pp}, i8* {value})");
                break;
            }
        }
    }

    // The runtime takes a C int. Passing the i1 itself leaves the rest of the
    // argument register undefined, so a false value can arrive non-zero and
    // print as True.
    private string ZeroExtendBool(string value) => Inst($"zext i1 {value} to i32");

    private string EmitConsole(BoundConsoleCall c)
    {
        var nl = c.Which == ConsoleKind.WriteLine ? 1 : 0;
        if (c.Argument == null)
        {
            Do("call void @arc_console_newline()");
            return "";
        }

        var t = c.Argument.Type;
        if (t.Kind is TypeKind.String or TypeKind.Struct)
        {
            var v = EmitR(c.Argument);
            if (t.Kind == TypeKind.Struct)
            {
                v = EmitLiteral(new BoundLiteral
                {
                    Type = t,
                    LitKind = Syntax.LiteralKind.String,
                    StringValue = t.Name
                });
            }

            _stmtTemps.Add(v);
            Do($"call void @arc_console_write(i8* {v}, i32 {nl})");
            return "";
        }

        if (t.LlvmType == "i1")
        {
            var v = EmitR(c.Argument);
            Do($"call void @arc_console_write_bool(i32 {ZeroExtendBool(v)}, i32 {nl})");
            return "";
        }

        if (t.Name == "char")
        {
            var v = EmitR(c.Argument);
            Do($"call void @arc_console_write_char(i32 {v}, i32 {nl})");
            return "";
        }

        if (t.LlvmType == "i64")
        {
            var v = EmitR(c.Argument);
            var write = t.IsUnsignedInteger ? "arc_console_write_uint" : "arc_console_write_int";
            Do($"call void @{write}(i64 {v}, i32 {nl})");
            return "";
        }

        if (t.LlvmType == "i32")
        {
            var v = EmitR(c.Argument);
            var x = Inst($"{(t.IsUnsignedInteger ? "zext" : "sext")} i32 {v} to i64");
            Do($"call void @arc_console_write_int(i64 {x}, i32 {nl})");
            return "";
        }

        if (t.LlvmType is "i8" or "i16")
        {
            var v = EmitR(c.Argument);
            var x = Inst($"{(t.IsUnsignedInteger ? "zext" : "sext")} {t.LlvmType} {v} to i64");
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
        if (_staticRootCount > 0)
        {
            fns.AppendLine(
                $"  call void @arc_set_roots(i8*** bitcast ([{_staticRootCount} x i8**]* @arc_static_roots to i8***), i64 {_staticRootCount})");
        }

        if (entry == null)
        {
            fns.AppendLine("  ret i32 0");
            fns.AppendLine("}");
            return;
        }

        fns.AppendLine(
            $"  call void @arc_set_exception_types(%TypeInfo** getelementptr ([{RuntimeExceptionTypes.Length} x %TypeInfo*], [{RuntimeExceptionTypes.Length} x %TypeInfo*]* @arc_exception_types, i64 0, i64 0), i64 {RuntimeExceptionTypes.Length})");
        if (entry.ReturnType.Kind == TypeKind.Void)
        {
            fns.AppendLine($"  call void @{entry.MangledName}()");
        }
        else
        {
            fns.AppendLine($"  %r = call {entry.ReturnType.LlvmType} @{entry.MangledName}()");
        }

        foreach (var type in _program.Types)
        {
            if (type.HasExplicitStaticConstructor && type.StaticFieldInitialiser is { } initialiser)
            {
                fns.AppendLine($"  call void @arc_assign_take(i8** {TypeFailureSlot(initialiser)}, i8* null)");
            }
        }

        // An exception that no handler caught ends the program here.
        fns.AppendLine("  %pending = load i8*, i8** @arc_exception");
        fns.AppendLine("  %unhandled = icmp ne i8* %pending, null");
        fns.AppendLine("  br i1 %unhandled, label %main.unhandled, label %main.done");
        fns.AppendLine("main.unhandled:");
        fns.AppendLine("  call void @arc_unhandled()");
        fns.AppendLine("  unreachable");
        fns.AppendLine("main.done:");
        if (entry.ReturnType.Kind == TypeKind.Void)
        {
            fns.AppendLine("  call void @arc_report()");
            fns.AppendLine("  ret i32 0");
        }
        else
        {
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
