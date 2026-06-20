using ArcSharp.Lexing;
using ArcSharp.Syntax;

namespace ArcSharp.Binding;

public sealed class BoundProgram
{
    public List<TypeSymbol> Types = [];
    public List<BoundMethodBody> MethodBodies = [];
    public int InterfaceSelectorCount;
    public MethodSymbol? Entry;
    public List<Diagnostic> Diagnostics = [];
}

public sealed class Binder
{
    private readonly Dictionary<string, TypeSymbol> _types = [];
    private readonly Dictionary<string, TypeSymbol> _arrayCache = [];
    private readonly Dictionary<string, TypeSymbol> _weakRefCache = [];
    public List<Diagnostic> Diagnostics { get; } = [];

    // predefined
    private readonly TypeSymbol _int, _long, _bool, _char, _float, _double, _void, _string, _null, _error;

    // interface selectors:  key "Iface::Method/argc" -> selector index
    private readonly Dictionary<string, int> _selectors = [];

    // per-method binding state
    private TypeSymbol _curType = null!;
    private MethodSymbol _curMethod = null!;
    private readonly List<Dictionary<string, LocalSymbol>> _scopes = [];
    private Dictionary<string, ParamSymbol> _params = [];
    private List<LocalSymbol> _methodLocals = [];
    private int _localId;

    public Binder()
    {
        _int = Prim("int");
        _long = Prim("long");
        _bool = Prim("bool");
        _char = Prim("char");
        _float = Prim("float");
        _double = Prim("double");
        _void = new TypeSymbol { Name = "void", Kind = TypeKind.Void };
        _string = new TypeSymbol { Name = "string", Kind = TypeKind.String };
        _null = new TypeSymbol { Name = "<null>", Kind = TypeKind.Class };
        _error = new TypeSymbol { Name = "<error>", Kind = TypeKind.Error };
        foreach (var t in new[] { _int, _long, _bool, _char, _float, _double, _void, _string })
        {
            _types[t.Name] = t;
        }

        static TypeSymbol Prim(string n) => new() { Name = n, Kind = TypeKind.Primitive };
    }

    // Multi-file entry: merge the type declarations of every compilation unit
    // into one program before binding. Namespaces are already flattened by the parser.
    public BoundProgram Bind(IEnumerable<CompilationUnit> units)
    {
        var merged = new CompilationUnit { Line = 0 };
        foreach (var u in units)
        {
            merged.Types.AddRange(u.Types);
        }

        return Bind(merged);
    }

    public BoundProgram Bind(CompilationUnit cu)
    {
        // 1. declare types
        foreach (var td in cu.Types)
        {
            if (_types.ContainsKey(td.Name))
            {
                Report(td.Line, $"duplicate type '{td.Name}'");
                continue;
            }

            var sym = new TypeSymbol
            {
                Name = td.Name,
                Kind = td switch { ClassDecl => TypeKind.Class, StructDecl => TypeKind.Struct, _ => TypeKind.Interface }
            };
            if (td is ClassDecl cd)
            {
                sym.ClassSyntax = cd;
            }
            else if (td is StructDecl sd)
            {
                sym.StructSyntax = sd;
            }
            else if (td is InterfaceDecl id)
            {
                sym.InterfaceSyntax = id;
            }

            _types[td.Name] = sym;
        }

        // 2. resolve bases / interfaces
        foreach (var td in cu.Types)
        {
            if (!_types.TryGetValue(td.Name, out var sym))
            {
                continue;
            }

            if (td is ClassDecl cd)
            {
                foreach (var bn in cd.InterfaceNames)
                {
                    if (!_types.TryGetValue(bn, out var b))
                    {
                        Report(td.Line, $"unknown base type '{bn}'");
                        continue;
                    }

                    if (b.Kind == TypeKind.Class)
                    {
                        if (sym.BaseType != null)
                        {
                            Report(td.Line, $"multiple base classes for '{td.Name}'");
                        }

                        sym.BaseType = b;
                    }
                    else if (b.Kind == TypeKind.Interface)
                    {
                        sym.Interfaces.Add(b);
                    }
                    else
                    {
                        Report(td.Line, $"'{bn}' cannot be a base of a class");
                    }
                }
            }
        }

        // 3. assign interface selectors
        foreach (var t in _types.Values.Where(t => t.Kind == TypeKind.Interface))
        {
            foreach (var m in t.InterfaceSyntax!.Members.OfType<MethodDecl>())
            {
                var key = $"{t.Name}::{m.Name}/{m.Parameters.Count}";
                if (!_selectors.ContainsKey(key))
                {
                    _selectors[key] = _selectors.Count;
                }
            }
        }

        // 4. layout + member symbols
        foreach (var t in _types.Values.Where(t => t.Kind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface))
        {
            LayoutType(t);
        }

        // 5. bind bodies
        var program = new BoundProgram { InterfaceSelectorCount = _selectors.Count };
        program.Types.AddRange(_types.Values.Where(t =>
            t.Kind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface));
        foreach (var t in program.Types)
        {
            foreach (var m in t.Methods)
            {
                if (m.Syntax?.Body == null)
                {
                    continue;
                }

                var body = BindMethodBody(t, m);
                program.MethodBodies.Add(body);
            }
        }

        // definite-assignment flow analysis (conservative; reports as warnings)
        DefiniteAssignment.Analyze(program, (line, msg) => Report(line, msg, "ARC2100", DiagnosticSeverity.Warning));

        program.Entry = FindEntry(program.Types);
        program.Diagnostics.AddRange(Diagnostics);
        return program;
    }

