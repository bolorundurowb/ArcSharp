using ArcSharp.Lexing;

namespace ArcSharp.Syntax;

public sealed class Parser(List<Token> tokens)
{
    private readonly List<Token> _tokens = tokens;
    private int _i;
    public List<Diagnostic> Diagnostics { get; } = [];

    private Token Current => _tokens[Math.Min(_i, _tokens.Count - 1)];
    private Token Peek(int n) => _tokens[Math.Min(_i + n, _tokens.Count - 1)];
    private bool At(TokenKind k) => Current.Kind == k;

    private Token Advance()
    {
        var t = Current;
        if (_i < _tokens.Count - 1)
        {
            _i++;
        }

        return t;
    }

    private bool Accept(TokenKind k)
    {
        if (At(k))
        {
            Advance();
            return true;
        }

        return false;
    }

    private Token Expect(TokenKind k)
    {
        if (At(k))
        {
            return Advance();
        }

        Report($"expected {k} but found {Current.Kind} '{Current.Text}'");
        return Current;
    }

    private void Report(string msg, string id = "ARC1001") =>
        Diagnostics.Add(new Diagnostic { Message = msg, Line = Current.Line, Column = Current.Column, Id = id });

    // ---- Compilation unit --------------------------------------------------
    public CompilationUnit ParseCompilationUnit()
    {
        var cu = new CompilationUnit { Line = Current.Line };
        while (!At(TokenKind.EndOfFile))
        {
            if (At(TokenKind.UsingKw))
            {
                SkipUsing();
                continue;
            }

            if (At(TokenKind.NamespaceKw))
            {
                ParseNamespaceInto(cu);
                continue;
            }

            var t = ParseTypeDecl();
            if (t != null)
            {
                cu.Types.Add(t);
            }
            else
            {
                if (!At(TokenKind.EndOfFile))
                {
                    Advance();
                }
            }
        }

        return cu;
    }

    private void SkipUsing()
    {
        while (!At(TokenKind.Semicolon) && !At(TokenKind.EndOfFile))
        {
            Advance();
        }

        Accept(TokenKind.Semicolon);
    }

    private void ParseNamespaceInto(CompilationUnit cu)
    {
        Expect(TokenKind.NamespaceKw);
        while (!At(TokenKind.OpenBrace) && !At(TokenKind.Semicolon) && !At(TokenKind.EndOfFile))
        {
            Advance();
        }

        if (Accept(TokenKind.Semicolon))
        {
            while (!At(TokenKind.EndOfFile))
            {
                if (At(TokenKind.UsingKw))
                {
                    SkipUsing();
                    continue;
                }

                var t = ParseTypeDecl();
                if (t != null)
                {
                    cu.Types.Add(t);
                }
                else
                {
                    Advance();
                }
            }

            return;
        }

        Expect(TokenKind.OpenBrace);
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            if (At(TokenKind.UsingKw))
            {
                SkipUsing();
                continue;
            }

            var t = ParseTypeDecl();
            if (t != null)
            {
                cu.Types.Add(t);
            }
            else
            {
                Advance();
            }
        }

