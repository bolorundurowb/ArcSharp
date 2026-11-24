using ArcSharp.Diagnostics;
using Microsoft.CodeAnalysis;
using OmniAssert;
using Xunit;

namespace ArcSharp.Analyzers.Tests;

public sealed class UnsupportedConstructTests
{
    public static TheoryData<string, string> UnsupportedSources => new()
    {
        { "foreach", "class C { static void Main() { string xs = \"x\"; foreach (char x in xs) { } } }" },
        { "switch list pattern", "class C { public int Length => 1; public int this[int i] => i; static void Test(C value) { switch (value) { case [1]: break; } } }" },
        { "exception filter", "class C { static void Main() { try { } catch (System.Exception e) when (e != null) { } } }" },
        { "throw expression", "class C { static int Main() { int x = 1; return x > 0 ? x : throw new System.Exception(); } }" },
        { "lock", "class C { static void Main() { object o = new object(); lock (o) { } } }" },
        { "anonymous method", "class C { static void Main() { System.Func<int, int> f = delegate(int x) { return x + 1; }; } }" },
        { "query", "class C { static void Main() { int[] xs = new int[1]; var q = from x in xs select x; } }" },
        { "list pattern", "class C { public int Length => 1; public int this[int i] => i; static bool Test(C value) => value is [1]; }" },
        { "list switch expression", "class C { public int Length => 1; public int this[int i] => i; static int Test(C value) => value switch { [1] => 2, _ => 3 }; }" },
        { "conditional access", "class C { static void Main() { string s = null; int n = s?.Length ?? 0; } }" },
        { "coalescing assignment", "class C { static void Main() { string s = null; s ??= \"x\"; } }" },
        { "interpolated string", "class C { static void Main() { System.Console.WriteLine($\"x\"); } }" },
        { "target-typed new", "class C { static void Main() { C c = new(); } }" },
        { "struct object initializer", "struct S { public int X; } class C { static void Main() { S s = new S { X = 1 }; } }" },
        { "collection initializer", "class B : System.Collections.IEnumerable { public void Add(int x) { } public System.Collections.IEnumerator GetEnumerator() { return null; } } class C { static void Main() { B b = new B { 1 }; } }" },
        { "attribute", "[System.Obsolete] class C { }" },
        { "generic delegate", "delegate void D<T>(T value); class C { }" },
        { "method group delegate", "delegate void D(); class C { static void M() { D d = M; } }" },
        { "framework lambda", "class C { static void M() { System.Func<int> f = () => 1; } }" },
        { "record struct", "record struct R(int X); class C { }" },
        { "generic record", "record R<T>(T X); class C { }" },
        { "local function", "class C { static void Main() { void F() { } F(); } }" },
        { "expression body", "interface I { int One => 1; }" },
        { "generic static abstract method", "interface I { static abstract T Make<T>(); }" },
        { "generic interface method on a struct", "interface I { T M<T>(T value); } struct S : I { public T M<T>(T value) { return value; } }" },
        { "using var", "class C { static void Main() { using var d = new System.IO.MemoryStream(); } }" },
        { "multi-dimensional array", "class C { static void Main() { int[,] xs = new int[1, 1]; } }" },
        { "sealed class", "sealed class C { }" },
        { "top-level statements", "System.Console.WriteLine(\"x\");" },
        { "tuple", "class C { static void Main() { (int a, int b) t = (1, 2); } }" },
        { "base access", "class B { public int X; } class C : B { public int Get() { return base.X; } }" },
        { "ref local", "class C { static void Main() { int n = 1; ref int a = ref n; } }" },
        { "ref return", "class C { static ref int Get(ref int x) { return ref x; } }" },
        { "decimal constructor with parts", "class C { static void Main() { decimal d = new decimal(1, 2, 3, false, 0); } }" },
        { "nullable primitive boxing", "class C { static void Main() { int? n = 1; object o = n; } }" },
        { "GC.ReRegisterForFinalize", "class C { static void Main() { System.GC.ReRegisterForFinalize(new C()); } }" }
    };

