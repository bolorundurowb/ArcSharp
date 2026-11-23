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

    // ARC3200 (info): a GC call that has no effect because ARC reclaims an
    // object at its last strong release.
    public const string GcCollectMessage =
        "GC.Collect() does nothing under ArcSharp: an object is destroyed, and its finaliser runs, " +
        "when its last strong reference is released.";

    public const string GcWaitMessage =
        "GC.WaitForPendingFinalizers() does nothing under ArcSharp: finalisers run synchronously " +
        "at the last release, so none are ever pending.";

    // ARC3001: resurrection is rejected, so there is nothing to re-register.
    public const string GcReRegisterMessage =
        "GC.ReRegisterForFinalize is not supported: a finaliser runs once, at the last strong release, " +
        "and an object cannot be resurrected.";

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
