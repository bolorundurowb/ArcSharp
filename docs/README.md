<p align="center">
  <img src="assets/logo.svg" alt="ArcSharp" width="360">
</p>

# ArcSharp documentation

ArcSharp compiles a subset of C# 12 to native Windows x64 executables. The
compiler that exists today reference-counts every object and destroys it when
the last strong reference goes away. The [Roadmap](roadmap.md) is the
direction from here: a Roslyn frontend, an ownership tree rather than CIL, ARC
only where a lifetime cannot be proved, deterministic finalisation, and a
bounded compatibility target.

ArcSharp is a proof of concept. Read [Known issues](language-reference.md#known-issues)
before you rely on it for anything beyond experiments.

## Using ArcSharp

| Guide | What it covers |
|---|---|
| [Getting started](getting-started.md) | Install the prerequisites, build the compiler, and run your first program |
| [Language reference](language-reference.md) | The supported C# subset, how it differs from .NET, and known issues |
| [Memory model](memory-model.md) | When objects die, why cycles leak, `WeakReference<T>`, and the finalisation contract |
| [Command-line reference](cli-reference.md) | Every compiler option, the files it writes, and its exit codes |
| [Diagnostics](diagnostics.md) | Every error and warning code, and what to do about each |
| [Troubleshooting](troubleshooting.md) | Build failures, crashes, leaks, and wrong output |

## Working on ArcSharp

| Document | What it covers |
|---|---|
| [Architecture](architecture.md) | The pipeline as it is today, the object layout, the ARC rules, and the decisions that bound the direction |
| [Roadmap](roadmap.md) | Thirteen milestones from today's compiler to the end goal, one at a time: Roslyn first, then a verification baseline, the lifetime pass, finalisers, and the rest of the compatibility target. That target is a subset of C# and the libraries, not the whole framework |
| [Verification](verification.md) | The test harness and the results of the latest recorded run |
| [`analyzers/`](../analyzers/) | The Roslyn analyser, the shared `ARC3xxx` catalogue, and their xUnit tests |
| [`AGENTS.md`](../AGENTS.md) | Contributor guide: the invariants you must preserve and the change checklist |

## Feature implementation

- [ARC checking mode](features/arc-checking-mode.md)
- [Retain and release statistics](features/arc-statistics.md)
- [M2 verification baseline](features/verification-baseline.md)
- [Lifetime classification, borrows and moves](features/lifetime-reads-and-moves.md)
- [Comparing ArcSharp with the .NET garbage collector](features/gc-comparison.md)

## Logo

The logo is in [`assets/`](assets/). `logo.svg` is the horizontal lockup and
adapts its wordmark colour to light and dark themes. `logo-mark.svg` is the
square mark on its own, for icons and avatars. The ring stands for a reference
count, and the detached dot is a reference being released.
