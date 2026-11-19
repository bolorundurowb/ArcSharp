using ArcSharp.Binding;
using Diagnostic = ArcSharp.Lexing.Diagnostic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ArcSharp.Frontend;

public static class RoslynFrontend
{
    public static BoundProgram Bind(IReadOnlyList<string> inputs, List<(string file, Diagnostic d)> diags)
    {
        var program = new BoundProgram();
        var reference = ReferenceAssembly();
        if (reference == null)
        {
            diags.Add(("", Error(0, 0, "ARC2001", "the reference assembly ArcSharp.References.dll was not found next to the compiler")));
            return program;
        }

        var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12);
        var trees = new List<SyntaxTree>
        {
            // CompilationOptions.Usings is not applied to ordinary compilations.
            CSharpSyntaxTree.ParseText("global using System;", parse, path: "global-usings.cs")
        };
        foreach (var input in inputs)
        {
            var text = LegacyWeak.Rewrite(File.ReadAllText(input));
            trees.Add(CSharpSyntaxTree.ParseText(text, parse, path: Path.GetFullPath(input)));
        }

        var options = new CSharpCompilationOptions(
            OutputKind.ConsoleApplication,
            nullableContextOptions: NullableContextOptions.Disable,
            specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>
            {
                // A trailing '?' is accepted and ignored, matching the current compiler.
                ["CS8632"] = ReportDiagnostic.Suppress,
                ["CS8019"] = ReportDiagnostic.Suppress
            });
        var compilation = CSharpCompilation.Create(
            "arcsharp-program",
            trees,
            [MetadataReference.CreateFromFile(reference)],
            options);

        var lowering = new RoslynLowering(compilation, diags);
        lowering.CopyRoslynDiagnostics();
        lowering.ReportUnsupportedSyntax();
        lowering.ReportLifetimeRules();
        lowering.Lower(program);
        return program;
    }

    private static string? ReferenceAssembly()
    {
        var dir = Path.GetDirectoryName(typeof(RoslynFrontend).Assembly.Location);
        if (dir == null)
        {
            return null;
        }

        var path = Path.Combine(dir, "ArcSharp.References.dll");
        return File.Exists(path) ? path : null;
    }

    internal static Diagnostic Error(int line, int column, string id, string message) => new()
    {
        Line = line,
        Column = column,
        Id = id,
        Message = message,
        Severity = Lexing.DiagnosticSeverity.Error
    };
}