    private MethodSymbol? FindEntry(List<TypeSymbol> types)
    {
        foreach (var t in types)
        {
            foreach (var m in t.Methods)
            {
                if (m.IsStatic && m.Name == "Main" && m.Parameters.Count == 0)
                {
                    return m;
                }
            }
        }

        Report(0, "no static 'Main()' entry point found", "ARC2050");
        return null;
    }

    private void LayoutType(TypeSymbol t)
    {
        if (t.LayoutDone)
        {
            return;
        }

        t.LayoutDone = true;

        var decl = (TypeDecl?)t.ClassSyntax ?? (TypeDecl?)t.StructSyntax ?? t.InterfaceSyntax!;

        // inherited layout first
        if (t.BaseType != null)
        {
            LayoutType(t.BaseType);
            t.InstanceFields.AddRange(t.BaseType.InstanceFields);
            t.Vtable.AddRange(t.BaseType.Vtable);
        }

        // fields
        foreach (var fd in decl.Members.OfType<FieldDecl>())
        {
            var ft = ResolveType(fd.Type);
            var fs = new FieldSymbol
                { Name = fd.Name, Type = ft, Owner = t, IsStatic = fd.IsStatic, IsWeak = fd.IsWeak };
            if (fd.IsWeak && !ft.IsReferenceType)
            {
                Report(fd.Line, "'weak' may only be applied to reference-typed fields");
            }

            if (fs.IsStatic)
            {
                t.StaticFields.Add(fs);
            }
            else
            {
                fs.Index = t.InstanceFields.Count;
                t.InstanceFields.Add(fs);
            }
        }

        // methods
        foreach (var md in decl.Members.OfType<MethodDecl>())
        {
            var m = new MethodSymbol
            {
                Name = md.IsConstructor ? "<ctor>" : md.Name,
                Owner = t,
                ReturnType = md.IsConstructor ? _void : ResolveType(md.ReturnType!),
                IsStatic = md.IsStatic,
                IsVirtual = md.IsVirtual,
                IsOverride = md.IsOverride,
                IsAbstract = md.IsAbstract,
                IsConstructor = md.IsConstructor,
                Syntax = md
            };
            var pi = 0;
            foreach (var p in md.Parameters)
            {
                m.Parameters.Add(new ParamSymbol { Name = p.Name, Type = ResolveType(p.Type), Index = pi++ });
            }

            t.Methods.Add(m);

            // vtable slot assignment
            if (m.IsOverride)
            {
                var baseM = FindVirtual(t.BaseType, m.Name, m.Parameters.Count);
                if (baseM != null)
                {
                    m.VtableSlot = baseM.VtableSlot;
                    t.Vtable[m.VtableSlot] = m;
                }
                else
                {
                    m.VtableSlot = t.Vtable.Count;
                    t.Vtable.Add(m);
                }
            }
            else if (m.IsVirtual)
            {
                m.VtableSlot = t.Vtable.Count;
                t.Vtable.Add(m);
            }
        }

        // interface implementation map
        foreach (var iface in AllInterfaces(t))
        {
            foreach (var im in iface.InterfaceSyntax!.Members.OfType<MethodDecl>())
            {
                var key = $"{iface.Name}::{im.Name}/{im.Parameters.Count}";
                if (!_selectors.TryGetValue(key, out var sel))
                {
                    continue;
                }

                var impl = FindMethod(t, im.Name, im.Parameters.Count);
                if (impl != null)
                {
                    t.InterfaceImpl[sel] = impl;
                }
                else if (t.Kind == TypeKind.Class)
                {
                    Report(decl.Line, $"'{t.Name}' does not implement '{iface.Name}.{im.Name}'");
                }
            }
        }
    }

    private IEnumerable<TypeSymbol> AllInterfaces(TypeSymbol t)
    {
        var seen = new HashSet<TypeSymbol>();
        var cur = t;
        while (cur != null)
        {
            foreach (var i in cur.Interfaces)
            {
                if (seen.Add(i))
                {
                    yield return i;
                }
            }

            cur = cur.BaseType;
        }
    }

