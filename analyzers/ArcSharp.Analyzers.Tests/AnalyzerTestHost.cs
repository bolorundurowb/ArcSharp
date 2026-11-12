using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ArcSharp.Analyzers.Tests;

// Compiles a snippet in memory and runs one analyser over it. The framework
// reference set comes from the test host, so snippets can use Console,
// WeakReference<T>, arrays, and the rest of the base library.
internal static class AnalyzerTestHost
{
    private static readonly ImmutableArray<MetadataReference> References = LoadReferences();

    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        DiagnosticAnalyzer analyzer, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12));
        var compilation = CSharpCompilation.Create(
            "ArcSharpAnalyzerTests",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return await compilation
            .WithAnalyzers(ImmutableArray.Create(analyzer))
            .GetAnalyzerDiagnosticsAsync();
    }

    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES is not available");

        return trusted
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();
    }
}