        Expect(TokenKind.CloseBrace);
    }

    // ---- Type declarations -------------------------------------------------
    private TypeDecl? ParseTypeDecl()
    {
        var isAbstract = false;
        while (true)
        {
            if (Accept(TokenKind.PublicKw) || Accept(TokenKind.PrivateKw) ||
                Accept(TokenKind.ProtectedKw) || Accept(TokenKind.InternalKw) ||
                Accept(TokenKind.StaticKw))
            {
                continue;
            }

            if (At(TokenKind.AbstractKw))
            {
                isAbstract = true;
                Advance();
                continue;
            }

            break;
        }

        if (At(TokenKind.ClassKw))
        {
            return ParseClass(isAbstract);
        }

        if (At(TokenKind.StructKw))
        {
            return ParseStruct();
        }

        if (At(TokenKind.InterfaceKw))
        {
            return ParseInterface();
        }

        Report($"expected type declaration, found {Current.Kind}");
        return null;
    }

    private ClassDecl ParseClass(bool isAbstract)
    {
        var line = Current.Line;
        Expect(TokenKind.ClassKw);
        var name = Expect(TokenKind.Identifier).Text;
        var cls = new ClassDecl { Name = name, Line = line, IsAbstract = isAbstract };
        if (Accept(TokenKind.Colon))
        {
            do
            {
                var b = Expect(TokenKind.Identifier).Text;
                cls.InterfaceNames.Add(b);
            } while (Accept(TokenKind.Comma));
        }

        Expect(TokenKind.OpenBrace);
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            ParseMemberInto(cls, name);
        }

        Expect(TokenKind.CloseBrace);
        return cls;
    }

    private StructDecl ParseStruct()
    {
        var line = Current.Line;
        Expect(TokenKind.StructKw);
        var name = Expect(TokenKind.Identifier).Text;
        var s = new StructDecl { Name = name, Line = line };
        if (Accept(TokenKind.Colon))
        {
            do
            {
                Expect(TokenKind.Identifier);
            } while (Accept(TokenKind.Comma));
        }

        Expect(TokenKind.OpenBrace);
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            ParseMemberInto(s, name);
        }

        Expect(TokenKind.CloseBrace);
        return s;
    }

    private InterfaceDecl ParseInterface()
    {
        var line = Current.Line;
        Expect(TokenKind.InterfaceKw);
        var name = Expect(TokenKind.Identifier).Text;
        var iface = new InterfaceDecl { Name = name, Line = line };
        if (Accept(TokenKind.Colon))
        {
            do
            {
                Expect(TokenKind.Identifier);
            } while (Accept(TokenKind.Comma));
        }

        Expect(TokenKind.OpenBrace);
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            ParseMemberInto(iface, name);
        }

        Expect(TokenKind.CloseBrace);
        return iface;
    }

    // ---- Members -----------------------------------------------------------
    private void ParseMemberInto(TypeDecl owner, string typeName)
    {
        var line = Current.Line;
        bool isStatic = false,
            isVirtual = false,
            isOverride = false,
            isAbstract = false,
            isWeak = false,
            isPublic = false;
        while (true)
        {
            if (Accept(TokenKind.PublicKw))
            {
                isPublic = true;
                continue;
            }

            if (Accept(TokenKind.PrivateKw) || Accept(TokenKind.ProtectedKw) || Accept(TokenKind.InternalKw))
            {
                continue;
            }

            if (Accept(TokenKind.StaticKw))
            {
                isStatic = true;
                continue;
            }

            if (Accept(TokenKind.VirtualKw))
            {
                isVirtual = true;
                continue;
            }

            if (Accept(TokenKind.OverrideKw))
            {
                isOverride = true;
                continue;
            }

            if (Accept(TokenKind.AbstractKw))
            {
                isAbstract = true;
                continue;
            }

            if (Accept(TokenKind.WeakKw))
            {
                isWeak = true;
                continue;
            }

            if (Accept(TokenKind.NewKw))
            {
                continue;
            }

            break;
        }

        if (At(TokenKind.Identifier) && Current.Text == typeName && Peek(1).Kind == TokenKind.OpenParen)
        {
            var ctor = new MethodDecl
                { Name = typeName, Line = line, IsConstructor = true, IsStatic = isStatic, IsPublic = isPublic };
            Advance(); // consume constructor name
            ParseParameterList(ctor.Parameters);
            if (Accept(TokenKind.Colon))
            {
                ctor.HasCtorInit = true;
                if (Accept(TokenKind.ThisKw))
                {
                    ctor.CtorInitIsThis = true;
                }
                else
                {
                    Expect(TokenKind.BaseKw);
                }

                ctor.CtorInitArgs = ParseArgumentList();
            }

            ctor.Body = At(TokenKind.OpenBrace) ? ParseBlock() : null;
            Accept(TokenKind.Semicolon);
            owner.Members.Add(ctor);
            return;
        }

        var type = ParseType();
        var name = Expect(TokenKind.Identifier).Text;

        if (At(TokenKind.OpenParen))
        {
            var m = new MethodDecl
            {
                Name = name,
                ReturnType = type,
                Line = line,
                IsStatic = isStatic,
                IsVirtual = isVirtual,
                IsOverride = isOverride,
                IsAbstract = isAbstract || owner is InterfaceDecl,
                IsPublic = isPublic
            };
            ParseParameterList(m.Parameters);
            if (At(TokenKind.OpenBrace))
            {
                m.Body = ParseBlock();
            }
            else
            {
                Expect(TokenKind.Semicolon);
            }

            owner.Members.Add(m);
        }
        else
        {
            var f = new FieldDecl
                { Type = type, Name = name, Line = line, IsStatic = isStatic, IsWeak = isWeak, IsPublic = isPublic };
            if (Accept(TokenKind.Assign))
            {
                ParseExpression();
            }

            Expect(TokenKind.Semicolon);
            owner.Members.Add(f);
        }
    }

    private void ParseParameterList(List<ParameterSyntax> into)
    {
        Expect(TokenKind.OpenParen);
        if (!At(TokenKind.CloseParen))
        {
            do
            {
                var t = ParseType();
                var n = Expect(TokenKind.Identifier).Text;
                into.Add(new ParameterSyntax { Type = t, Name = n, Line = t.Line });
            } while (Accept(TokenKind.Comma));
        }

        Expect(TokenKind.CloseParen);
    }

    private TypeSyntax ParseType()
    {
        var line = Current.Line;
        var name = Current.Kind switch
        {
            TokenKind.IntKw => "int",
            TokenKind.LongKw => "long",
            TokenKind.BoolKw => "bool",
            TokenKind.StringKw => "string",
            TokenKind.CharKw => "char",
            TokenKind.FloatKw => "float",
            TokenKind.DoubleKw => "double",
            TokenKind.ByteKw => "byte",
            TokenKind.SByteKw => "sbyte",
            TokenKind.ShortKw => "short",
            TokenKind.UShortKw => "ushort",
            TokenKind.UIntKw => "uint",
            TokenKind.ULongKw => "ulong",
            TokenKind.VoidKw => "void",
            TokenKind.VarKw => "var",
            TokenKind.Identifier => Current.Text,
            _ => "?"
        };
        if (name == "?")
        {
            Report($"expected type, found {Current.Kind}");
        }

        Advance();
        var ts = new TypeSyntax { Name = name, Line = line };
        if (At(TokenKind.Less))
        {
            ParseTypeArgList(ts.TypeArgs);
        }

        ts.Nullable = Accept(TokenKind.Question);
        if (At(TokenKind.OpenBracket) && Peek(1).Kind == TokenKind.CloseBracket)
        {
            Advance();
            Advance();
            ts.ArrayRank = 1;
            ts.Nullable = ts.Nullable || Accept(TokenKind.Question);
        }

        return ts;
    }

    private void ParseTypeArgList(List<TypeSyntax> into)
    {
        Expect(TokenKind.Less);
        do
        {
            into.Add(ParseType());
        } while (Accept(TokenKind.Comma));

        Expect(TokenKind.Greater);
    }

    // ---- Statements --------------------------------------------------------
    private BlockStmt ParseBlock()
    {
        var line = Current.Line;
        Expect(TokenKind.OpenBrace);
        var block = new BlockStmt { Line = line };
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            var before = _i;
            block.Statements.Add(ParseStatement());
            if (_i == before)
            {
                Advance();
            }
        }

        Expect(TokenKind.CloseBrace);
        return block;
    }

    private Stmt ParseStatement()
    {
        return Current.Kind switch
        {
            TokenKind.OpenBrace => ParseBlock(),
            TokenKind.IfKw => ParseIf(),
            TokenKind.WhileKw => ParseWhile(),
            TokenKind.ForKw => ParseFor(),
            TokenKind.ReturnKw => ParseReturn(),
            _ => ParseSimpleStatement(requireSemicolon: true),
        };
    }

    private Stmt ParseSimpleStatement(bool requireSemicolon)
    {
        if (LooksLikeLocalDecl())
        {
            var line = Current.Line;
            var type = ParseType();
            var name = Expect(TokenKind.Identifier).Text;
            Expr? init = null;
            if (Accept(TokenKind.Assign))
            {
                init = ParseExpression();
            }

            if (requireSemicolon)
            {
                Expect(TokenKind.Semicolon);
            }

            return new LocalDeclStmt { Type = type, Name = name, Initializer = init, Line = line };
        }
        else
        {
            var line = Current.Line;
            var e = ParseExpression();
            if (requireSemicolon)
            {
                Expect(TokenKind.Semicolon);
            }

            return new ExprStmt { Expression = e, Line = line };
        }
    }

    private bool LooksLikeLocalDecl()
    {
        if (Current.Kind is TokenKind.IntKw or TokenKind.LongKw or TokenKind.BoolKw
            or TokenKind.StringKw or TokenKind.CharKw or TokenKind.FloatKw or TokenKind.DoubleKw
            or TokenKind.ByteKw or TokenKind.SByteKw or TokenKind.ShortKw or TokenKind.UShortKw
            or TokenKind.UIntKw or TokenKind.ULongKw or TokenKind.VarKw)
        {
            if (Current.Kind == TokenKind.VarKw) return Peek(1).Kind == TokenKind.Identifier;
            if (Peek(1).Kind == TokenKind.Identifier) return true;
            if (Peek(1).Kind == TokenKind.OpenBracket && Peek(2).Kind == TokenKind.CloseBracket) return true;
            return false;
        }

        if (Peek(1).Kind == TokenKind.Identifier)
        {
            return true;
        }

        if (Peek(1).Kind == TokenKind.OpenBracket && Peek(2).Kind == TokenKind.CloseBracket)
        {
            return true;
        }

        return false;
    }
    if (Current.Kind == TokenKind.Identifier)
    {
        if (Peek(1).Kind == TokenKind.Identifier)
        {
            return true;
        }

        if (Peek(1).Kind == TokenKind.Question && Peek(2).Kind == TokenKind.Identifier)
        {
            return true;
        }

        if (Peek(1).Kind == TokenKind.OpenBracket && Peek(2).Kind == TokenKind.CloseBracket
                                                  && Peek(3).Kind == TokenKind.Identifier)
        {
            return true;
        }
    }

