using Microsoft.CodeAnalysis;

namespace ArcSharp.Frontend;

// The ArcSharp-only diagnostic walk lands in the next step. Roslyn's own
// diagnostics are copied here, because a program that Roslyn rejects must not
// be lowered.
sealed partial class RoslynLowering
{
    public void CopyRoslynDiagnostics()
    {
        foreach (var diagnostic in _compilation.GetDiagnostics())
        {
            if (diagnostic.Severity is DiagnosticSeverity.Hidden or DiagnosticSeverity.Info || diagnostic.IsSuppressed)
            {
                continue;
            }

            var severity = diagnostic.Severity == DiagnosticSeverity.Warning
                ? Lexing.DiagnosticSeverity.Warning
                : Lexing.DiagnosticSeverity.Error;
            Report(diagnostic.Location, diagnostic.GetMessage(), diagnostic.Id, severity);
        }
    }

    public void ReportUnsupportedSyntax()
    {
    }

    public void ReportLifetimeRules()
    {
    }
}
