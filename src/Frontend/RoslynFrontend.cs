using ArcSharp.Binding;
using Diagnostic = ArcSharp.Lexing.Diagnostic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ArcSharp.Frontend;

// Hosts a Roslyn compilation of the program against ArcSharp.References.
// Declaration and body lowering land in the following steps. Until then this
// reports Roslyn's own diagnostics and refuses to emit, so a half-lowered
// program cannot run.
public static class RoslynFrontend
{
    public static BoundProgram Bind(IReadOnlyList<string> inputs, List<(string file, Diagnostic d)> diags)
    {
        var reference = ReferenceAssembly();
        if (reference == null)
        {
            diags.Add(("", Error(0, 0, "ARC2001", "the reference assembly ArcSharp.References.dll was not found next to the compiler")));
            return new BoundProgram();
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

        var failed = false;
        foreach (var diagnostic in compilation.GetDiagnostics())
        {
            if (diagnostic.Severity is DiagnosticSeverity.Hidden or DiagnosticSeverity.Info || diagnostic.IsSuppressed)
            {
                continue;
            }

            failed |= diagnostic.Severity == DiagnosticSeverity.Error;
            var severity = diagnostic.Severity == DiagnosticSeverity.Warning
                ? Lexing.DiagnosticSeverity.Warning
                : Lexing.DiagnosticSeverity.Error;
            var span = diagnostic.Location.GetLineSpan();
            var line = span.StartLinePosition.Line + 1;
            var column = span.StartLinePosition.Character + 1;
            var file = diagnostic.Location.SourceTree == null ? "" : diagnostic.Location.SourceTree.FilePath;
            diags.Add((file, Error(line, column, diagnostic.Id, diagnostic.GetMessage(), severity)));
        }

        if (!failed)
        {
            diags.Add(("", Error(0, 0, "ARC2001", "the Roslyn frontend does not lower programs yet")));
        }

        return new BoundProgram();
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

    private static Diagnostic Error(
        int line, int column, string id, string message,
        Lexing.DiagnosticSeverity severity = Lexing.DiagnosticSeverity.Error) => new()
    {
        Line = line,
        Column = column,
        Id = id,
        Message = message,
        Severity = severity
    };
}