return false;

}

private Stmt ParseIf()
{
    var line = Current.Line;
    Expect(TokenKind.IfKw);
    Expect(TokenKind.OpenParen);
    var cond = ParseExpression();
    Expect(TokenKind.CloseParen);
    var then = ParseStatement();
    Stmt? els = null;
    if (Accept(TokenKind.ElseKw))
    {
        els = ParseStatement();
    }

    return new IfStmt { Condition = cond, Then = then, Else = els, Line = line };
}

private Stmt ParseWhile()
{
    var line = Current.Line;
    Expect(TokenKind.WhileKw);
    Expect(TokenKind.OpenParen);
    var cond = ParseExpression();
    Expect(TokenKind.CloseParen);
    var body = ParseStatement();
    return new WhileStmt { Condition = cond, Body = body, Line = line };
}

private Stmt ParseFor()
{
    var line = Current.Line;
    Expect(TokenKind.ForKw);
    Expect(TokenKind.OpenParen);
    Stmt? init = null;
    if (!At(TokenKind.Semicolon))
    {
        init = ParseSimpleStatement(requireSemicolon: false);
    }

    Expect(TokenKind.Semicolon);
    Expr? cond = null;
    if (!At(TokenKind.Semicolon))
    {
        cond = ParseExpression();
    }

    Expect(TokenKind.Semicolon);
    Expr? update = null;
    if (!At(TokenKind.CloseParen))
    {
        update = ParseExpression();
    }

    Expect(TokenKind.CloseParen);
    var body = ParseStatement();
    return new ForStmt { Init = init, Condition = cond, Update = update, Body = body, Line = line };
}

