namespace ArcSharp.Syntax;

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

public enum RefKind
{
    None,
    Ref,
    Out,
    In
}
