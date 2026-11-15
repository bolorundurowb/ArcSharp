namespace ArcSharp.Syntax;

// Specialisations own their syntax: property expansion and binding must never
// mutate a template or another instantiation. Keep this exhaustive so new AST
// nodes cannot silently disappear during monomorphisation.
internal sealed class TypeSubstitution(IReadOnlyDictionary<string, TypeSyntax> arguments)
{
    public TypeSyntax Type(TypeSyntax t)
    {
        if (arguments.TryGetValue(t.Name, out var replacement))
        {
            return new TypeSyntax
            {
                Name = replacement.Name, TypeArgs = replacement.TypeArgs.Select(Type).ToList(),
                ArrayRank = t.ArrayRank + replacement.ArrayRank,
                Nullable = t.Nullable || replacement.Nullable, Line = t.Line
            };
        }
        return new TypeSyntax
        {
            Name = t.Name, TypeArgs = t.TypeArgs.Select(Type).ToList(),
            ArrayRank = t.ArrayRank, Nullable = t.Nullable, Line = t.Line
        };
    }

    public TypeDecl Declaration(TypeDecl declaration, string name)
    {
        TypeDecl result = declaration switch
        {
            ClassDecl c => new ClassDecl { Name = name, IsAbstract = c.IsAbstract, Bases = c.Bases.Select(Type).ToList() },
            StructDecl => new StructDecl { Name = name },
            InterfaceDecl => new InterfaceDecl { Name = name },
            _ => throw new InvalidOperationException($"Unhandled generic declaration: {declaration.GetType().Name}")
        };
        result.Line = declaration.Line;
        result.Members = declaration.Members.Select(Member).ToList();
        return result;
    }

    private MemberDecl Member(MemberDecl member)
    {
        MemberDecl result = member switch
        {
            FieldDecl f => new FieldDecl { Name = f.Name, Type = Type(f.Type), IsWeak = f.IsWeak },
            PropertyDecl p => new PropertyDecl { Name = p.Name, Type = Type(p.Type), HasGetter = p.HasGetter, HasSetter = p.HasSetter },
            MethodDecl m => Method(m),
            _ => throw new InvalidOperationException($"Unhandled generic member: {member.GetType().Name}")
        };
        result.IsStatic = member.IsStatic;
        result.IsPublic = member.IsPublic;
        result.Line = member.Line;
        return result;
    }

    public MethodDecl Method(MethodDecl m)
    {
        return new MethodDecl
        {
            Name = m.Name, Line = m.Line, ReturnType = m.ReturnType == null ? null : Type(m.ReturnType),
            TypeParameters = [.. m.TypeParameters],
            Constraints = m.Constraints.Select(c => new GenericConstraint
            {
                Parameter = c.Parameter, Line = c.Line, ReferenceType = c.ReferenceType,
                ValueType = c.ValueType, Constructor = c.Constructor, Types = c.Types.Select(Type).ToList()
            }).ToList(),
            Parameters = m.Parameters.Select(p => new ParameterSyntax
            {
                Name = p.Name, Line = p.Line, Type = Type(p.Type), RefKind = p.RefKind
            }).ToList(),
            IsStatic = m.IsStatic, IsPublic = m.IsPublic, IsVirtual = m.IsVirtual,
            IsOverride = m.IsOverride, IsAbstract = m.IsAbstract, IsConstructor = m.IsConstructor,
            HasCtorInit = m.HasCtorInit, CtorInitIsThis = m.CtorInitIsThis,
            CtorInitArgs = m.CtorInitArgs.Select(Expression).ToList(),
            Body = m.Body == null ? null : (BlockStmt)Statement(m.Body)
        };
    }

    private Stmt Statement(Stmt statement)
    {
        Stmt result = statement switch
        {
            BlockStmt b => new BlockStmt { Statements = b.Statements.Select(Statement).ToList() },
            LocalDeclStmt d => new LocalDeclStmt { Name = d.Name, Type = Type(d.Type), Initializer = Optional(d.Initializer) },
            ExprStmt e => new ExprStmt { Expression = Expression(e.Expression) },
            IfStmt i => new IfStmt { Condition = Expression(i.Condition), Then = Statement(i.Then), Else = i.Else == null ? null : Statement(i.Else) },
            WhileStmt w => new WhileStmt { Condition = Expression(w.Condition), Body = Statement(w.Body) },
            ForStmt f => new ForStmt { Init = f.Init == null ? null : Statement(f.Init), Condition = Optional(f.Condition), Update = Optional(f.Update), Body = Statement(f.Body) },
            ReturnStmt r => new ReturnStmt { Value = Optional(r.Value) },
            UsingStmt u => new UsingStmt { Resource = u.Resource == null ? null : (LocalDeclStmt)Statement(u.Resource), ResourceExpr = Optional(u.ResourceExpr), Body = Statement(u.Body) },
            _ => throw new InvalidOperationException($"Unhandled generic statement: {statement.GetType().Name}")
        };
        result.Line = statement.Line;
        return result;
    }

    private Expr? Optional(Expr? expression) => expression == null ? null : Expression(expression);

    private Expr Expression(Expr expression)
    {
        Expr result = expression switch
        {
            LiteralExpr l => new LiteralExpr { Kind = l.Kind, IntValue = l.IntValue, BoolValue = l.BoolValue, StringValue = l.StringValue, FloatValue = l.FloatValue },
            NameExpr n => new NameExpr { Name = n.Name, TypeArgs = n.TypeArgs.Select(Type).ToList() },
            ThisExpr => new ThisExpr(),
            MemberAccessExpr m => new MemberAccessExpr { Target = Expression(m.Target), Name = m.Name },
            InvocationExpr i => new InvocationExpr { Callee = Expression(i.Callee), TypeArgs = i.TypeArgs.Select(Type).ToList(), Arguments = i.Arguments.Select(Expression).ToList() },
            NewObjectExpr n => NewObject(n),
            NewArrayExpr n => new NewArrayExpr { ElementType = Type(n.ElementType), Size = Expression(n.Size) },
            ByRefArgExpr b => new ByRefArgExpr { Kind = b.Kind, IsDeclaration = b.IsDeclaration, IsVar = b.IsVar, DeclType = b.DeclType == null ? null : Type(b.DeclType), Name = b.Name, Target = Optional(b.Target) },
            IndexExpr i => new IndexExpr { Target = Expression(i.Target), Index = Expression(i.Index) },
            BinaryExpr b => new BinaryExpr { Op = b.Op, Left = Expression(b.Left), Right = Expression(b.Right) },
            UnaryExpr u => new UnaryExpr { Op = u.Op, Operand = Expression(u.Operand) },
            AssignExpr a => new AssignExpr { Op = a.Op, Target = Expression(a.Target), Value = Expression(a.Value) },
            CastExpr c => new CastExpr { Type = Type(c.Type), Operand = Expression(c.Operand) },
            IsExpr i => new IsExpr { TestType = Type(i.TestType), Operand = Expression(i.Operand) },
            AsExpr a => new AsExpr { TestType = Type(a.TestType), Operand = Expression(a.Operand) },
            _ => throw new InvalidOperationException($"Unhandled generic expression: {expression.GetType().Name}")
        };
        result.Line = expression.Line;
        return result;
    }

    private NewObjectExpr NewObject(NewObjectExpr n)
    {
        var type = Type(new TypeSyntax { Name = n.TypeName, TypeArgs = n.TypeArgs });
        return new NewObjectExpr { TypeName = type.Name, TypeArgs = type.TypeArgs, Arguments = n.Arguments.Select(Expression).ToList() };
    }
}