private Stmt ParseReturn()
{
    var line = Current.Line;
    Expect(TokenKind.ReturnKw);
    Expr? v = null;
    if (!At(TokenKind.Semicolon))
    {
        v = ParseExpression();
    }

    Expect(TokenKind.Semicolon);
    return new ReturnStmt { Value = v, Line = line };
}

// ---- Expressions (precedence climbing) --------------------------------
private Expr ParseExpression() => ParseAssignment();

private Expr ParseAssignment()
{
    var left = ParseOr();
    if (At(TokenKind.Assign) || At(TokenKind.PlusEquals) || At(TokenKind.MinusEquals))
    {
        var op = Advance().Kind;
        var right = ParseAssignment();
        return new AssignExpr { Target = left, Value = right, Op = op, Line = left.Line };
    }

    return left;
}

private Expr ParseOr()
{
    var left = ParseAnd();
    while (At(TokenKind.PipePipe))
    {
        var op = Advance().Kind;
        var right = ParseAnd();
        left = new BinaryExpr { Op = op, Left = left, Right = right, Line = left.Line };
    }

    return left;
}

private Expr ParseAnd()
{
    var left = ParseEquality();
    while (At(TokenKind.AmpAmp))
    {
        var op = Advance().Kind;
        var right = ParseEquality();
        left = new BinaryExpr { Op = op, Left = left, Right = right, Line = left.Line };
    }

    return left;
}

private Expr ParseEquality()
{
    var left = ParseRelational();
    while (At(TokenKind.EqualsEquals) || At(TokenKind.BangEquals))
    {
        var op = Advance().Kind;
        var right = ParseRelational();
        left = new BinaryExpr { Op = op, Left = left, Right = right, Line = left.Line };
    }

    return left;
}

