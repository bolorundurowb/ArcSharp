using ArcSharp.Lexing;
using ArcSharp.Syntax;

namespace ArcSharp.Binding;

// ---- Bound expressions -----------------------------------------------------
public abstract class BoundExpr
{
    public required TypeSymbol Type;
}

public sealed class BoundLiteral : BoundExpr
{
    public string Site = "";
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
    public bool ByRef;
}

public sealed class BoundThis : BoundExpr
{
}

public sealed class BoundByRefArg : BoundExpr
{
    public required BoundExpr Lvalue;
    public required RefKind Kind;
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
    public bool Interface;
    public bool ReturnsAssignedValue; // interface dispatch
    public int Selector = -1; // global interface selector when Interface

    // Receiver is one node of a delegate's invocation list, and Method is the
    // delegate type's Invoke. The callee is the node's function, and its first
    // argument is the node's target.
    public bool ThroughDelegate;
}

// The address of a method, as the 64-bit function value a delegate node holds.
public sealed class BoundFunctionAddress : BoundExpr
{
    public required MethodSymbol Method;
}

public sealed class BoundNewObject : BoundExpr
{
    public bool CapturesArguments { get; init; }
    public string Site = "";
    public MethodSymbol? Ctor;
    public List<BoundExpr> Arguments = [];
}

public sealed class BoundNewArray : BoundExpr
{
    public string Site = "";
    public required TypeSymbol ElementType;
    public required BoundExpr Size;
    public List<BoundExpr> Initializers = [];
}

public sealed class BoundNamedArgument : BoundExpr
{
    public required int Ordinal;
    public required BoundExpr Value;
}

public sealed class BoundArraySlice : BoundExpr
{
    public required BoundExpr Receiver;
    public required BoundExpr Start;
    public required BoundExpr Length;
    public required TypeSymbol ElementType;
    public string Site = "";
}

public sealed class BoundIndex : BoundExpr
{
    public required BoundExpr Receiver;
    public required BoundExpr Index;
}

public sealed class BoundWeakRefNew : BoundExpr
{
    public string Site = "";
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
    BoolArith,
    BoolCmp,
    StrConcat,
    StrEq,
    DelegateCombine,
    DelegateRemove,
    DelegateEq
}

public sealed class BoundBinary : BoundExpr
{
    public TokenKind Op;
    public BinKind Kind;
    public bool IsUnsigned;
    public bool IsChecked;
    public required BoundExpr Left;
    public required BoundExpr Right;
}

public sealed class BoundUnary : BoundExpr
{
    public TokenKind Op;
    public bool IsChecked;
    public required BoundExpr Operand;
}

public sealed class BoundOperatorUpdate : BoundExpr
{
    public required BoundExpr Target;
    public required BoundPatternInput Input;
    public required BoundExpr Result;
    public bool IsPostfix;
}

// Evaluates the effects in order and discards their values, then yields Value.
public sealed class BoundSequence : BoundExpr
{
    public required List<BoundExpr> Effects;
    public required BoundExpr Value;
}

public sealed class BoundStringCompound : BoundExpr
{
    public required BoundExpr Target;
    public required BoundExpr Right;
}

// 'target += handler' or 'target -= handler' on a delegate. The target is
// addressed once, and its slot takes the new list.
public sealed class BoundDelegateCompound : BoundExpr
{
    public required BoundExpr Target;
    public required BoundExpr Right;
    public bool IsRemove;
}

public sealed class BoundNumericCompound : BoundExpr
{
    public required BoundExpr Target;
    public required BoundExpr Right;
    public required TypeSymbol OperationType;
    public required TokenKind Op;
    public bool IsChecked;
}

public sealed class BoundIncrement : BoundExpr
{
    public required BoundExpr Target;
    public bool IsDecrement;
    public bool IsPostfix;
    public bool IsChecked;
}

public sealed class BoundAssign : BoundExpr
{
    public required BoundExpr Target; // BoundLocal/BoundParam/BoundFieldAccess/BoundIndex
    public required BoundExpr Value;
}

public sealed class BoundConversion : BoundExpr
{
    public required BoundExpr Operand; // e.g. int -> long, or reference upcast/downcast

    // An explicit reference cast to a class. The emitter tests the runtime type
    // and raises InvalidCastException when the operand is not an instance.
    public bool CastCheck;
    public bool IsChecked;
}

