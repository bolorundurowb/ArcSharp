using ArcSharp.Diagnostics;

namespace ArcSharp.Lexing;

public enum TokenKind
{
    // literals & names
    Identifier,
    IntLiteral,
    LongLiteral,
    UIntLiteral,
    ULongLiteral,
    FloatLiteral,
    DoubleLiteral,
    StringLiteral,
    CharLiteral,

    // keywords
    ClassKw,
    StructKw,
    InterfaceKw,
    PublicKw,
    PrivateKw,
    ProtectedKw,
    InternalKw,
    StaticKw,
    VirtualKw,
    OverrideKw,
    AbstractKw,
    NewKw,
    ReturnKw,
    IfKw,
    ElseKw,
    WhileKw,
    ForKw,
    TrueKw,
    FalseKw,
    NullKw,
    ThisKw,
    BaseKw,
    WeakKw,
    RefKw,
    OutKw,
    InKw,
    UsingKw,
    NamespaceKw,
    VoidKw,
    IntKw,
    LongKw,
    BoolKw,
    StringKw,
    CharKw,
    FloatKw,
    DoubleKw,
    ByteKw,
    SByteKw,
    ShortKw,
    UShortKw,
    UIntKw,
    ULongKw,
    VarKw,
    SealedKw,
    PartialKw,
    ReadonlyKw,
    ConstKw,

    // punctuation
    OpenBrace,
    CloseBrace,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    Semicolon,
    Comma,
    Dot,
    Colon,
    Question,

    // operators
    Assign,
    EqualsEquals,
    BangEquals,
    Less,
    LessEquals,
    Greater,
    GreaterEquals,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Bang,
    AmpAmp,
    PipePipe,
    FatArrow,
    PlusEquals,
    MinusEquals,
    IsKw,
    AsKw,
    EndOfFile,
    Bad
}

public readonly record struct Token(TokenKind Kind, string Text, int Position, int Line, int Column)
{
    public override string ToString() => $"{Kind}('{Text}')";
}

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info
}

public sealed class Diagnostic
{
    public required string Message { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public string Id { get; init; } = "ARC0000";
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

    private string SeverityText => Severity switch
    {
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Info => "info",
        _ => "error"
    };

    public override string ToString() => $"({Line},{Column}): {SeverityText} {Id}: {Message}";
}

public sealed class Lexer(string text)
{
    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["class"] = TokenKind.ClassKw,
        ["struct"] = TokenKind.StructKw,
        ["interface"] = TokenKind.InterfaceKw,
        ["public"] = TokenKind.PublicKw,
        ["private"] = TokenKind.PrivateKw,
        ["protected"] = TokenKind.ProtectedKw,
        ["internal"] = TokenKind.InternalKw,
        ["static"] = TokenKind.StaticKw,
        ["virtual"] = TokenKind.VirtualKw,
        ["override"] = TokenKind.OverrideKw,
        ["abstract"] = TokenKind.AbstractKw,
        ["new"] = TokenKind.NewKw,
        ["return"] = TokenKind.ReturnKw,
        ["if"] = TokenKind.IfKw,
        ["else"] = TokenKind.ElseKw,
        ["while"] = TokenKind.WhileKw,
        ["for"] = TokenKind.ForKw,
        ["true"] = TokenKind.TrueKw,
        ["false"] = TokenKind.FalseKw,
        ["null"] = TokenKind.NullKw,
        ["this"] = TokenKind.ThisKw,
        ["base"] = TokenKind.BaseKw,
        ["weak"] = TokenKind.WeakKw,
        ["ref"] = TokenKind.RefKw,
        ["out"] = TokenKind.OutKw,
        ["in"] = TokenKind.InKw,
        ["is"] = TokenKind.IsKw,
        ["as"] = TokenKind.AsKw,
        ["using"] = TokenKind.UsingKw,
        ["namespace"] = TokenKind.NamespaceKw,
        ["sealed"] = TokenKind.SealedKw,
        ["partial"] = TokenKind.PartialKw,
        ["readonly"] = TokenKind.ReadonlyKw,
        ["const"] = TokenKind.ConstKw,
        ["void"] = TokenKind.VoidKw,
        ["int"] = TokenKind.IntKw,
        ["long"] = TokenKind.LongKw,
        ["bool"] = TokenKind.BoolKw,
        ["string"] = TokenKind.StringKw,
        ["char"] = TokenKind.CharKw,
        ["float"] = TokenKind.FloatKw,
        ["double"] = TokenKind.DoubleKw,
        ["byte"] = TokenKind.ByteKw,
        ["sbyte"] = TokenKind.SByteKw,
        ["short"] = TokenKind.ShortKw,
        ["ushort"] = TokenKind.UShortKw,
        ["uint"] = TokenKind.UIntKw,
        ["ulong"] = TokenKind.ULongKw,
        ["var"] = TokenKind.VarKw,
    };

