using ArcSharp.Syntax;

namespace ArcSharp.Binding;

public enum TypeKind
{
    Primitive,
    String,
    Class,
    Struct,
    Interface,
    Array,
    Void,
    Error,
    WeakRef
}

public sealed class TypeSymbol
{
    public required string Name;
    public TypeKind Kind;

    // class/struct
    public TypeSymbol? BaseType;
    public List<TypeSymbol> Interfaces = [];
    public List<FieldSymbol> InstanceFields = []; // includes inherited, in layout order
    public List<FieldSymbol> StaticFields = [];
    public List<MethodSymbol> Methods = []; // declared in this type (incl ctors)
    public List<PropertySymbol> Properties = []; // declared in this type
    public List<MethodSymbol> Vtable = []; // virtual slot order (incl inherited)
    public Dictionary<int, MethodSymbol> InterfaceImpl = []; // selector -> implementing method
    public ClassDecl? ClassSyntax;
    public StructDecl? StructSyntax;
    public InterfaceDecl? InterfaceSyntax;
    public bool LayoutDone;

    // array
    public TypeSymbol? ElementType;

    public bool IsReferenceType =>
        Kind is TypeKind.Class or TypeKind.String or TypeKind.Array or TypeKind.Interface or TypeKind.WeakRef;

    public string LlvmType => Kind switch
    {
        TypeKind.Primitive => Name switch
        {
            "int" => "i32",
            "long" => "i64",
            "byte" => "i8",
            "sbyte" => "i8",
            "short" => "i16",
            "ushort" => "i16",
            "uint" => "i32",
            "ulong" => "i64",
            "char" => "i32",
            "bool" => "i1",
            "float" => "float",
            "double" => "double",
            _ => "i32"
        },
        TypeKind.Void => "void",
        TypeKind.Struct => "%struct." + Name,
        _ => "i8*" // all reference types are opaque pointers
    };

    public bool IsUnsignedInteger => Kind == TypeKind.Primitive && Name is "byte" or "ushort" or "uint" or "ulong";
    public bool IsSignedInteger => Kind == TypeKind.Primitive && Name is "sbyte" or "short" or "int" or "long";
    public bool IsInteger => IsSignedInteger || IsUnsignedInteger || Name == "char";
    public bool IsFloat => Kind == TypeKind.Primitive && Name is "float" or "double";

    // size of one storage slot for this type, in bytes (uniform 8-byte model)
    public int SlotSize => Kind == TypeKind.Struct ? InstanceFields.Count * 8 : 8;

    public bool IsSubclassOf(TypeSymbol other)
    {
        var t = BaseType;
        while (t != null)
        {
            if (t == other)
            {
                return true;
            }

            t = t.BaseType;
        }

        return false;
    }

    public bool Implements(TypeSymbol iface)
    {
        var t = this;
        while (t != null)
        {
            if (t.Interfaces.Contains(iface))
            {
                return true;
            }

            t = t.BaseType;
        }

        return false;
    }

    public override string ToString() => Name;
}

public sealed class FieldSymbol
{
    public required string Name;
    public required TypeSymbol Type;
    public required TypeSymbol Owner;
    public bool IsStatic;
    public bool IsWeak;
    public int Index; // slot index among instance fields (0-based)

    public string MangledStatic => $"@{Owner.Name}__sf__{Name}";

    // byte offset of this field within an instance (after the 24-byte header)
    public int ByteOffset => 24 + Index * 8;
}

public sealed class ParamSymbol
{
    public required string Name;
    public required TypeSymbol Type;
    public RefKind RefKind;
    public int Index;
}

public sealed class LocalSymbol
{
    public required string Name;
    public required TypeSymbol Type;
    public int Id; // unique within a method
}

public sealed class PropertySymbol
{
    public required string Name;
    public required TypeSymbol Type;
    public required TypeSymbol Owner;
    public bool IsStatic;
    public bool HasGetter;
    public bool HasSetter;
    public FieldSymbol? BackingField;
    public MethodSymbol? Getter;
    public MethodSymbol? Setter;
}

public sealed class MethodSymbol
{
    public required string Name;
    public required TypeSymbol Owner;
    public TypeSymbol ReturnType = null!;
    public List<ParamSymbol> Parameters = [];
    public bool IsStatic;
    public bool IsVirtual;
    public bool IsOverride;
    public bool IsAbstract;
    public bool IsConstructor;
    public int VtableSlot = -1;
    public MethodDecl? Syntax;

    public string MangledName
    {
        get
        {
            if (IsConstructor)
            {
                return $"{Owner.Name}__ctor__{Parameters.Count}";
            }

            return $"{Owner.Name}__{Name}__{Parameters.Count}";
        }
    }
}
