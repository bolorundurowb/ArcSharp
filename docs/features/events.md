# Events

Classes can declare non-virtual field-like events, including static events.
Use `+=` to subscribe and `-=` to remove a matching handler. Inside the
declaring class, code can invoke the event, read it as a delegate or assign it.
Roslyn enforces C# access rules.

```csharp
class Source
{
    public event Action Changed;

    public void Raise()
    {
        Changed?.Invoke();
    }
}

class Program
{
    static void OnChanged()
    {
        Console.WriteLine("changed");
    }

    static void Main()
    {
        var source = new Source();
        source.Changed += OnChanged;
        source.Raise();
        source.Changed -= OnChanged;
        source.Raise();
    }
}
```

This prints `changed` once. Custom accessors, virtual, abstract, override and
extern events, and events on interfaces or structs report `ARC3001`.

## Lowering and ownership

Each event has an ordinary strong delegate backing field. Initialisers use the
normal field initialisation path. Synthesised add and remove methods reuse
delegate combine and remove. The caller evaluates the receiver and the handler
expression before the accessor reads the current handler list. This preserves
side effects when a handler factory changes the same event.

The backing field keeps each subscribed handler alive. Removing a handler or
clearing the event releases the old list. Instance teardown and static field
cleanup use the ordinary ARC paths. When a subscribed lambda captures the
publisher, the compiler and analyser can report `ARC3100` through the delegate
capture graph. The subscription remains legal and the cycle leaks if left
intact. The analysis works at the type level and cannot follow method-group
targets.

## Verification

The harness checks subscription order, removal, event initialisation,
single evaluation of the receiver, handler expression side effects and static
event storage in `events`. It checks the intentional leak and warning in
`event_cycle` and rejection of custom accessors in `event_accessors`.