    public static TheoryData<string, string> SupportedSources => new()
    {
        { "lambda capture", "delegate int D(); class C { static int M(int x) { D d = () => ++x; return d(); } }" },
        { "generic class", "class Box<T> where T : class { public T Value; }" },
        { "generic method", "class C { static T Identity<T>(T value) { return value; } }" },
        { "generic interface and struct", "interface IValue<T> { T Get(); } struct Pair<T> { public T Item; public int Tag; }" },
        { "record class and with", "record R(int X); class C { static R Move(R r) { return r with { X = 2 }; } }" },
        {
            "deconstruction into existing variables",
            "record R(int X, int Y); class C { static void Main() { int a; int b; (a, b) = new R(1, 2); } }"
        },
        { "class object initializer", "class N { public int X; } class C { static void Main() { N n = new N { X = 1 }; } }" },
        { "plain program", "class Program { static void Main() { System.Console.WriteLine(\"hi\"); } }" },
        {
            "try, catch, finally and throw",
            "class Program { static void Main() { try { throw new System.InvalidOperationException(\"x\"); } " +
            "catch (System.InvalidOperationException e) { System.Console.WriteLine(e.Message); throw; } " +
            "finally { System.Console.WriteLine(\"done\"); } } }"
        },
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
            "static auto-property",
            "class Program { public static int Count { get; set; } static void Main() { Count = 2; System.Console.WriteLine(Count); } }"
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
            "finalisers and GC.SuppressFinalize",
            "class Base { ~Base() { } } " +
            "class Resource : Base, System.IDisposable { ~Resource() { } public void Dispose() { System.GC.SuppressFinalize(this); } } " +
            "class Program { static void Main() { } }"
        },
        {
            "virtual and interface generic methods on classes",
            "interface I { T M<T>(T value); } class A : I { public virtual T M<T>(T value) { return value; } } " +
            "class B : A { public override T M<T>(T value) { return value; } } abstract class C { public abstract T N<T>(T value); }"
        },
        {
            "static abstract members",
            "interface IZero<T> where T : IZero<T> { static abstract T Zero(); static abstract T operator +(T a, T b); " +
            "static abstract int Id { get; } static virtual int Weight() { return 1; } }"
        },
        {
            "partial classes and extension methods",
            "partial class Box { public int Value; } partial class Box { public int Twice() { return Value * 2; } } " +
            "static class Extensions { public static int Plus(this Box box, int amount) { return box.Value + amount; } }"
        },
        {
            "updates through accessors",
            "class Counter { private int _count; public int Count { get { return _count; } set { _count = value; } } " +
            "public int this[int index] { get { return _count + index; } set { _count = value; } } " +
            "void Bump() { Count += 2; Count++; this[1] -= 1; this[2]--; } }"
        },
        {
            "decimal arithmetic and updates",
            "class C { static decimal Sum(decimal a, decimal b) { decimal t = a * b + 1.5m; t += a; t++; return -t / 2; } }"
        },
        {
            "lifted nullable updates",
            "class C { static void Main() { int? a = 1; a += 2; a++; decimal? d = 1m; d *= 2m; d--; } }"
        },
        {
            "nullable struct boxing",
            "struct S { public int V; } class C { static void Main() { S? s = new S(); object o = s; S? back = o as S?; } }"
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
        Ensure.Must<Diagnostic>(reported).HaveCountGreaterThan(0, $"{name} reported no ARC3001 diagnostics");
        Ensure.Must(reported[0].Severity).Be(DiagnosticSeverity.Error);
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
        Ensure.Must<string>(reported).HaveCount(0, $"{name} reported: {string.Join("; ", reported)}");
    }

    [Theory]
    [InlineData("System.GC.Collect();", DiagnosticCatalog.GcCollectMessage)]
    [InlineData("System.GC.WaitForPendingFinalizers();", DiagnosticCatalog.GcWaitMessage)]
    public async Task Reports_arc3200_for_a_gc_call_that_does_nothing(string call, string message)
    {
        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            new UnsupportedConstructAnalyzer(), $"class C {{ static void Main() {{ {call} }} }}");

        var reported = diagnostics.Where(d => d.Id == DiagnosticCatalog.ArcClrDisagreementId).ToList();
        Ensure.Must<Diagnostic>(reported).HaveCount(1);
        Ensure.Must(reported[0].Severity).Be(DiagnosticSeverity.Info);
        Ensure.Must(reported[0].GetMessage()).Be(message);
    }

    [Fact]
    public async Task Does_not_report_arc3200_for_gc_calls_that_ArcSharp_honours()
    {
        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            new UnsupportedConstructAnalyzer(),
            "class C { static void Main() { System.GC.SuppressFinalize(new C()); } }");

        Ensure.Must<Diagnostic>(diagnostics).NotContain(d => d.Id == DiagnosticCatalog.ArcClrDisagreementId);
    }
}
