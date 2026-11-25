# Delegates

ArcSharp supports delegate types, single and multicast. A delegate is an
ordinary heap object under ARC, so it keeps its target alive and releases it
when the delegate itself dies.

A delegate can hold a method group or a [lambda](lambdas.md). Field-like events on classes use the same delegate representation. See [Events](events.md).

This program compiles and runs with the current compiler. It prints
`second a`, then `ab`:

```csharp
delegate void Step(string name);

class Log
{
    public string Text = "";

    public void Add(string name)
    {
        Text = Text + name;
    }
}

class Program
{
    static void Second(string name)
    {
        System.Console.WriteLine("second " + name);
    }

    static void Main()
    {
        var log = new Log();
        Step step = log.Add;
        step += Second;        // combine
        step("a");             // log gets "a", then Second prints
        step -= Second;        // remove
        step("b");             // only log.Add runs
        Step none = null;
        none?.Invoke("c");     // skipped
        System.Console.WriteLine(log.Text);
    }
}
```

## What works

- `delegate` declarations, including generic ones.
- A method group converted to a delegate: `D d = obj.Method;`,
  `D d = Type.Method;` and `new D(obj.Method)`. The method can be static,
  instance, virtual, or reached through an interface.
- `ref` and `out` parameters, and struct and class parameters and results. See
  the `delegates_targets` sample.
- Lambdas, with captured variables. See [Lambdas and captures](lambdas.md).
- Calling a delegate with `d(args)`.
- `+`, `-`, `+=` and `-=` on delegates of the same type.
- `==` and `!=` between two delegates, or a delegate and `null`.
- `?.Invoke(...)`, which skips the call when the delegate is `null`.
- `System.Action` and `System.Func` with up to four parameters, and
  `System.Predicate<T>`. They are declared in `corelib/Delegates.cs` as ordinary
  generic delegate types.

## Design

### Representation

A delegate is one object per handler. The handlers of a multicast delegate form
an immutable linked list.

| Slot | Content |
|---|---|
| `$target` | The receiver object, held strongly. `null` for a static method. For a lambda, the closure. |
| `$function` | The address of a generated function, as a 64-bit integer. |
| `$next` | The next handler, held strongly, or `null`. |

The object is 48 bytes: the 24-byte header and three 8-byte slots.

Combining and removing never change an existing list. `arc_delegate_combine`
copies the nodes of the first list in front of the second list, which the new
list shares. `arc_delegate_remove` copies the nodes before the removed run, and
the new list shares the nodes after it. Both functions are in
`runtime/arc_runtime.c`. This matches .NET, where delegates are immutable. It
also means a handler that removes itself from the field does not disturb the
call that is already running.

Removal takes out the last matching run, and does nothing when no handler
matches. Removing every handler gives `null`.

### One function shape

Every handler is called as `function(target, arguments...)`. A static method,
an instance method, a virtual method and an interface method are all reached
through a small generated static thunk with that shape, one for each method and
delegate type. The thunk casts `target` to the method's owner and makes the
call with the ordinary call lowering, so virtual and interface dispatch happen
inside the thunk.

A lambda uses the same shape. Its target is the closure, and its thunk calls the
closure's body method.

This keeps `Invoke` simple. The thunks and `Invoke` are built as bound trees, so
they go through the same ARC lowering as ordinary code.

### Invoke

The compiler generates `Invoke` for each delegate type as a loop over the list.
It calls each handler through `BoundCall.ThroughDelegate`, which loads the
function and the target from the node. The result of the last handler is the
result of the call.

An exception from a handler leaves the loop at once. The handlers after it are
not called, as in .NET. The `delegates_lifetime` sample checks this.

Calling a null delegate raises `NullReferenceException`, which a `try` block can
catch. `?.Invoke` skips the call instead. The `delegates_multicast` sample checks
both.

### Equality

Two delegates are equal when their lists have the same length and every
position has the same target and function.

## Decisions

- **The target is held strongly.** The delegate owns its receiver, so
  `Handler h = obj.Method;` keeps `obj` alive until `h` dies. The finaliser
  of `obj` runs at the delegate's last release, not at the end of the scope that
  named `obj`. This is the same rule as any other strong field.
- **Immutable lists, with the list operations in C.** The allocation and copying
  needed for combine and remove are simpler to get right once in the runtime than
  as IR for each delegate type. The nodes are allocated with `arc_alloc`, so they
  have ordinary headers and ARC accounting.
- **Thunks instead of dispatch in the delegate.** A thunk costs one extra call,
  but a delegate to a virtual or interface method needs no dispatch logic of its
  own. A lambda gets the same benefit, so it needs no virtual `Invoke` slot.

## Limits

- **Custom event accessors and virtual events** report `ARC3001`. See [Events](events.md) for supported declarations.
- **Anonymous methods and `async` lambdas** report `ARC3001`.
  See [Lambdas and captures](lambdas.md#limits).
- **A delegate cannot bind to a library method**, such as `Console.WriteLine`.
  It reports `ARC3001`.
- **A delegate cannot bind to an instance method of a struct.** The struct would
  have to be boxed. It reports `ARC3001`.
- **A null instance is not checked when the delegate is created.** Unlike .NET,
  `D d = nullObject.Method;` does not throw there. No sample covers invoking
  such a delegate, so do not rely on the behaviour.
- **`ARC3100` cannot see a cycle through a method-group delegate.** The cycle
  graph does not follow the target of a method-group delegate, so no edge leads
  from the delegate to the object it holds. An object that stores a delegate to
  one of its own methods in a field leaks without a warning. Clear the delegate
  (`handler = null`) before the last reference to the object goes. A lambda's
  cycles are visible, because its closure is a typed class.
- **Other `System.Delegate` members are not available**, such as `Method`,
  `Target` and `DynamicInvoke`. Use `+` and `-` to combine and remove handlers.
- **`?.` with a value result is not supported**, such as `text?.Length`.

## How it is verified

| Sample | What it proves |
|---|---|
| `delegates_basic` | Method groups, explicit `new D(...)` creation, and static and instance targets. |
| `delegates_multicast` | Combine, remove, equality, the value of the last handler, and a null invoke. |
| `delegates_targets` | Virtual, interface, `ref`, `out`, struct, generic, `Func` and `Action` cases. |
| `delegates_lifetime` | The delegate owns its target, a handler that throws, delegates in fields and arrays, and `live=0`. |
| `delegates_lambdas` | Lambdas with captures, a lambda and a method group in one multicast delegate, removal by equality, and a null call. |
| `negative/delegate_struct_target` and `negative/delegate_library_method` | The two rejected bindings report `ARC3001`. |

The reference surface check, `ref/ArcSharp.References.Check`, covers the
`System.IntPtr`, `Delegate.Combine` and `Delegate.Remove` declarations that a
delegate needs. The analyser reports the same rejected bindings from an ordinary
`dotnet build`.