public sealed class BoundChoice : BoundExpr
{
    public required BoundExpr Condition;
    public required BoundExpr WhenTrue;
    public required BoundExpr WhenFalse;
}

public sealed class BoundSwitchFailure : BoundExpr
{
    public required BoundExpr Exception;
}

public sealed class BoundPatternInput : BoundExpr
{
}

public sealed class BoundPatternTest : BoundExpr
{
    public required BoundExpr Value;
    public required BoundPatternInput Input;
    public required BoundExpr Test;
}

public sealed class BoundPatternBinding : BoundExpr
{
    public required BoundExpr Test;
    public required BoundExpr Value;
    public required LocalSymbol Local;
}

public sealed class BoundUnbox : BoundExpr
{
    public required BoundExpr Operand;
    public required FieldSymbol BoxedValue;
}

public sealed class BoundIs : BoundExpr
{
    public required BoundExpr Operand;
    public required TypeSymbol TestType;
}

// True when two non-null objects have exactly the same runtime type. A record's
// generated equality uses it in place of EqualityContract.
public sealed class BoundSameType : BoundExpr
{
    public required BoundExpr Left;
    public required BoundExpr Right;
}

public enum DecimalOp
{
    Constant,
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    Negate,
    Compare,
    FromInteger,
    FromUnsigned,
    FromDouble,
    ToInteger,
    ToUnsigned,
    ToDouble,
    ToText
}

// One decimal operation, done by the runtime on 128-bit values. Constant holds
// the two slots of the value in Low and High. ToInteger holds the smallest and
// largest result the target type allows in Low and High.
public sealed class BoundDecimalOp : BoundExpr
{
    public DecimalOp Op;
    public List<BoundExpr> Arguments = [];
    public long Low;
    public long High;
}

public sealed class BoundAs : BoundExpr
{
    public required BoundExpr Operand;
    public required TypeSymbol TestType;
}

public sealed class BoundLength : BoundExpr      // array.Length or string.Length
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

public enum GcKind
{
    SuppressFinalize,
    Collect,
    WaitForPendingFinalizers
}

// Collect and WaitForPendingFinalizers are no-ops that survive lowering so the
// emitter handles every node explicitly; only SuppressFinalize has an Argument.
public sealed class BoundGcCall : BoundExpr
{
    public GcKind Which;
    public BoundExpr? Argument;
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
    public required bool ConditionIsTop;
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

public sealed class BoundSwitch : BoundStmt
{
    public required BoundExpr Value;
    public required BoundPatternInput Input;
    public List<BoundSwitchSection> Sections = [];
}

public sealed class BoundSwitchSection
{
    public bool IsDefault;
    public List<BoundExpr> Tests = [];
    public required BoundBlock Body;
}

public sealed class BoundBranch : BoundStmt
{
    public required bool IsContinue;
}

public sealed class BoundReturn : BoundStmt
{
    public BoundExpr? Value;
}

// 'using' statement: the resource local holds the owned (+1) resource for the
// body, and is disposed and released at scope exit. Dispose is null when the
// resource has no parameterless 'void Dispose()' method (an error was already
// reported, or the resource is always null).
public sealed class BoundUsing : BoundStmt
{
    public required LocalSymbol Resource;
    public BoundExpr? ResourceInit;
    public required BoundStmt Body;
    public MethodSymbol? Dispose;
    public bool DisposeInterface;
    public bool DisposeVirtual;
    public int DisposeSelector = -1;
}

// 'throw value;' or, when Value is null, 'throw;' inside a catch block.
public sealed class BoundThrow : BoundStmt
{
    public BoundExpr? Value;
}

// One catch clause. ExceptionType is null for a clause that catches everything.
public sealed class BoundCatch
{
    public TypeSymbol? ExceptionType;
    public LocalSymbol? Variable;
    public required BoundBlock Body;
}

// try/catch/finally. Catches and Finally are each optional, but not both empty.
public sealed class BoundTry : BoundStmt
{
    public required BoundBlock Body;
    public List<BoundCatch> Catches = [];
    public BoundBlock? Finally;
}

public sealed class BoundMethodBody
{
    public required MethodSymbol Method;
    public required BoundBlock Body;
    public List<LocalSymbol> AllLocals = [];
}

public sealed class BoundProgram
{
    public List<TypeSymbol> Types = [];
    public List<BoundMethodBody> MethodBodies = [];
    public int InterfaceSelectorCount;
    public MethodSymbol? Entry;
    public List<Diagnostic> Diagnostics = [];
}
