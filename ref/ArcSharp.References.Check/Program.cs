using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: ArcSharp.References.Check <ArcSharp.References.dll>");
    if (args.Length == 1)
    {
        Console.Error.WriteLine($"not found: {args[0]}");
    }

    return 2;
}

var corlib = MetadataReference.CreateFromFile(Path.GetFullPath(args[0]));
var parse = new CSharpParseOptions(LanguageVersion.CSharp12);
var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
var failed = 0;

foreach (var check in Cases())
{
    var tree = CSharpSyntaxTree.ParseText(check.Source, parse);
    var compilation = CSharpCompilation.Create("surface", [tree], [corlib], options);
    using var pe = new MemoryStream();
    var emit = compilation.Emit(pe);
    var errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    var ok = check.ErrorId == null
        ? errors.Length == 0
        : errors.Any(d => d.Id == check.ErrorId);
    if (ok)
    {
        continue;
    }

    failed++;
    Console.Error.WriteLine(check.ErrorId == null
        ? $"FAIL {check.Name}"
        : $"FAIL {check.Name} (expected {check.ErrorId})");
    if (errors.Length == 0)
    {
        Console.Error.WriteLine("  compiled with no errors");
    }

    foreach (var error in errors)
    {
        Console.Error.WriteLine($"  {error.Id}: {error.GetMessage()}");
    }
}

if (failed != 0)
{
    Console.Error.WriteLine($"{failed} surface check(s) failed");
    return 1;
}

Console.WriteLine($"surface checks passed ({Cases().Length})");
return 0;

