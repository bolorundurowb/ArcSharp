using ArcSharp.Diagnostics;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ArcSharp.Analyzers.Tests;

public sealed class UnsupportedConstructTests
{
    public static TheoryData<string, string> UnsupportedSources => new()
    {
        { "break", "class C { static void Main() { while (true) { break; } } }" },
        { "continue", "class C { static void Main() { while (true) { continue; } } }" },
        { "do", "class C { static void Main() { int i = 0; do { i = i + 1; } while (i < 3); } }" },
        { "foreach", "class C { static void Main() { int[] xs = new int[1]; foreach (int x in xs) { } } }" },
        { "switch", "class C { static void Main() { int x = 1; switch (x) { case 1: break; } } }" },
        { "try", "class C { static void Main() { try { } catch { } } }" },
        { "throw", "class C { static void Main() { throw new System.Exception(); } }" },
        { "lock", "class C { static void Main() { object o = new object(); lock (o) { } } }" },
        { "lambda", "class C { static void Main() { System.Func<int, int> f = x => x + 1; } }" },
        { "query", "class C { static void Main() { int[] xs = new int[1]; var q = from x in xs select x; } }" },
        { "pattern", "class C { static void Main() { object o = \"s\"; if (o is string s) { } } }" },
        { "switch expression", "class C { static void Main() { int x = 1; int y = x switch { 1 => 2, _ => 3 }; } }" },
        { "conditional access", "class C { static void Main() { string s = null; int n = s?.Length ?? 0; } }" },
        { "coalesce", "class C { static void Main() { string s = null; string t = s ?? \"x\"; } }" },
        { "interpolated string", "class C { static void Main() { System.Console.WriteLine($\"x\"); } }" },
        { "default", "class C { static void Main() { int x = default(int); } }" },
        { "checked", "class C { static void Main() { int x = checked(1 + 2); } }" },
        { "target-typed new", "class C { static void Main() { C c = new(); } }" },
        { "object initializer", "class C { static void Main() { C c = new C { }; } }" },
        { "attribute", "[System.Obsolete] class C { }" },
        { "enum", "enum E { A } class C { }" },
        { "delegate", "delegate void D(); class C { }" },
        { "indexer", "class C { public int this[int i] { get { return i; } } }" },
        { "record", "record R(int X); class C { }" },
        { "local function", "class C { static void Main() { void F() { } F(); } }" },
        { "expression body", "class C { static int One() => 1; }" },
        { "generic method", "class C { static void M<T>() { } }" },
        { "optional parameter", "class C { static void M(int x = 1) { } }" },
        { "params parameter", "class C { static void M(params int[] xs) { } }" },
        { "named argument", "class C { static void M(int x) { } static void Main() { M(x: 1); } }" },
        { "shift", "class C { static void Main() { int x = 1 << 2; } }" },
        { "using var", "class C { static void Main() { using var d = new System.IO.MemoryStream(); } }" },
        { "struct interface", "struct S : System.IDisposable { public void Dispose() { } } class C { }" },
        { "multi-dimensional array", "class C { static void Main() { int[,] xs = new int[1, 1]; } }" },
        { "sealed class", "sealed class C { }" },
        { "partial class", "partial class C { }" },
        { "static auto-property", "class C { public static int P { get; set; } }" },
        { "get-only property", "class C { public int P { get; } }" },
        { "top-level statements", "System.Console.WriteLine(\"x\");" },
        { "tuple", "class C { static void Main() { (int a, int b) t = (1, 2); } }" },
        { "base access", "class B { public int X; } class C : B { public int Get() { return base.X; } }" }
    };