    private MethodSymbol? FindVirtual(TypeSymbol? t, string name, int argc)
    {
        while (t != null)
        {
            foreach (var m in t.Methods)
            {
                if (m.Name == name && m.Parameters.Count == argc && (m.IsVirtual || m.IsOverride))
                {
                    return m;
                }
            }

            t = t.BaseType;
        }

        return null;
    }

    private MethodSymbol? FindMethod(TypeSymbol? t, string name, int argc)
    {
        while (t != null)
        {
            foreach (var m in t.Methods)
            {
                if (!m.IsConstructor && m.Name == name && m.Parameters.Count == argc)
                {
                    return m;
                }
            }

            t = t.BaseType;
        }

        return null;
    }

    private MethodSymbol? FindCtor(TypeSymbol t, int argc)
    {
        foreach (var m in t.Methods)
        {
            if (m.IsConstructor && m.Parameters.Count == argc)
            {
                return m;
            }
        }

        return null;
    }

    // ---- type resolution ---------------------------------------------------
    private TypeSymbol GetWeakRefType(TypeSymbol elem)
    {
        var key = "WeakReference<" + elem.Name + ">";
        if (!_weakRefCache.TryGetValue(key, out var w))
        {
            w = new TypeSymbol { Name = key, Kind = TypeKind.WeakRef, ElementType = elem };
            _weakRefCache[key] = w;
        }

        return w;
    }

    private TypeSymbol ResolveType(TypeSyntax ts)
    {
        if (ts.Name == "WeakReference" && ts.TypeArgs.Count == 1)
        {
            var elem = ResolveType(ts.TypeArgs[0]);
            if (!elem.IsReferenceType && elem.Kind != TypeKind.Error)
            {
                Report(ts.Line, "WeakReference<T> requires a reference type argument");
            }

            return GetWeakRefType(elem);
        }

        TypeSymbol baseT;
        if (!_types.TryGetValue(ts.Name, out var found))
        {
            Report(ts.Line, $"unknown type '{ts.Name}'");
            baseT = _error;
        }
        else
        {
            baseT = found;
        }

        if (ts.ArrayRank == 1)
        {
            var key = baseT.Name + "[]";
            if (!_arrayCache.TryGetValue(key, out var arr))
            {
                arr = new TypeSymbol { Name = key, Kind = TypeKind.Array, ElementType = baseT };
                _arrayCache[key] = arr;
            }

            return arr;
        }

        return baseT;
    }

    // ---- body binding ------------------------------------------------------
    private BoundMethodBody BindMethodBody(TypeSymbol type, MethodSymbol m)
    {
        _curType = type;
        _curMethod = m;
        _scopes.Clear();
        _methodLocals = [];
        _localId = 0;
        _params = [];
        foreach (var p in m.Parameters)
        {
            _params[p.Name] = p;
        }

        var block = BindBlock(m.Syntax!.Body!);

        if (m.IsConstructor)
        {
            var initCall = BuildCtorInit(m);
            if (initCall != null)
            {
                block.Statements.Insert(0, new BoundExprStmt { Expression = initCall });
            }
        }

        return new BoundMethodBody { Method = m, Body = block, AllLocals = _methodLocals };
    }

    private BoundExpr? BuildCtorInit(MethodSymbol ctor)
    {
        var md = ctor.Syntax!;
        TypeSymbol? targetType;
        List<Expr> argSyntax;
        if (md.HasCtorInit)
        {
            targetType = md.CtorInitIsThis ? _curType : _curType.BaseType;
            argSyntax = md.CtorInitArgs;
        }
        else if (_curType.BaseType != null && FindCtor(_curType.BaseType, 0) != null)
        {
            targetType = _curType.BaseType;
            argSyntax = [];
        }
        else
        {
            return null;
        }

        if (targetType == null)
        {
            Report(md.Line, "'base' constructor call has no base class");
            return null;
        }

        var args = argSyntax.Select(BindExpr).ToList();
        var target = FindCtor(targetType, args.Count);
        if (target == null)
        {
            Report(md.Line, $"no matching constructor on '{targetType}'");
            return null;
        }

        for (var i = 0; i < args.Count; i++)
        {
            args[i] = Convert(args[i], target.Parameters[i].Type, md.Line);
        }

        return new BoundCall
        {
            Receiver = new BoundThis { Type = _curType }, Method = target, Arguments = args, Type = _void,
            Virtual = false
        };
    }

    private void PushScope() => _scopes.Add([]);
    private void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

