using System.Collections.Generic;

namespace ArcSharp.Diagnostics;

// The ARC3xxx catalogue shared by the compiler and the Roslyn analyser.
//
// This assembly deliberately has no Roslyn reference: the compiler references
// it, and the compiler must stay free of NuGet dependencies. The analyser maps
// these ids, titles, and messages onto DiagnosticDescriptors.
public static class DiagnosticCatalog
{
    public const string UnsupportedConstructId = "ARC3001";
    public const string PossibleStrongCycleId = "ARC3100";
    public const string ArcClrDisagreementId = "ARC3200";
    public const string ThreadingId = "ARC3300";

    public const string UnsupportedConstructTitle = "Construct is not supported by ArcSharp";
    public const string PossibleStrongCycleTitle = "Possible strong reference cycle";
    public const string ArcClrDisagreementTitle = "ARC and CLR behaviour disagree";
    public const string ThreadingTitle = "Threading is not supported by ArcSharp";

    public const string Category = "ArcSharp";

    // A strongly connected component of two or more classes.
    public static string PossibleStrongCycleMessage(IReadOnlyList<string> names) =>
        $"possible strong cycle involving {string.Join(", ", names)}: " +
        "strong fields can reach each other in a loop, and ARC leaks it. " +
        "Break a back-edge with WeakReference<T>.";

    // A class with a strong field of its own type.
    public static string SelfCycleMessage(string name) =>
        $"possible strong cycle: '{name}' has a strong field of its own type, " +
        "so instances can form a loop that ARC leaks. Break it with WeakReference<T>.";
}
