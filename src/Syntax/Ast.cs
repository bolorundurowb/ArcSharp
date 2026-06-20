using ArcSharp.Lexing;

namespace ArcSharp.Syntax;

public abstract class Node
{
    public int Line;
}

// ---- Type references -------------------------------------------------------
public sealed class TypeSyntax : Node
{
    public required string Name;
    public int ArrayRank; // 0 = not an array, 1 = T[]
    public List<TypeSyntax> TypeArgs = []; // generic type arguments, e.g. WeakReference<T>
    public bool Nullable; // trailing '?', ignored semantically for ref types
    public override string ToString() => Name + (ArrayRank > 0 ? "[]" : "");
}

// ---- Top level -------------------------------------------------------------
public sealed class CompilationUnit : Node
{
    public List<TypeDecl> Types = [];
}

public abstract class TypeDecl : Node
{
    public required string Name;
    public List<MemberDecl> Members = [];
}

public sealed class ClassDecl : TypeDecl
{
    public string? BaseTypeName;
    public List<string> InterfaceNames = [];
    public bool IsAbstract;
}

public sealed class StructDecl : TypeDecl
{
}

public sealed class InterfaceDecl : TypeDecl
{
}

// ---- Members ---------------------------------------------------------------
public abstract class MemberDecl : Node
{
    public bool IsStatic;
    public bool IsPublic = true;
}

public sealed class FieldDecl : MemberDecl
{
    public required TypeSyntax Type;
    public required string Name;
    public bool IsWeak;
}

public sealed class PropertyDecl : MemberDecl
{
    public required TypeSyntax Type;
    public required string Name;
    public bool HasGetter;
    public bool HasSetter;
}

public sealed class ParameterSyntax : Node
{
    public RefKind RefKind;
    public required TypeSyntax Type;
    public required string Name;
}

public sealed class MethodDecl : MemberDecl
{
    public TypeSyntax? ReturnType; // null for constructors
    public required string Name;
    public List<ParameterSyntax> Parameters = [];
    public BlockStmt? Body; // null for abstract/interface methods
    public bool IsVirtual;
    public bool IsOverride;
    public bool IsAbstract;

    public bool IsConstructor;

    // constructor initializer:  : base(args)  or  : this(args)
    public bool HasCtorInit;
    public bool CtorInitIsThis;
    public List<Expr> CtorInitArgs = [];
}

// ---- Statements ------------------------------------------------------------
public abstract class Stmt : Node
{
}

public sealed class BlockStmt : Stmt
{
    public List<Stmt> Statements = [];
}

public sealed class LocalDeclStmt : Stmt
{
    public required TypeSyntax Type; // may have Name == "var"
    public required string Name;
    public Expr? Initializer;
}

public sealed class ExprStmt : Stmt
{
    public required Expr Expression;
}

public sealed class IfStmt : Stmt
{
    public required Expr Condition;
    public required Stmt Then;
    public Stmt? Else;
}

public sealed class WhileStmt : Stmt
{
    public required Expr Condition;
    public required Stmt Body;
}

public sealed class ForStmt : Stmt
{
    public Stmt? Init;
    public Expr? Condition;
    public Expr? Update;
    public required Stmt Body;
}

public sealed class ReturnStmt : Stmt
{
    public Expr? Value;
}

// ---- Expressions -----------------------------------------------------------
public abstract class Expr : Node
{
}

public enum LiteralKind
{
    Int,
    Long,
    UInt,
    ULong,
    Float,
    Double,
    Bool,
    String,
    Null,
    Char
}

public sealed class LiteralExpr : Expr
{
    public LiteralKind Kind;
    public long IntValue;
    public bool BoolValue;
    public string StringValue = "";
    public double FloatValue; // used for both float and double literals
}

public sealed class NameExpr : Expr
{
    public required string Name;
}

public sealed class ThisExpr : Expr
{
}

public sealed class MemberAccessExpr : Expr
{
    public required Expr Target;
    public required string Name;
}

public sealed class InvocationExpr : Expr
{
    public required Expr Callee;
    public List<Expr> Arguments = [];
}

public sealed class NewObjectExpr : Expr
{
    public required string TypeName;
    public List<TypeSyntax> TypeArgs = [];
    public List<Expr> Arguments = [];
}

public enum RefKind { None, Ref, Out, In }

public sealed class ByRefArgExpr : Expr
{
    public required RefKind Kind;
    public bool IsDeclaration; // 'out var x' or 'out T x'
    public bool IsVar; // 'out var x'
    public TypeSyntax? DeclType; // for 'out T x'
    public string? Name; // declared name
    public Expr? Target; // for existing lvalue
}

public sealed class NewArrayExpr : Expr
{
    public required TypeSyntax ElementType;
    public required Expr Size;
}

public sealed class IndexExpr : Expr
{
    public required Expr Target;
    public required Expr Index;
}

public sealed class BinaryExpr : Expr
{
    public TokenKind Op;
    public required Expr Left;
    public required Expr Right;
}

public sealed class UnaryExpr : Expr
{
    public TokenKind Op;
    public required Expr Operand;
}

public sealed class AssignExpr : Expr
{
    public required Expr Target;
    public required Expr Value;
    public TokenKind Op = TokenKind.Assign;
}

public sealed class CastExpr : Expr
{
    public required TypeSyntax Type;
    public required Expr Operand;
}

public sealed class IsExpr : Expr
{
    public required Expr Operand;
    public required TypeSyntax TestType;
}

public sealed class AsExpr : Expr
{
    public required Expr Operand;
    public required TypeSyntax TestType;
}