    private LocalSymbol DeclareLocal(string name, TypeSymbol type, int line)
    {
        var sym = new LocalSymbol { Name = name, Type = type, Id = _localId++ };
        _scopes[^1][name] = sym;
        _methodLocals.Add(sym);
        return sym;
    }

    private LocalSymbol? LookupLocal(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out var s))
            {
                return s;
            }
        }

        return null;
    }

    private BoundBlock BindBlock(BlockStmt b)
    {
        PushScope();
        var bb = new BoundBlock();
        foreach (var s in b.Statements)
        {
            bb.Statements.Add(BindStatement(s));
        }

        PopScope();
        return bb;
    }

    private BoundStmt BindStatement(Stmt s) => s switch
    {
        BlockStmt b => BindBlock(b),
        LocalDeclStmt d => BindLocalDecl(d),
        ExprStmt e => new BoundExprStmt { Expression = BindExpr(e.Expression) },
        IfStmt i => new BoundIf
        {
            Condition = BindToBool(i.Condition), Then = BindStatement(i.Then),
            Else = i.Else == null ? null : BindStatement(i.Else)
        },
        WhileStmt w => new BoundWhile { Condition = BindToBool(w.Condition), Body = BindStatement(w.Body) },
        ForStmt f => BindFor(f),
        ReturnStmt r => BindReturn(r),
        _ => new BoundBlock()
    };

    private BoundStmt BindLocalDecl(LocalDeclStmt d)
    {
        var init = d.Initializer == null ? null : BindExpr(d.Initializer);
        TypeSymbol type;
        if (d.Type.Name == "var")
        {
            if (init == null)
            {
                Report(d.Line, "'var' requires an initializer");
                type = _error;
            }
            else
            {
                type = init.Type == _null ? _error : init.Type;
            }
        }
        else
        {
            type = ResolveType(d.Type);
        }

        if (init != null)
        {
            init = Convert(init, type, d.Line);
        }

        var sym = DeclareLocal(d.Name, type, d.Line);
        return new BoundLocalDecl { Symbol = sym, Initializer = init };
    }

    private BoundStmt BindFor(ForStmt f)
    {
        PushScope();
        var init = f.Init == null ? null : BindStatement(f.Init);
        var cond = f.Condition == null ? null : BindToBool(f.Condition);
        var upd = f.Update == null ? null : BindExpr(f.Update);
        var body = BindStatement(f.Body);
        PopScope();
        return new BoundFor { Init = init, Condition = cond, Update = upd, Body = body };
    }

    private BoundStmt BindReturn(ReturnStmt r)
    {
        if (r.Value == null)
        {
            return new BoundReturn { Value = null };
        }

        var v = BindExpr(r.Value);
        v = Convert(v, _curMethod.ReturnType, r.Line);
        return new BoundReturn { Value = v };
    }

    private BoundExpr BindToBool(Expr e)
    {
        var b = BindExpr(e);
        if (b.Type != _bool && b.Type != _error)
        {
            Report(e.Line, $"condition must be 'bool', found '{b.Type}'");
        }

        return b;
    }

    // ---- expression binding ------------------------------------------------
    private BoundExpr BindExpr(Expr e) => e switch
    {
        LiteralExpr l => BindLiteral(l),
        NameExpr n => BindName(n),
        ThisExpr => BindThis(),
        MemberAccessExpr m => BindMemberAccess(m),
        InvocationExpr inv => BindInvocation(inv),
        NewObjectExpr no => BindNewObject(no),
        NewArrayExpr na => BindNewArray(na),
        IndexExpr ix => BindIndex(ix),
        BinaryExpr b => BindBinary(b),
        UnaryExpr u => BindUnary(u),
        AssignExpr a => BindAssign(a),
        CastExpr c => BindCast(c),
        _ => Err()
    };

    private BoundExpr Err() => new BoundLiteral { Type = _error, LitKind = LiteralKind.Null };

    private BoundExpr BindLiteral(LiteralExpr l) => l.Kind switch
    {
        LiteralKind.Int => new BoundLiteral { Type = _int, LitKind = l.Kind, IntValue = l.IntValue },
        LiteralKind.Long => new BoundLiteral { Type = _long, LitKind = l.Kind, IntValue = l.IntValue },
        LiteralKind.UInt => new BoundLiteral { Type = _uint, LitKind = l.Kind, IntValue = l.IntValue },
        LiteralKind.ULong => new BoundLiteral { Type = _ulong, LitKind = l.Kind, IntValue = l.IntValue },
        LiteralKind.Float => new BoundLiteral { Type = _float, LitKind = l.Kind, FloatValue = l.FloatValue },
        LiteralKind.Double => new BoundLiteral { Type = _double, LitKind = l.Kind, FloatValue = l.FloatValue },
        LiteralKind.Char => new BoundLiteral { Type = _char, LitKind = l.Kind, IntValue = l.IntValue },
        LiteralKind.Bool => new BoundLiteral { Type = _bool, LitKind = l.Kind, BoolValue = l.BoolValue },
        LiteralKind.String => new BoundLiteral { Type = _string, LitKind = l.Kind, StringValue = l.StringValue },
        _ => new BoundLiteral { Type = _null, LitKind = LiteralKind.Null }
    };

    private BoundExpr BindThis()
    {
        if (_curMethod.IsStatic)
        {
            Report(0, "'this' is not available in a static method");
        }

        return new BoundThis { Type = _curType };
    }

    private BoundExpr BindName(NameExpr n)
    {
        var local = LookupLocal(n.Name);
        if (local != null)
        {
            return new BoundLocal { Symbol = local, Type = local.Type };
        }

        if (_params.TryGetValue(n.Name, out var p))
        {
            return new BoundParam { Symbol = p, Type = p.Type };
        }

        // instance field via implicit this
        var f = FindField(_curType, n.Name, instance: true);
        if (f != null && !_curMethod.IsStatic)
        {
            return new BoundFieldAccess { Receiver = new BoundThis { Type = _curType }, Field = f, Type = f.Type };
        }

        // static field
        var sf = FindField(_curType, n.Name, instance: false);
        if (sf != null)
        {
            return new BoundFieldAccess { Receiver = null, Field = sf, Type = sf.Type };
        }

        Report(n.Line, $"unknown name '{n.Name}'", "ARC2002");
        return Err();
    }

    private FieldSymbol? FindField(TypeSymbol? t, string name, bool instance)
    {
        while (t != null)
        {
            foreach (var f in (instance ? t.InstanceFields : t.StaticFields))
            {
                if (f.Name == name)
                {
                    return f;
                }
            }

            // static fields are not inherited in our model; instance are already flattened
            if (instance)
            {
                break;
            }

            t = t.BaseType;
        }

        return null;
    }

    private BoundExpr BindMemberAccess(MemberAccessExpr m)
    {
        // static member: Target is a type name
        if (m.Target is NameExpr tn && _types.TryGetValue(tn.Name, out var typeRef)
                                    && LookupLocal(tn.Name) == null && !_params.ContainsKey(tn.Name))
        {
            var sf = FindField(typeRef, m.Name, instance: false);
            if (sf != null)
            {
                return new BoundFieldAccess { Receiver = null, Field = sf, Type = sf.Type };
            }

            Report(m.Line, $"'{tn.Name}' has no static member '{m.Name}'");
            return Err();
        }

        var recv = BindExpr(m.Target);
        if (m.Name == "Length" && (recv.Type.Kind == TypeKind.Array || recv.Type.Kind == TypeKind.String))
        {
            return new BoundLength { Receiver = recv, IsString = recv.Type.Kind == TypeKind.String, Type = _int };
        }

        var f = FindField(recv.Type, m.Name, instance: true);
        if (f != null)
        {
            return new BoundFieldAccess { Receiver = recv, Field = f, Type = f.Type };
        }

        Report(m.Line, $"'{recv.Type}' has no member '{m.Name}'");
        return Err();
    }

    private BoundExpr BindInvocation(InvocationExpr inv)
    {
        var args = inv.Arguments.Select(BindExpr).ToList();

        if (inv.Callee is MemberAccessExpr ma)
        {
            // Console.WriteLine / Console.Write
            if (ma.Target is NameExpr cn && cn.Name == "Console"
                                         && LookupLocal("Console") == null && !_params.ContainsKey("Console"))
            {
                return BindConsole(ma.Name, args, inv.Line);
            }

            // static method on a type
            if (ma.Target is NameExpr tn && _types.TryGetValue(tn.Name, out var typeRef)
                                         && LookupLocal(tn.Name) == null && !_params.ContainsKey(tn.Name))
            {
                var sm = FindMethod(typeRef, ma.Name, args.Count);
                if (sm != null && sm.IsStatic)
                {
                    return MakeCall(null, sm, args, inv.Line);
                }

                Report(inv.Line, $"'{tn.Name}' has no static method '{ma.Name}'");
                return Err();
            }

            // instance / virtual / interface method
            var recv = BindExpr(ma.Target);
            if (recv.Type.Kind == TypeKind.WeakRef)
            {
                return BindWeakRefCall(recv, ma.Name, inv.Arguments, inv.Line);
            }

            if (recv.Type.Kind == TypeKind.Interface)
            {
                var key = $"{recv.Type.Name}::{ma.Name}/{args.Count}";
                if (_selectors.TryGetValue(key, out var sel))
                {
                    var im = FindInterfaceMethod(recv.Type, ma.Name, args.Count)!;
                    var call = MakeCall(recv, im, args, inv.Line);
                    if (call is BoundCall bc)
                    {
                        bc.Interface = true;
                        bc.Virtual = false;
                        bc.Selector = sel;
                    }

                    return call;
                }

                Report(inv.Line, $"interface '{recv.Type}' has no method '{ma.Name}'");
                return Err();
            }

            var method = FindMethod(recv.Type, ma.Name, args.Count);
            if (method == null)
            {
                Report(inv.Line, $"'{recv.Type}' has no method '{ma.Name}'");
                return Err();
            }

            var c = MakeCall(method.IsStatic ? null : recv, method, args, inv.Line);
            if (c is BoundCall b2)
            {
                b2.Virtual = method.IsVirtual || method.IsOverride;
            }

            return c;
        }

        if (inv.Callee is NameExpr nm)
        {
            var method = FindMethod(_curType, nm.Name, args.Count);
            if (method == null)
            {
                Report(inv.Line, $"unknown method '{nm.Name}'");
                return Err();
            }

            BoundExpr? recv = method.IsStatic ? null : new BoundThis { Type = _curType };
            var c = MakeCall(recv, method, args, inv.Line);
            if (c is BoundCall b2)
            {
                b2.Virtual = method.IsVirtual || method.IsOverride;
            }

            return c;
        }

        Report(inv.Line, "invalid call target");
        return Err();
    }

    private BoundExpr BindWeakRefCall(BoundExpr recv, string name, List<Expr> rawArgs, int line)
    {
        var elem = recv.Type.ElementType!;
        if (name == "TryGetTarget")
        {
            if (rawArgs.Count != 1 || rawArgs[0] is not OutArgExpr oa)
            {
                Report(line, "TryGetTarget expects a single 'out' argument");
                return Err();
            }

            var outLval = BindOutTarget(oa, elem, line);
            return new BoundWeakRefTryGet { WeakRef = recv, OutTarget = outLval, Type = _bool };
        }

        if (name == "SetTarget")
        {
            if (rawArgs.Count != 1)
            {
                Report(line, "SetTarget(target) takes one argument");
                return Err();
            }

            var t = Convert(BindExpr(rawArgs[0]), elem, line);
            return new BoundWeakRefSet { WeakRef = recv, Target = t, Type = _void };
        }

        Report(line, $"WeakReference<T> has no method '{name}'");
        return Err();
    }

    private BoundExpr BindOutTarget(OutArgExpr oa, TypeSymbol expected, int line)
    {
        if (oa.IsDeclaration)
        {
            var t = oa.IsVar ? expected : ResolveType(oa.DeclType!);
            var sym = DeclareLocal(oa.Name!, t, line);
            return new BoundLocal { Symbol = sym, Type = t };
        }

        var lval = BindExpr(oa.Target!);
        if (lval is not (BoundLocal or BoundParam or BoundFieldAccess or BoundIndex))
        {
            Report(line, "'out' argument must be a variable, field, or element");
        }

        return lval;
    }

    private MethodSymbol? FindInterfaceMethod(TypeSymbol iface, string name, int argc)
    {
        foreach (var m in iface.Methods)
        {
            if (m.Name == name && m.Parameters.Count == argc)
            {
                return m;
            }
        }

        return null;
    }

    private BoundExpr MakeCall(BoundExpr? recv, MethodSymbol m, List<BoundExpr> args, int line)
    {
        if (args.Count != m.Parameters.Count)
        {
            Report(line, $"method '{m.Name}' expects {m.Parameters.Count} args, got {args.Count}");
        }

        var conv = new List<BoundExpr>();
        for (var i = 0; i < args.Count && i < m.Parameters.Count; i++)
        {
            conv.Add(Convert(args[i], m.Parameters[i].Type, line));
        }

        return new BoundCall { Receiver = recv, Method = m, Arguments = conv, Type = m.ReturnType };
    }

    private BoundExpr BindConsole(string name, List<BoundExpr> args, int line)
    {
        var which = name == "Write" ? ConsoleKind.Write : ConsoleKind.WriteLine;
        if (name != "Write" && name != "WriteLine")
        {
            Report(line, $"Console has no method '{name}'");
            return Err();
        }

        if (args.Count == 0)
        {
            return new BoundConsoleCall { Which = which, Argument = null, Type = _void };
        }

        if (args.Count != 1)
        {
            Report(line, "Console.Write/WriteLine takes 0 or 1 argument");
        }

        return new BoundConsoleCall { Which = which, Argument = args[0], Type = _void };
    }

    private BoundExpr BindNewObject(NewObjectExpr no)
    {
        if (no.TypeName == "WeakReference" && no.TypeArgs.Count == 1)
        {
            var elem = ResolveType(no.TypeArgs[0]);
            var wr = GetWeakRefType(elem);
            if (no.Arguments.Count != 1)
            {
                Report(no.Line, "WeakReference<T>(target) takes one argument");
                return Err();
            }

            var target = Convert(BindExpr(no.Arguments[0]), elem, no.Line);
            return new BoundWeakRefNew { Target = target, Type = wr };
        }

        if (!_types.TryGetValue(no.TypeName, out var t) || t.Kind is not (TypeKind.Class or TypeKind.Struct))
        {
            Report(no.Line, $"cannot instantiate '{no.TypeName}'");
            return Err();
        }

        var args = no.Arguments.Select(BindExpr).ToList();
        var ctor = FindCtor(t, args.Count);
        if (ctor != null)
        {
            for (var i = 0; i < args.Count; i++)
            {
                args[i] = Convert(args[i], ctor.Parameters[i].Type, no.Line);
            }
        }
        else if (args.Count != 0)
        {
            Report(no.Line, $"no constructor of '{no.TypeName}' takes {args.Count} arguments");
        }

        return new BoundNewObject { Ctor = ctor, Arguments = args, Type = t };
    }

    private BoundExpr BindNewArray(NewArrayExpr na)
    {
        var elem = ResolveType(na.ElementType);
        var key = elem.Name + "[]";
        if (!_arrayCache.TryGetValue(key, out var arr))
        {
            arr = new TypeSymbol { Name = key, Kind = TypeKind.Array, ElementType = elem };
            _arrayCache[key] = arr;
        }

        var size = Convert(BindExpr(na.Size), _int, na.Line);
        return new BoundNewArray { ElementType = elem, Size = size, Type = arr };
    }

    private BoundExpr BindIndex(IndexExpr ix)
    {
        var recv = BindExpr(ix.Target);
        var idx = Convert(BindExpr(ix.Index), _int, ix.Line);
        if (recv.Type.Kind != TypeKind.Array)
        {
            Report(ix.Line, $"cannot index '{recv.Type}'");
            return Err();
        }

        return new BoundIndex { Receiver = recv, Index = idx, Type = recv.Type.ElementType! };
    }

    private BoundExpr BindBinary(BinaryExpr b)
    {
        var l = BindExpr(b.Left);
        var r = BindExpr(b.Right);
        if (l.Type == _error || r.Type == _error)
        {
            return Err();
        }

        // logical
        if (b.Op is TokenKind.AmpAmp or TokenKind.PipePipe)
        {
            if (l.Type != _bool || r.Type != _bool)
            {
                Report(b.Line, "logical operator requires 'bool' operands");
            }

            return new BoundBinary { Op = b.Op, Kind = BinKind.BoolLogic, Left = l, Right = r, Type = _bool };
        }

        // string concatenation
        if (b.Op == TokenKind.Plus && (l.Type == _string || r.Type == _string))
        {
            return new BoundBinary { Op = b.Op, Kind = BinKind.StrConcat, Left = l, Right = r, Type = _string };
        }

        // equality on references / null
        if (b.Op is TokenKind.EqualsEquals or TokenKind.BangEquals
            && (l.Type.IsReferenceType || r.Type.IsReferenceType || l.Type == _null || r.Type == _null))
        {
            return new BoundBinary { Op = b.Op, Kind = BinKind.RefEq, Left = l, Right = r, Type = _bool };
        }

        // numeric
        return BindNumeric(b, l, r);
    }

    private BoundExpr BindNumeric(BinaryExpr b, BoundExpr l, BoundExpr r)
    {
        if (!IsNumeric(l.Type) || !IsNumeric(r.Type))
        {
            Report(b.Line, $"operator '{b.Op}' cannot be applied to '{l.Type}' and '{r.Type}'");
            return Err();
        }

        // promotion order: double > float > long > int
        TypeSymbol ot;
        if (l.Type == _double || r.Type == _double)
        {
            ot = _double;
        }
        else if (l.Type == _float || r.Type == _float)
        {
            ot = _float;
        }
        else if (l.Type == _long || r.Type == _long)
        {
            ot = _long;
        }
        else
        {
            ot = _int;
        }

        l = Convert(l, ot, b.Line);
        r = Convert(r, ot, b.Line);
        var cmp = b.Op is TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater
            or TokenKind.GreaterEquals or TokenKind.EqualsEquals or TokenKind.BangEquals;

        var kind = (ot, cmp) switch
        {
            (_, true) when ot == _double => BinKind.DoubleCmp,
            (_, true) when ot == _float => BinKind.FloatCmp,
            (_, true) when ot == _long => BinKind.LongCmp,
            (_, true) => BinKind.IntCmp,
            (_, false) when ot == _double => BinKind.DoubleArith,
            (_, false) when ot == _float => BinKind.FloatArith,
            (_, false) when ot == _long => BinKind.LongArith,
            (_, false) => BinKind.IntArith,
        };
        return new BoundBinary { Op = b.Op, Kind = kind, Left = l, Right = r, Type = cmp ? _bool : ot };
    }

    private bool IsNumeric(TypeSymbol t) => t == _int || t == _long || t == _char || t == _float || t == _double;

    private BoundExpr BindUnary(UnaryExpr u)
    {
        var o = BindExpr(u.Operand);
        if (u.Op == TokenKind.Bang)
        {
            if (o.Type != _bool)
            {
                Report(u.Line, "'!' requires a 'bool' operand");
            }

            return new BoundUnary { Op = u.Op, Operand = o, Type = _bool };
        }

        // minus
        if (!IsNumeric(o.Type))
        {
            Report(u.Line, "unary '-' requires a numeric operand");
        }

        return new BoundUnary { Op = u.Op, Operand = o, Type = o.Type };
    }

    private BoundExpr BindAssign(AssignExpr a)
    {
        var target = BindExpr(a.Target);
        if (target is not (BoundLocal or BoundParam or BoundFieldAccess or BoundIndex))
        {
            Report(a.Line, "the left-hand side of an assignment must be a variable, field, or element");
            return Err();
        }

        if (target is BoundParam bp && bp.Symbol.Type.IsReferenceType)
        {
            Report(a.Line, "reassigning a reference-typed parameter is not supported in this first pass");
        }

        var value = BindExpr(a.Value);

        if (a.Op == TokenKind.PlusEquals || a.Op == TokenKind.MinusEquals)
        {
            var op = a.Op == TokenKind.PlusEquals ? TokenKind.Plus : TokenKind.Minus;
            // build target <op> value
            if (target.Type == _string && op == TokenKind.Plus)
            {
                value = new BoundBinary
                    { Op = TokenKind.Plus, Kind = BinKind.StrConcat, Left = target, Right = value, Type = _string };
            }
            else
            {
                var isLong = target.Type == _long;
                value = Convert(value, target.Type, a.Line);
                value = new BoundBinary
                {
                    Op = op, Kind = isLong ? BinKind.LongArith : BinKind.IntArith, Left = target, Right = value,
                    Type = target.Type
                };
            }
        }

        value = Convert(value, target.Type, a.Line);
        return new BoundAssign { Target = target, Value = value, Type = target.Type };
    }

    private BoundExpr BindCast(CastExpr c)
    {
        var target = ResolveType(c.Type);
        var o = BindExpr(c.Operand);
        // numeric casts and reference up/down casts -> conversion (mostly passthrough in IR)
        return new BoundConversion { Operand = o, Type = target };
    }

    // ---- conversions -------------------------------------------------------
    private BoundExpr Convert(BoundExpr e, TypeSymbol target, int line)
    {
        if (e.Type == target || target == _error || e.Type == _error)
        {
            return Setnull(e, target);
        }

        // null literal -> any reference type
        if (e.Type == _null && target.IsReferenceType)
        {
            return new BoundConversion { Operand = e, Type = target };
        }

        // int -> long
        if (e.Type == _int && target == _long)
        {
            return new BoundConversion { Operand = e, Type = target };
        }

        if (e.Type == _char && (target == _int || target == _long))
        {
            return new BoundConversion { Operand = e, Type = target };
        }

        // integer <-> floating point
        if (IsNumeric(e.Type) && IsNumeric(target) && e.Type != target)
        {
            return new BoundConversion { Operand = e, Type = target };
        }

        // reference up/down cast (subclass <-> base, class <-> interface)
        if (e.Type.IsReferenceType && target.IsReferenceType && RefCompatible(e.Type, target))
        {
            return new BoundConversion { Operand = e, Type = target };
        }

        Report(line, $"cannot convert '{e.Type}' to '{target}'", "ARC2010");
        return e;
    }

    private BoundExpr Setnull(BoundExpr e, TypeSymbol target)
    {
        // when e is null literal but already typed as target (e.g. var), keep as-is
        return e;
    }

    private bool RefCompatible(TypeSymbol from, TypeSymbol to)
    {
        if (from == to)
        {
            return true;
        }

        if (from.IsSubclassOf(to) || to.IsSubclassOf(from))
        {
            return true;
        }

        if (to.Kind == TypeKind.Interface && from.Implements(to))
        {
            return true;
        }

        if (from.Kind == TypeKind.Interface)
        {
            return true; // downcast from interface, checked at runtime (unchecked here)
        }

        if (from == _string && to == _string)
        {
            return true;
        }

        return false;
    }

    private void Report(int line, string msg, string id = "ARC2001",
        DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        Diagnostics.Add(new Diagnostic { Message = msg, Line = line, Column = 0, Id = id, Severity = severity });
}
