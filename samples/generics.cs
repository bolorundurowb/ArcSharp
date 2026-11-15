using System;

class Program
{
    static int Prefer(int value)
    {
        return value + 1;
    }

    static T Prefer<T>(T value)
    {
        return value;
    }

    static T Identity<T>(T value)
    {
        return value;
    }

    static T Construct<T>() where T : new()
    {
        return new T();
    }

    static T First<T>(T[] values)
    {
        return values[0];
    }

    static T[] ArrayOf<T>(T value)
    {
        T[] result = new T[1];
        result[0] = value;
        return result;
    }

    static T Unbox<T>(Box<T> box)
    {
        return box.Get();
    }

    static T Read<T>(IValue<T> value)
    {
        return value.Get();
    }

    static T ReadConstrained<T, U>(U value) where U : class, IValue<T>
    {
        return value.Get();
    }

    static T Recurse<T>(T value, int depth)
    {
        if (depth == 0)
        {
            return value;
        }
        return Recurse<T>(value, depth - 1);
    }

    static void Replace<T>(ref T slot, T value)
    {
        slot = value;
    }

    static void Main()
    {
        Box<int> number = new Box<int>(42);
        Box<string> text = new Box<string>("hello");
        Box<Box<string>> nested = new Box<Box<string>>(text);
        Derived<int> derived = new Derived<int>(7);
        IValue<int> value = derived;
        var factory = new Factory<Tag>();
        var link = new Link<int>();
        link.Value = 17;
        var tag = factory.Create();
        tag.Text = "made";
        int[] numbers = new int[1];
        numbers[0] = 8;
        var boxes = new Box<string>[1];
        boxes[0] = text;
        Box<int[]> arrayBox = new Box<int[]>(numbers);
        WeakReference<Tag> observer = new WeakReference<Tag>(tag);
        var weakBox = new Box<WeakReference<Tag>>(observer);
        var arrays = Identity<int[]>(arrayBox.Get());
        var words = ArrayOf<string>("array");
        string replacement = "before";
        Replace<int>(ref numbers[0], 11);
        replacement = "after";
        Console.WriteLine("generic=" + Identity(number.Get()) + "," + Identity<string>(nested.Get().Get()));
        Console.WriteLine("dispatch=" + Read<int>(value) + "," + ReadConstrained<int, Derived<int>>(derived));
        Console.WriteLine("infer=" + First(numbers) + "," + Unbox(text) + "," + text.Echo<int>(9));
        Console.WriteLine("owners=" + Recurse<string>(replacement, 3) + "," + boxes[0].Get() + "," + tag.Text);
        Console.WriteLine("counts=" + Box<int>.Count + "," + Box<string>.Count);
        Console.WriteLine("arrays=" + arrays[0] + "," + words[0]);
        bool weakOk = false;
        if (weakBox.Get().TryGetTarget(out var target))
        {
            Console.WriteLine("weak=" + target.Text);
            weakOk = target.Text.Length == 4;
        }
        Console.WriteLine("types=" + (derived is Box<int>) + "," + (value as Box<string> == null));
        bool numberOk = Identity(number.Get()) == 42;
        bool nestedOk = nested.Get().Get() == text.Get();
        bool dispatchOk = Read<int>(value) == 7;
        bool constraintOk = ReadConstrained<int, Derived<int>>(derived) == 7;
        bool firstOk = First(numbers) == 11;
        bool unboxOk = Unbox(text) == text.Get();
        bool echoOk = text.Echo<int>(9) == 9;
        bool arrayOk = arrays[0] == 11;
        bool wordOk = words[0].Length == 5;
        bool linkValueOk = link.Value == 17;
        bool linkNextOk = link.Next == null;
        bool isOk = derived is Box<int>;
        bool asOk = value as Box<string> == null;
        if (numberOk && nestedOk && dispatchOk && constraintOk && firstOk && unboxOk
            && echoOk && arrayOk && wordOk && weakOk && isOk && asOk
            && Box<int>.Count == 2 && Box<string>.Count == 1 && Construct<int>() == 0
            && Prefer(3) == 4 && Prefer<int>(3) == 3 && linkValueOk && linkNextOk)
        {
            Console.WriteLine("generics PASS");
        }
    }
}
