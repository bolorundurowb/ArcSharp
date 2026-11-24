using ArcSharp.Diagnostics;
using Microsoft.CodeAnalysis;
using OmniAssert;
using Xunit;

namespace ArcSharp.Analyzers.Tests;

public sealed class StrongCycleTests
{
    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> Analyze(string source) =>
        AnalyzerTestHost.AnalyzeAsync(new StrongCycleAnalyzer(), source);

    private static async Task<Diagnostic> SingleWarning(string source)
    {
        var diagnostics = await Analyze(source);
        var reported = diagnostics.Where(d => d.Id == DiagnosticCatalog.PossibleStrongCycleId).ToList();
        Ensure.Must<Diagnostic>(reported).HaveCount(1);
        Ensure.Must(reported[0].Severity).Be(DiagnosticSeverity.Warning);
        return reported[0];
    }

    [Fact]
    public async Task Mutual_references_are_a_cycle()
    {
        var d = await SingleWarning(
            "class A { public B b; } class B { public A a; } class Program { static void Main() { } }");

        Ensure.Must(d.GetMessage()).Contain("A");
        Ensure.Must(d.GetMessage()).Contain("B");
    }

    [Fact]
    public async Task Self_reference_is_a_cycle()
    {
        var d = await SingleWarning(
            "class Node { public Node next; } class Program { static void Main() { } }");

        Ensure.Must(d.GetMessage()).Contain("Node");
    }

    [Fact]
    public async Task Array_field_is_an_edge()
    {
        await SingleWarning(
            "class Node { public Node[] children; } class Program { static void Main() { } }");
    }

    [Fact]
    public async Task Static_field_is_an_edge()
    {
        await SingleWarning(
            "class A { public static B b; } class B { public A a; } class Program { static void Main() { } }");
    }

    [Fact]
    public async Task Class_reference_through_a_struct_is_an_edge()
    {
        await SingleWarning(
            "struct Link { public A owner; } class A { public Link link; } class Program { static void Main() { } }");
    }

    [Fact]
    public async Task Lambda_capture_can_close_a_cycle()
    {
        var d = await SingleWarning(
            "delegate int Reader(); class Source { Reader read; int value; void Subscribe() { read = () => value; } }");

        Ensure.Must(d.GetMessage()).Contain("Source");
        Ensure.Must(d.GetMessage()).Contain("Reader");
    }

    [Fact]
    public async Task Weak_reference_breaks_a_cycle()
    {
        var diagnostics = await Analyze(
            "class Node { public System.WeakReference<Node> next; } class Program { static void Main() { } }");

        Ensure.Must<Diagnostic>(diagnostics).NotContain(d => d.Id == DiagnosticCatalog.PossibleStrongCycleId);
    }

    [Fact]
    public async Task Weak_reference_inside_a_struct_breaks_a_cycle()
    {
        var diagnostics = await Analyze(
            "struct Link { public System.WeakReference<A> owner; } class A { public Link link; } class Program { static void Main() { } }");

        Ensure.Must<Diagnostic>(diagnostics).NotContain(d => d.Id == DiagnosticCatalog.PossibleStrongCycleId);
    }

    [Fact]
    public async Task Acyclic_classes_are_clean()
    {
        var diagnostics = await Analyze(
            "class A { public B b; } class B { public int x; } class Program { static void Main() { } }");

        Ensure.Must<Diagnostic>(diagnostics).NotContain(d => d.Id == DiagnosticCatalog.PossibleStrongCycleId);
    }

    [Fact]
    public async Task Struct_fields_do_not_add_a_cycle_on_their_own()
    {
        var diagnostics = await Analyze(
            "struct Pair { public int a; public int b; } class Holder { public Pair p; } class Program { static void Main() { } }");

        Ensure.Must<Diagnostic>(diagnostics).NotContain(d => d.Id == DiagnosticCatalog.PossibleStrongCycleId);
    }
}