static SurfaceCase[] Cases() =>
[
    new("parameter array", """
        class P
        {
            static void Collect(params int[] values)
            {
            }

            static void Call()
            {
                Collect(1, 2);
            }
        }
        """),
    new("array foreach", """
        class P
        {
            static void M(int[] values)
            {
                foreach (long value in values)
                {
                    System.Console.WriteLine(value);
                }
            }
        }
        """),
    new("console", """
        using System;
        class P
        {
            static void M()
            {
                Console.Write();
                Console.Write("s");
                Console.Write(true);
                Console.Write('a');
                Console.Write((byte)1);
                Console.Write((sbyte)1);
                Console.Write((short)1);
                Console.Write((ushort)1);
                Console.Write(1);
                Console.Write(1u);
                Console.Write(1L);
                Console.Write(1UL);
                Console.Write(1.5f);
                Console.Write(1.5);
                Console.WriteLine();
                Console.WriteLine("s");
                Console.WriteLine(true);
                Console.WriteLine('a');
                Console.WriteLine((byte)1);
                Console.WriteLine((sbyte)1);
                Console.WriteLine((short)1);
                Console.WriteLine((ushort)1);
                Console.WriteLine(1);
                Console.WriteLine(1u);
                Console.WriteLine(1L);
                Console.WriteLine(1UL);
                Console.WriteLine(1.5f);
                Console.WriteLine(1.5);
            }
        }
        """),
    new("lengths", """
        class P
        {
            static void M()
            {
                string s = "hi";
                int n = s.Length;
                int[] a = new int[n];
                int m = a.Length;
                a[0] = 1;
                object[] objects = new object[1];
                string[] words = new string[1];
                int count = objects.Length + words.Length;
            }
        }
        """),
    new("weak-reference", """
        using System;
        class P
        {
            static void M()
            {
                var weak = new WeakReference<string>("hi");
                string target;
                bool alive = weak.TryGetTarget(out target);
                if (alive)
                {
                    weak.SetTarget(target);
                }
            }
        }
        """),
    new("using-interface", """
        using System;
        class R : IDisposable
        {
            public void Dispose()
            {
            }
        }
        class P
        {
            static void M()
            {
                using (R resource = new R())
                {
                }
            }
        }
        """),
    new("concat", """
        using System;
        class P
        {
            static void M()
            {
                string name = "n";
                int item = 1;
                string child = "c";
                int slot = 2;
                int counter = 3;
                int local = 4;
                Console.WriteLine("owners=" + name + "," + item + "," + child + "," + slot + "," + counter + "," + local);
                Console.Write("concat=" + 'B' + " direct=");
                Console.Write("cmp=" + (1 == 1) + "/" + (1 != 2) + " direct=");
                long big = 20000000000L;
                uint wide = 3000000000u;
                Console.WriteLine("concat=" + big + " uint=" + wide);
                string joined = string.Concat(name, child);
            }
        }
        """),
    new("classes", """
        interface IShape
        {
            int Area();
        }
        class Shape : IShape
        {
            public int w;
            public Shape(int w) { this.w = w; }
            public virtual int Area() { return w; }
        }
        class Square : Shape
        {
            public Square(int s) : base(s) { }
            public override int Area() { return w * w; }
        }
        class P
        {
            static void M()
            {
                object value = new Square(3);
                string text = value as string;
                bool match = value is Square;
            }
        }
        """),
    new("system-console", """
        class P
        {
            static void M()
            {
                System.Console.WriteLine("hi");
                System.Console.Write(1);
                System.String text = "hi";
                System.Int32 n = text.Length;
                System.Object value = text;
                var weak = new System.WeakReference<System.String>(text);
                System.String target;
                weak.TryGetTarget(out target);
                weak.SetTarget(target);
            }
        }
        class R : System.IDisposable
        {
            public void Dispose()
            {
            }
        }
        """),
    new("to-string-visible", """
        class P
        {
            static void M()
            {
                string s = "hi";
                string t = s.ToString();
            }
        }
        """),
    new("string-index", """
        class P
        {
            static void M()
            {
                string s = "hi";
                char c = s[0];
            }
        }
        """, "CS0021"),
    new("nullable-value", """
        class P
        {
            static void M()
            {
                int? n = null;
            }
        }
        """, "CS0518"),
    new("math", """
        class P
        {
            static void M()
            {
                int n = Math.Abs(1);
            }
        }
        """, "CS0103"),
    new("attribute-binds", """
        using System;
        [AttributeUsage(AttributeTargets.Class)]
        class Mine : Attribute
        {
        }
        [Mine]
        class P
        {
        }
        """),
    new("enum-binds", """
        enum E
        {
            A
        }
        class P
        {
            static void M()
            {
                E value = E.A;
            }
        }
        """),
    new("new-string", """
        class P
        {
            static void M()
            {
                string s = new string();
            }
        }
        """, "CS1729"),
    new("print-class", """
        using System;
        class Box
        {
        }
        class P
        {
            static void M()
            {
                Console.WriteLine(new Box());
            }
        }
        """, "CS1503"),
    new("weak-value", """
        using System;
        class P
        {
            static void M()
            {
                var weak = new WeakReference<int>(1);
            }
        }
        """, "CS0452"),
    new("weak-resurrection", """
        using System;
        class P
        {
            static void M()
            {
                var weak = new WeakReference<string>("hi", false);
            }
        }
        """, "CS1729"),
    new("finaliser", """
        using System;
        class Base
        {
            ~Base()
            {
            }
        }
        class Derived : Base, IDisposable
        {
            ~Derived()
            {
                Console.WriteLine("derived");
            }
            public void Dispose()
            {
                GC.SuppressFinalize(this);
            }
        }
        class P
        {
            static void M()
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.ReRegisterForFinalize(new Derived());
            }
        }
        """),
    new("gc-keep-alive", """
        class P
        {
            static void M()
            {
                System.GC.KeepAlive(new P());
            }
        }
        """, "CS0117"),
    new("structural-dispose", """
        class R
        {
            public void Dispose()
            {
            }
        }
        class P
        {
            static void M()
            {
                using (R resource = new R())
                {
                }
            }
        }
        """, "CS1674"),
];

sealed class SurfaceCase
{
    public SurfaceCase(string name, string source, string? errorId = null)
    {
        Name = name;
        Source = source;
        ErrorId = errorId;
    }

    public string Name { get; }

    public string Source { get; }

    public string? ErrorId { get; }
}
