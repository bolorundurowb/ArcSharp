using ArcSharp.Binding;
using Microsoft.CodeAnalysis;

namespace ArcSharp.Frontend;

// Body lowering and the ArcSharp-only diagnostic walk land in the next steps.
// Until then a method body is an error, so a declaration-only compile cannot
// emit a program that runs the wrong code. Roslyn's own diagnostics are copied
// now, because a program that Roslyn rejects must not be lowered.
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

    private void LowerBody(PendingBody pending)
    {
        Report(pending.Method.DeclarationLine, 0, "method bodies are not lowered yet", "ARC3001");
        _readyBodies.Add(new BoundMethodBody
        {
            Method = pending.Method,
            Body = new BoundBlock()
        });
    }
}
