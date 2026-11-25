using ArcSharp.Binding;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace ArcSharp.Frontend;

sealed partial class RoslynLowering
{
    private const string UnsupportedEvent = "only non-virtual field-like events on classes are supported";

    private static bool HasEventStorage(IEventSymbol symbol) =>
        symbol is { IsAbstract: false, IsVirtual: false, IsOverride: false, AddMethod: { IsExtern: false }, RemoveMethod: { IsExtern: false } }
        && symbol.ContainingType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class;

    private void CreateEvent(TypeSymbol owner, IEventSymbol symbol, List<FieldSymbol> fields, List<MethodSymbol> methods)
    {
        if (!HasEventStorage(symbol)
            || symbol.AddMethod is not { } add || symbol.RemoveMethod is not { } remove
            || symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                is not VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax } declaration)
        {
            return;
        }

        var field = new FieldSymbol
        {
            Name = $"<{symbol.Name}>k__BackingField",
            Type = MapType(symbol.Type, symbol.Locations.FirstOrDefault()),
            Owner = owner,
            IsStatic = symbol.IsStatic
        };
        fields.Add(field);
        _fields[new MemberKey(symbol.OriginalDefinition, owner)] = field;
        if (declaration.Initializer is { } initialiser)
        {
            RegisterInitialiser(field, initialiser);
        }

        AddAccessor(add, isRemove: false);
        AddAccessor(remove, isRemove: true);

        void AddAccessor(IMethodSymbol accessor, bool isRemove)
        {
            var value = new ParamSymbol { Name = "value", Type = field.Type, Index = 0 };
            var method = Accessor(owner, accessor, accessor.Name, _void, [value]);
            methods.Add(method);
            _readyBodies.Add(new BoundMethodBody
            {
                Method = method,
                Body = new BoundBlock
                {
                    Statements =
                    [
                        new BoundExprStmt
                        {
                            Expression = new BoundDelegateCompound
                            {
                                Target = EventField(field, field.IsStatic ? null : new BoundThis { Type = owner }),
                                Right = new BoundParam { Symbol = value, Type = field.Type },
                                IsRemove = isRemove,
                                Type = field.Type
                            }
                        }
                    ]
                }
            });
        }
    }

    private BoundExpr LowerEventReference(IEventReferenceOperation operation)
    {
        var owner = MapType(operation.Event.ContainingType, operation.Syntax.GetLocation());
        if (!_fields.TryGetValue(new MemberKey(operation.Event.OriginalDefinition, owner), out var field))
        {
            return UnsupportedExpr(operation);
        }

        return EventField(field, EventReceiver(operation));
    }

    private BoundExpr LowerEventAssignment(IEventAssignmentOperation operation)
    {
        if (operation.EventReference is not IEventReferenceOperation reference)
        {
            return UnsupportedExpr(operation);
        }

        var accessor = operation.Adds ? reference.Event.AddMethod : reference.Event.RemoveMethod;
        if (accessor == null || MapMethod(accessor, operation.Syntax.GetLocation()) is not { } method)
        {
            return UnsupportedExpr(operation);
        }

        return BindMemberCall(EventReceiver(reference), method, [LowerExpr(operation.HandlerValue)]);
    }

    private BoundExpr? EventReceiver(IEventReferenceOperation operation) =>
        operation.Event.IsStatic ? null : operation.Instance == null ? SourceReceiver() : LowerExpr(operation.Instance);

    private static BoundFieldAccess EventField(FieldSymbol field, BoundExpr? receiver) =>
        new() { Field = field, Type = field.Type, Receiver = receiver };
}
