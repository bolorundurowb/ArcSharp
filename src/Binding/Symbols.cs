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
    public bool LayoutInProgress;

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
        // A struct value is an opaque blob of one 8-byte slot per field, so
        // every field sits at a known 8*i byte offset without declaring an
        // LLVM struct type (whose packing LLVM would choose for us). The size
        // comes from the binder's layout because a nested struct field spans
        // more than one slot.
        TypeKind.Struct => $"[{HeapSize / 8} x i64]",
        _ => "i8*" // all reference types are opaque pointers
    };

    public bool IsUnsignedInteger => Kind == TypeKind.Primitive && Name is "byte" or "ushort" or "uint" or "ulong";
    public bool IsSignedInteger => Kind == TypeKind.Primitive && Name is "sbyte" or "short" or "int" or "long";
    public bool IsInteger => IsSignedInteger || IsUnsignedInteger || Name == "char";
    public bool IsFloat => Kind == TypeKind.Primitive && Name is "float" or "double";

    // size of one storage slot for this type, in bytes (uniform 8-byte model);
    // a struct value occupies as many slots as its laid-out size
    public int SlotSize => Kind == TypeKind.Struct ? HeapSize : 8;

    // total heap size of a class instance (24-byte header + field slots), or
    // the byte size of a struct value; computed by the binder's layout
    public int HeapSize;

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

    // byte offset within the owner, set by the binder's layout: 24 + running
    // total for class instances (after the header), 8 * i for struct values
    public int ByteOffset;
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

    public string SignatureId
    {
        get
        {
            if (Parameters.Count == 0)
            {
                return "0";
            }

            var parts = new string[Parameters.Count];
            for (var i = 0; i < Parameters.Count; i++)
            {
                parts[i] = ParamToken(Parameters[i]);
            }

            return Parameters.Count + "_" + string.Join("_", parts);
        }
    }

    // The parameter types are part of the name. Two methods that share a name
    // and an arity would otherwise be one LLVM symbol, and clang rejects the
    // redefinition.
    public string MangledName
    {
        get
        {
            var kind = IsConstructor ? "ctor" : Name;
            return LlvmNames.Ident($"{Owner.Name}__{kind}__{SignatureId}");
        }
    }

    private static string ParamToken(ParamSymbol p)
    {
        var prefix = p.RefKind switch
        {
            RefKind.Ref => "ref_",
            RefKind.Out => "out_",
            RefKind.In => "in_",
            _ => ""
        };
        return prefix + p.Type.Name;
    }
}

static class LlvmNames
{
    public static string Ident(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '$' or '-')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }

        return sb.ToString();
    }
}
