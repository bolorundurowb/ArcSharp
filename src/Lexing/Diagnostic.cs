namespace ArcSharp.Lexing;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info
}

public sealed class Diagnostic
{
    public required string Message { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public string Id { get; init; } = "ARC0000";
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

    private string SeverityText => Severity switch
    {
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Info => "info",
        _ => "error"
    };

    public override string ToString() => $"({Line},{Column}): {SeverityText} {Id}: {Message}";
}
