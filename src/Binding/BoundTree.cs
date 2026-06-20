using ArcSharp.Lexing;

namespace ArcSharp.Binding;

// ---- Bound expressions -----------------------------------------------------
public abstract class BoundExpr
{
    public required TypeSymbol Type;
}

public sealed class BoundLiteral : BoundExpr
{
    public Syntax.LiteralKind LitKind;
    public long IntValue;
    public bool BoolValue;
    public string StringValue = "";
    public double FloatValue; // used for float and double literals
}

public sealed class BoundLocal : BoundExpr
{
    public required LocalSymbol Symbol;
}

public sealed class BoundParam : BoundExpr
{
    public required ParamSymbol Symbol;
}

public sealed class BoundThis : BoundExpr
{
}

public sealed class BoundFieldAccess : BoundExpr
{
    public BoundExpr? Receiver; // null when static
    public required FieldSymbol Field;
}

public sealed class BoundCall : BoundExpr
{
    public BoundExpr? Receiver; // null when static
    public required MethodSymbol Method;
    public List<BoundExpr> Arguments = [];
    public bool Virtual;
    public bool Interface; // interface dispatch
    public int Selector = -1; // global interface selector when Interface
}

public sealed class BoundNewObject : BoundExpr
{
    public MethodSymbol? Ctor;
    public List<BoundExpr> Arguments = [];
}

public sealed class BoundNewArray : BoundExpr
{
    public required TypeSymbol ElementType;
    public required BoundExpr Size;
}

public sealed class BoundIndex : BoundExpr
{
    public required BoundExpr Receiver;
    public required BoundExpr Index;
}

public sealed class BoundWeakRefNew : BoundExpr
{
    public required BoundExpr Target; // T value the weak ref points at
}

public sealed class BoundWeakRefTryGet : BoundExpr
{
    public required BoundExpr WeakRef; // the WeakReference<T> object
    public required BoundExpr OutTarget; // lvalue receiving the strong (+1) target
}

public sealed class BoundWeakRefSet : BoundExpr
{
    public required BoundExpr WeakRef;
    public required BoundExpr Target;
}

public enum BinKind
{
    IntArith,
    LongArith,
    FloatArith,
    DoubleArith,
    IntCmp,
    LongCmp,
    FloatCmp,
    DoubleCmp,
    RefEq,
    BoolLogic,
    StrConcat,
    StrEq
}

public sealed class BoundBinary : BoundExpr
{
    public TokenKind Op;
    public BinKind Kind;
    public required BoundExpr Left;
    public required BoundExpr Right;
}

public sealed class BoundUnary : BoundExpr
{
    public TokenKind Op;
    public required BoundExpr Operand;
}

public sealed class BoundAssign : BoundExpr
{
    public required BoundExpr Target; // BoundLocal/BoundParam/BoundFieldAccess/BoundIndex
    public required BoundExpr Value;
}

public sealed class BoundConversion : BoundExpr
{
    public required BoundExpr Operand; // e.g. int -> long, or reference upcast/downcast
}

public sealed class BoundLength : BoundExpr // array.Length or string.Length
{
    public required BoundExpr Receiver;
    public bool IsString;
}

public enum ConsoleKind
{
    WriteLine,
    Write
}

public sealed class BoundConsoleCall : BoundExpr
{
    public ConsoleKind Which;
    public BoundExpr? Argument; // null => WriteLine() blank line
}

// ---- Bound statements ------------------------------------------------------
public abstract class BoundStmt
{
}

public sealed class BoundBlock : BoundStmt
{
    public List<BoundStmt> Statements = [];
    public List<LocalSymbol> Locals = []; // locals declared directly in this block
}

public sealed class BoundLocalDecl : BoundStmt
{
    public required LocalSymbol Symbol;
    public BoundExpr? Initializer;
}

public sealed class BoundExprStmt : BoundStmt
{
    public required BoundExpr Expression;
}

public sealed class BoundIf : BoundStmt
{
    public required BoundExpr Condition;
    public required BoundStmt Then;
    public BoundStmt? Else;
}

public sealed class BoundWhile : BoundStmt
{
    public required BoundExpr Condition;
    public required BoundStmt Body;
}

public sealed class BoundFor : BoundStmt
{
    public BoundStmt? Init;
    public BoundExpr? Condition;
    public BoundExpr? Update;
    public required BoundStmt Body;
}

public sealed class BoundReturn : BoundStmt
{
    public BoundExpr? Value;
}

public sealed class BoundMethodBody
{
    public required MethodSymbol Method;
    public required BoundBlock Body;
    public List<LocalSymbol> AllLocals = [];
}