    public static TheoryData<string, string> SupportedSources => new()
    {
        { "plain program", "class Program { static void Main() { System.Console.WriteLine(\"hi\"); } }" },
        {
            "inheritance and interfaces",
            "interface IShape { int Area(); } " +
            "class Shape { public virtual int Sides() { return 0; } } " +
            "class Square : Shape, IShape { public int Size; public override int Sides() { return 4; } public int Area() { return Size * Size; } } " +
            "class Program { static void Main() { Square s = new Square(); s.Size = 3; System.Console.WriteLine(s.Area()); } }"
        },
        {
            "auto-properties",
            "class Person { public string Name { get; set; } public int Age { get; set; } } " +
            "class Program { static void Main() { Person p = new Person(); p.Name = \"a\"; p.Age = 1; System.Console.WriteLine(p.Name); } }"
        },
        {
            "using statement",
            "class Resource : System.IDisposable { public void Dispose() { } } " +
            "class Program { static void Main() { using (Resource r = new Resource()) { System.Console.WriteLine(\"x\"); } } }"
        },
        {
            "structs with ref and out",
            "struct Point { public int x; public int y; public Point(int a, int b) { x = a; y = b; } public int Sum() { return x + y; } } " +
            "class Program { static void Fill(out Point p) { p = new Point(1, 2); } static void Bump(ref Point p) { p.x = p.x + 1; } " +
            "static void Main() { Point p; Fill(out p); Bump(ref p); System.Console.WriteLine(p.Sum()); } }"
        },
        {
            "WeakReference field",
            "class Node { public int v; } class Holder { public System.WeakReference<Node> w; } " +
            "class Program { static void Main() { Node n = new Node(); Holder h = new Holder(); h.w = new System.WeakReference<Node>(n); " +
            "if (h.w.TryGetTarget(out var got)) { System.Console.WriteLine(got.v); } } }"
        },
        {
            "arrays and loops",
            "class Program { static void Main() { int[] xs = new int[3]; " +
            "for (int i = 0; i < 3; i = i + 1) { xs[i] = i; } int sum = 0; " +
            "for (int i = 0; i < 3; i = i + 1) { sum = sum + xs[i]; } System.Console.WriteLine(sum); } }"
        },
        {
            "static field",
            "class Program { public static int Count; static void Main() { Count = 2; System.Console.WriteLine(Count); } }"
        },
        {
            "while and if",
            "class Program { static void Main() { int i = 0; while (i < 2) { i = i + 1; } " +
            "if (i == 2) { System.Console.WriteLine(\"ok\" + i); } else { System.Console.WriteLine(\"no\"); } } }"
        },
        {
            "struct field in class",
            "struct Pair { public int a; public int b; } class Holder { public Pair value; } " +
            "class Program { static void Main() { Holder h = new Holder(); h.value.a = 1; System.Console.WriteLine(h.value.a); } }"
        },
        {
            "nested structs",
            "struct Inner { public int v; } struct Outer { public Inner inner; public int tag; } " +
            "class Program { static void Main() { Outer o; o.inner.v = 1; o.tag = 2; System.Console.WriteLine(o.tag); } }"
        },
        {
            "byref primitives",
            "class Program { static void Inc(ref int x) { x = x + 1; } static void Set(out int x) { x = 4; } " +
            "static void Main() { int a = 1; Inc(ref a); int b; Set(out b); System.Console.WriteLine(a + b); } }"
        }
    };

    [Theory]
    [MemberData(nameof(UnsupportedSources))]
    public async Task Reports_arc3001_for_unsupported_construct(string name, string source)
    {
        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(new UnsupportedConstructAnalyzer(), source);

        var reported = diagnostics
            .Where(d => d.Id == DiagnosticCatalog.UnsupportedConstructId)
            .ToList();
        Assert.True(reported.Count >= 1, $"{name} reported no ARC3001 diagnostics");
        Assert.Equal(DiagnosticSeverity.Error, reported[0].Severity);
    }

    [Theory]
    [MemberData(nameof(SupportedSources))]
    public async Task Does_not_report_arc3001_for_supported_source(string name, string source)
    {
        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(new UnsupportedConstructAnalyzer(), source);

        var reported = diagnostics
            .Where(d => d.Id == DiagnosticCatalog.UnsupportedConstructId)
            .Select(d => d.GetMessage())
            .ToList();
        Assert.True(reported.Count == 0, $"{name} reported: {string.Join("; ", reported)}");
    }
}