    private readonly string _text = text;
    private int _pos;
    private int _line = 1;
    private int _col = 1;
    public List<Diagnostic> Diagnostics { get; } = [];

    private char Current => _pos < _text.Length ? _text[_pos] : '\0';
    private char Peek(int n = 1) => _pos + n < _text.Length ? _text[_pos + n] : '\0';

    private void Advance()
    {
        if (Current == '\n')
        {
            _line++;
            _col = 1;
        }
        else
        {
            _col++;
        }

        _pos++;
    }

    public List<Token> Lex()
    {
        var tokens = new List<Token>();
        Token t;
        do
        {
            t = Next();
            if (t.Kind != TokenKind.Bad)
            {
                tokens.Add(t);
            }
        } while (t.Kind != TokenKind.EndOfFile);

        return tokens;
    }

    private Token Next()
    {
        SkipTrivia();
        int startLine = _line, startCol = _col, start = _pos;
        if (_pos >= _text.Length)
        {
            return new Token(TokenKind.EndOfFile, "", start, startLine, startCol);
        }

        var c = Current;
        if (char.IsLetter(c) || c == '_')
        {
            return LexIdentifierOrKeyword(start, startLine, startCol);
        }

        if (char.IsDigit(c))
        {
            return LexNumber(start, startLine, startCol);
        }

        if (c == '"')
        {
            return LexString(start, startLine, startCol);
        }

        if (c == '\'')
        {
            return LexChar(start, startLine, startCol);
        }

        return LexPunctuation(start, startLine, startCol);
    }

    private void SkipTrivia()
    {
        while (_pos < _text.Length)
        {
            var c = Current;
            if (char.IsWhiteSpace(c))
            {
                Advance();
                continue;
            }

            if (c == '/' && Peek() == '/')
            {
                while (_pos < _text.Length && Current != '\n')
                {
                    Advance();
                }

                continue;
            }

            if (c == '/' && Peek() == '*')
            {
                Advance();
                Advance();
                while (_pos < _text.Length && !(Current == '*' && Peek() == '/'))
                {
                    Advance();
                }

                if (_pos < _text.Length)
                {
                    Advance();
                    Advance();
                }

                continue;
            }

            break;
        }
    }

    private Token LexIdentifierOrKeyword(int start, int line, int col)
    {
        while (char.IsLetterOrDigit(Current) || Current == '_')
        {
            Advance();
        }

        var text = _text[start.._pos];
        var kind = Keywords.TryGetValue(text, out var kw) ? kw : TokenKind.Identifier;
        return new Token(kind, text, start, line, col);
    }

    private Token LexNumber(int start, int line, int col)
    {
        while (char.IsDigit(Current))
        {
            Advance();
        }

        var hasDot = false;

        // floating-point literal: digits . digits [suffix]
        if (Current == '.' && char.IsDigit(Peek()))
        {
            Advance(); // '.'
            while (char.IsDigit(Current))
            {
                Advance();
            }

            hasDot = true;
        }

        // suffix determines literal kind
        if (Current == 'f' || Current == 'F')
        {
            return LexFloatSuffix(start, line, col);
        }

        if (Current == 'd' || Current == 'D')
        {
            return LexDoubleSuffix(start, line, col);
        }

        // decimal literals without suffix are double (C# semantics)
        if (hasDot)
        {
            return new Token(TokenKind.DoubleLiteral, _text[start.._pos], start, line, col);
        }

        // integer suffixes
        if (Current == 'L' || Current == 'l')
        {
            var lt = _text[start.._pos];
            Advance();
            if (Current == 'U' || Current == 'u')
            {
                Advance();
                return new Token(TokenKind.ULongLiteral, lt, start, line, col);
            }

            return new Token(TokenKind.LongLiteral, lt, start, line, col);
        }

        if (Current == 'U' || Current == 'u')
        {
            var ut = _text[start.._pos];
            Advance();
            if (Current == 'L' || Current == 'l')
            {
                Advance();
                return new Token(TokenKind.ULongLiteral, ut, start, line, col);
            }

            return new Token(TokenKind.UIntLiteral, ut, start, line, col);
        }

        var text = _text[start.._pos];
        return new Token(TokenKind.IntLiteral, text, start, line, col);
    }