private Expr ParseRelational()
{
    var left = ParseAdditive();
    while (At(TokenKind.Less) || At(TokenKind.LessEquals) || At(TokenKind.Greater) || At(TokenKind.GreaterEquals))
    {
        var op = Advance().Kind;
        var right = ParseAdditive();
        left = new BinaryExpr { Op = op, Left = left, Right = right, Line = left.Line };
    }

    return left;
}

private Expr ParseAdditive()
{
    var left = ParseMultiplicative();
    while (At(TokenKind.Plus) || At(TokenKind.Minus))
    {
        var op = Advance().Kind;
        var right = ParseMultiplicative();
        left = new BinaryExpr { Op = op, Left = left, Right = right, Line = left.Line };
    }

    return left;
}

private Expr ParseMultiplicative()
{
    var left = ParseUnary();
    while (At(TokenKind.Star) || At(TokenKind.Slash) || At(TokenKind.Percent))
    {
        var op = Advance().Kind;
        var right = ParseUnary();
        left = new BinaryExpr { Op = op, Left = left, Right = right, Line = left.Line };
    }

    return left;
}

private Expr ParseUnary()
{
    if (At(TokenKind.Bang) || At(TokenKind.Minus))
    {
        var line = Current.Line;
        var op = Advance().Kind;
        var operand = ParseUnary();
        return new UnaryExpr { Op = op, Operand = operand, Line = line };
    }

    if (At(TokenKind.OpenParen) && TryParseCast(out var cast))
    {
        return cast!;
    }

    return ParsePostfix();
}

private bool TryParseCast(out Expr? cast)
{
    cast = null;
    var save = _i;
    if (!At(TokenKind.OpenParen))
    {
        return false;
    }

    Advance();
    var typeStart = Current.Kind is TokenKind.IntKw or TokenKind.LongKw or TokenKind.BoolKw
        or TokenKind.StringKw or TokenKind.CharKw or TokenKind.FloatKw or TokenKind.DoubleKw or TokenKind.Identifier;
    if (!typeStart)
    {
        _i = save;
        return false;
    }

    var type = ParseType();
    if (!At(TokenKind.CloseParen))
    {
        _i = save;
        return false;
    }

    Advance();
    var exprStart = Current.Kind is TokenKind.Identifier or TokenKind.IntLiteral or TokenKind.LongLiteral
        or TokenKind.FloatLiteral or TokenKind.DoubleLiteral
        or TokenKind.StringLiteral or TokenKind.CharLiteral or TokenKind.TrueKw or TokenKind.FalseKw
        or TokenKind.NullKw or TokenKind.OpenParen or TokenKind.ThisKw or TokenKind.NewKw
        or TokenKind.Bang or TokenKind.Minus;
    if (!exprStart)
    {
        _i = save;
        return false;
    }

    var operand = ParseUnary();
    cast = new CastExpr { Type = type, Operand = operand, Line = type.Line };
    return true;
}

private Expr ParsePostfix()
{
    var e = ParsePrimary();
    while (true)
    {
        if (At(TokenKind.Dot))
        {
            Advance();
            var name = Expect(TokenKind.Identifier).Text;
            e = new MemberAccessExpr { Target = e, Name = name, Line = e.Line };
        }
        else if (At(TokenKind.OpenParen))
        {
            var args = ParseArgumentList();
            e = new InvocationExpr { Callee = e, Arguments = args, Line = e.Line };
        }
        else if (At(TokenKind.OpenBracket))
        {
            Advance();
            var idx = ParseExpression();
            Expect(TokenKind.CloseBracket);
            e = new IndexExpr { Target = e, Index = idx, Line = e.Line };
        }
        else
        {
            break;
        }
    }

    return e;
}

private List<Expr> ParseArgumentList()
{
    Expect(TokenKind.OpenParen);
    var args = new List<Expr>();
    if (!At(TokenKind.CloseParen))
    {
        do
        {
            args.Add(ParseArgument());
        } while (Accept(TokenKind.Comma));
    }

    Expect(TokenKind.CloseParen);
    return args;
}

private Expr ParseArgument()
{
    if (Accept(TokenKind.OutKw))
    {
        var line = Current.Line;
        if (At(TokenKind.VarKw) && Peek(1).Kind == TokenKind.Identifier)
        {
            Advance();
            var n = Advance().Text;
            return new OutArgExpr { IsDeclaration = true, IsVar = true, Name = n, Line = line };
        }

        if (LooksLikeOutDecl())
        {
            var t = ParseType();
            var n = Expect(TokenKind.Identifier).Text;
            return new OutArgExpr { IsDeclaration = true, DeclType = t, Name = n, Line = line };
        }

        var tgt = ParseExpression();
        return new OutArgExpr { Target = tgt, Line = line };
    }

    return ParseExpression();
}

