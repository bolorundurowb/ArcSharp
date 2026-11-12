using System.Collections.Immutable;
using ArcSharp.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ArcSharp.Analyzers;

// Maps the shared ARC3xxx catalogue onto Roslyn descriptors. The catalogue is
// a plain netstandard2.0 assembly so the compiler can reference it without
// taking a Roslyn dependency.
internal static class Descriptors
{
    public static readonly DiagnosticDescriptor UnsupportedConstruct = new(
        id: DiagnosticCatalog.UnsupportedConstructId,
        title: DiagnosticCatalog.UnsupportedConstructTitle,
        messageFormat: "{0}",
        category: DiagnosticCatalog.Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "ArcSharp compiles a subset of C# 12. A construct outside that subset must be rejected, never silently dropped.");

    public static readonly DiagnosticDescriptor PossibleStrongCycle = new(
        id: DiagnosticCatalog.PossibleStrongCycleId,
        title: DiagnosticCatalog.PossibleStrongCycleTitle,
        messageFormat: "{0}",
        category: DiagnosticCatalog.Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ARC leaks reference cycles formed by strong fields. Break a back-edge with WeakReference<T>.",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    public static readonly ImmutableArray<DiagnosticDescriptor> All =
        ImmutableArray.Create(UnsupportedConstruct, PossibleStrongCycle);
}