    private Token LexFloatSuffix(int start, int line, int col)
    {
        if (Current == 'f' || Current == 'F')
        {
            Advance();
        }

        var text = _text[start.._pos];
        return new Token(TokenKind.FloatLiteral, text, start, line, col);
    }

    private Token LexDoubleSuffix(int start, int line, int col)
    {
        if (Current == 'd' || Current == 'D')
        {
            Advance();
        }

        var text = _text[start.._pos];
        return new Token(TokenKind.DoubleLiteral, text, start, line, col);
    }

    private Token LexString(int start, int line, int col)
    {
        Advance(); // opening quote
        var sb = new System.Text.StringBuilder();
        while (_pos < _text.Length && Current != '"')
        {
            if (Current == '\\')
            {
                Advance();
                AppendStringEscape(sb, line, col);
            }
            else
            {
                sb.Append(Current);
                Advance();
            }
        }

        if (Current == '"')
        {
            Advance();
        }
        else
        {
            Report(line, col, "unterminated string literal", "ARC0002");
        }

        return new Token(TokenKind.StringLiteral, sb.ToString(), start, line, col);
    }

    private Token LexChar(int start, int line, int col)
    {
        Advance(); // opening quote
        var value = Current == '\\' ? ReadCharEscape(line, col) : ReadOne();

        if (Current == '\'')
        {
            Advance();
        }
        else
        {
            Report(line, col, "unterminated char literal", "ARC0002");
        }

        return new Token(TokenKind.CharLiteral, ((int)value).ToString(), start, line, col);
    }

    private void AppendStringEscape(System.Text.StringBuilder sb, int line, int col)
    {
        if (Current is 'u' or 'U')
        {
            if (TryReadUnicodeEscape(line, col, out var codePoint))
            {
                AppendCodePoint(sb, codePoint);
            }

            return;
        }

        sb.Append(ReadSimpleEscape());
    }

    private char ReadCharEscape(int line, int col)
    {
        Advance(); // backslash
        if (Current is 'u' or 'U')
        {
            if (!TryReadUnicodeEscape(line, col, out var codePoint))
            {
                return '\0';
            }

            if (codePoint > 0xFFFF)
            {
                Report(line, col, "Unicode escape does not fit in a character literal", "ARC0004");
                return '\0';
            }

            return (char)codePoint;
        }

        return ReadSimpleEscape();
    }

    private char ReadOne()
    {
        var value = Current;
        Advance();
        return value;
    }