private bool LooksLikeOutDecl()
{
    var typeStart = Current.Kind is TokenKind.IntKw or TokenKind.LongKw or TokenKind.BoolKw
        or TokenKind.StringKw or TokenKind.CharKw or TokenKind.FloatKw or TokenKind.DoubleKw
        or TokenKind.ByteKw or TokenKind.SByteKw or TokenKind.ShortKw or TokenKind.UShortKw
        or TokenKind.UIntKw or TokenKind.ULongKw or TokenKind.Identifier;
    return typeStart && Peek(1).Kind == TokenKind.Identifier;
}

private Expr ParsePrimary()
{
    var line = Current.Line;
    switch (Current.Kind)
    {
        case TokenKind.IntLiteral:
        {
            var t = Advance();
            return new LiteralExpr { Kind = LiteralKind.Int, IntValue = long.Parse(t.Text), Line = line };
        }
        case TokenKind.LongLiteral:
        {
            var t = Advance();
            return new LiteralExpr { Kind = LiteralKind.Long, IntValue = long.Parse(t.Text), Line = line };
        }
        case TokenKind.FloatLiteral:
        {
            var t = Advance();
            return new LiteralExpr
                { Kind = LiteralKind.Float, FloatValue = double.Parse(t.Text.TrimEnd('f', 'F')), Line = line };
        }
        case TokenKind.DoubleLiteral:
        {
            var t = Advance();
            return new LiteralExpr
                { Kind = LiteralKind.Double, FloatValue = double.Parse(t.Text.TrimEnd('d', 'D')), Line = line };
        }
        case TokenKind.CharLiteral:
        {
            var t = Advance();
            return new LiteralExpr { Kind = LiteralKind.Char, IntValue = long.Parse(t.Text), Line = line };
        }
        case TokenKind.StringLiteral:
        {
            var t = Advance();
            return new LiteralExpr { Kind = LiteralKind.String, StringValue = t.Text, Line = line };
        }
        case TokenKind.TrueKw:
            Advance();
            return new LiteralExpr { Kind = LiteralKind.Bool, BoolValue = true, Line = line };
        case TokenKind.FalseKw:
            Advance();
            return new LiteralExpr { Kind = LiteralKind.Bool, BoolValue = false, Line = line };
        case TokenKind.NullKw:
            Advance();
            return new LiteralExpr { Kind = LiteralKind.Null, Line = line };
        case TokenKind.ThisKw:
            Advance();
            return new ThisExpr { Line = line };
        case TokenKind.Identifier:
        {
            var t = Advance();
            return new NameExpr { Name = t.Text, Line = line };
        }
        case TokenKind.NewKw: return ParseNew();
        case TokenKind.OpenParen:
        {
            Advance();
            var e = ParseExpression();
            Expect(TokenKind.CloseParen);
            return e;
        }
        default:
            Report($"unexpected token {Current.Kind} '{Current.Text}' in expression");
            Advance();
            return new LiteralExpr { Kind = LiteralKind.Null, Line = line };
    }
}

private Expr ParseNew()
{
    var line = Current.Line;
    Expect(TokenKind.NewKw);
    var type = ParseTypeNameForNew();
    var typeArgs = new List<TypeSyntax>();
    if (At(TokenKind.Less))
    {
        ParseTypeArgList(typeArgs);
    }

    if (At(TokenKind.OpenBracket))
    {
        Advance();
        var size = ParseExpression();
        Expect(TokenKind.CloseBracket);
        return new NewArrayExpr { ElementType = new TypeSyntax { Name = type, Line = line }, Size = size, Line = line };
    }

    var args = ParseArgumentList();
    return new NewObjectExpr { TypeName = type, TypeArgs = typeArgs, Arguments = args, Line = line };
}

private string ParseTypeNameForNew()
{
    string Eat(string n)
    {
        Advance();
        return n;
    }

    return Current.Kind switch
    {
        TokenKind.IntKw => Eat("int"),
        TokenKind.LongKw => Eat("long"),
        TokenKind.BoolKw => Eat("bool"),
        TokenKind.StringKw => Eat("string"),
        TokenKind.CharKw => Eat("char"),
        TokenKind.FloatKw => Eat("float"),
        TokenKind.DoubleKw => Eat("double"),
        TokenKind.Identifier => Advance().Text,
        _ => Eat("?")
    };
}
}