    private char ReadSimpleEscape()
    {
        var value = Current switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '0' => '\0',
            '"' => '"',
            '\\' => '\\',
            '\'' => '\'',
            _ => Current
        };
        Advance();
        return value;
    }

    private bool TryReadUnicodeEscape(int line, int col, out int codePoint)
    {
        var digits = Current == 'U' ? 8 : 4;
        Advance();
        uint value = 0;
        for (var n = 0; n < digits; n++)
        {
            var hex = HexValue(Current);
            if (hex < 0)
            {
                Report(line, col, "invalid Unicode escape", "ARC0004");
                codePoint = 0;
                return false;
            }

            value = (value << 4) | (uint)hex;
            Advance();
        }

        if (value > 0x10FFFF)
        {
            Report(line, col, "Unicode escape is outside the Unicode range", "ARC0004");
            codePoint = 0;
            return false;
        }

        codePoint = (int)value;
        return true;
    }

    private static int HexValue(char c)
    {
        if (c is >= '0' and <= '9')
        {
            return c - '0';
        }

        if (c is >= 'a' and <= 'f')
        {
            return c - 'a' + 10;
        }

        if (c is >= 'A' and <= 'F')
        {
            return c - 'A' + 10;
        }

        return -1;
    }

    private static void AppendCodePoint(System.Text.StringBuilder sb, int codePoint)
    {
        if (codePoint <= 0xFFFF)
        {
            sb.Append((char)codePoint);
            return;
        }

        sb.Append(char.ConvertFromUtf32(codePoint));
    }

    private Token LexPunctuation(int start, int line, int col)
    {
        var c = Current;
        var n = Peek();
        TokenKind kind;
        var len = 1;
        switch (c)
        {
            case '{': kind = TokenKind.OpenBrace; break;
            case '}': kind = TokenKind.CloseBrace; break;
            case '(': kind = TokenKind.OpenParen; break;
            case ')': kind = TokenKind.CloseParen; break;
            case '[': kind = TokenKind.OpenBracket; break;
            case ']': kind = TokenKind.CloseBracket; break;
            case ';': kind = TokenKind.Semicolon; break;
            case ',': kind = TokenKind.Comma; break;
            case '.': kind = TokenKind.Dot; break;
            case ':': kind = TokenKind.Colon; break;
            case '?': kind = TokenKind.Question; break;
            case '=':
                if (n == '=')
                {
                    kind = TokenKind.EqualsEquals;
                    len = 2;
                }
                else if (n == '>')
                {
                    // A lambda body. Without this token the source parses as an
                    // assignment of a comparison and compiles into a wrong
                    // program with no diagnostic at all.
                    Report(line, col, "lambdas ('=>') are not supported", DiagnosticCatalog.UnsupportedConstructId);
                    Advance();
                    Advance();
                    return new Token(TokenKind.Bad, "=>", start, line, col);
                }
                else
                {
                    kind = TokenKind.Assign;
                }

                break;
            case '!':
                if (n == '=')
                {
                    kind = TokenKind.BangEquals;
                    len = 2;
                }
                else
                {
                    kind = TokenKind.Bang;
                }

                break;
            case '<':
                if (n == '<')
                {
                    Report(line, col, "shift operator '<<' not supported", "ARC0003");
                    Advance();
                    Advance();
                    return new Token(TokenKind.Bad, "<<", start, line, col);
                }

                if (n == '=')
                {
                    kind = TokenKind.LessEquals;
                    len = 2;
                }
                else
                {
                    kind = TokenKind.Less;
                }

                break;
            case '>':
                if (n == '>')
                {
                    Report(line, col, "shift operator '>>' not supported", "ARC0003");
                    Advance();
                    Advance();
                    return new Token(TokenKind.Bad, ">>", start, line, col);
                }

                if (n == '=')
                {
                    kind = TokenKind.GreaterEquals;
                    len = 2;
                }
                else
                {
                    kind = TokenKind.Greater;
                }

                break;
            case '+':
                if (n == '=')
                {
                    kind = TokenKind.PlusEquals;
                    len = 2;
                }
                else
                {
                    kind = TokenKind.Plus;
                }

                break;
            case '-':
                if (n == '=')
                {
                    kind = TokenKind.MinusEquals;
                    len = 2;
                }
                else
                {
                    kind = TokenKind.Minus;
                }

                break;
            case '*': kind = TokenKind.Star; break;
            case '/': kind = TokenKind.Slash; break;
            case '%': kind = TokenKind.Percent; break;
            case '&':
                if (n == '&')
                {
                    kind = TokenKind.AmpAmp;
                    len = 2;
                }
                else
                {
                    Report(line, col, "single '&' not supported", "ARC0003");
                    Advance();
                    return new Token(TokenKind.Bad, "&", start, line, col);
                }

                break;
            case '|':
                if (n == '|')
                {
                    kind = TokenKind.PipePipe;
                    len = 2;
                }
                else
                {
                    Report(line, col, "single '|' not supported", "ARC0003");
                    Advance();
                    return new Token(TokenKind.Bad, "|", start, line, col);
                }

                break;
            default:
                Report(line, col, $"unexpected character '{c}'");
                Advance();
                return new Token(TokenKind.Bad, c.ToString(), start, line, col);
        }

        for (var i = 0; i < len; i++)
        {
            Advance();
        }

        return new Token(kind, _text[start.._pos], start, line, col);
    }

    private void Report(int line, int col, string msg, string id = "ARC0001") =>
        Diagnostics.Add(new Diagnostic { Message = msg, Line = line, Column = col, Id = id });
}
