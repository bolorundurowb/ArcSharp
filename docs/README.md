<p align="center">
  <img src="assets/logo.svg" alt="ArcSharp" width="360">
</p>

# ArcSharp documentation

ArcSharp compiles a subset of C# 12 to native Windows x64 executables. It
manages memory with automatic reference counting (ARC) instead of a garbage
collector, so an object is destroyed at the moment its last strong reference
goes away.

ArcSharp is a proof of concept. Read [Known issues](language-reference.md#known-issues)
before you rely on it for anything beyond experiments.

## Using ArcSharp

| Guide | What it covers |
|---|---|
| [Getting started](getting-started.md) | Install the prerequisites, build the compiler, and run your first program |
| [Language reference](language-reference.md) | The supported C# subset, how it differs from .NET, and known issues |
| [Memory model](memory-model.md) | How ARC decides when objects die, why cycles leak, and how to break them with `WeakReference<T>` |
| [Command-line reference](cli-reference.md) | Every compiler option, the files it writes, and its exit codes |
| [Diagnostics](diagnostics.md) | Every error and warning code, and what to do about each |
| [Troubleshooting](troubleshooting.md) | Build failures, crashes, leaks, and wrong output |

## Working on ArcSharp

| Document | What it covers |
|---|---|
| [Architecture](architecture.md) | The compiler pipeline, the object layout, the ARC ownership rules, and open design questions |
| [Roadmap](roadmap.md) | The plan from proof of concept to a usable toolchain, with priorities |
| [Verification](verification.md) | The test harness and the results of the latest recorded run |
| [`analyzers/`](../analyzers/) | The Roslyn analyser, the shared `ARC3xxx` catalogue, and their xUnit tests |
| [`AGENTS.md`](../AGENTS.md) | Contributor guide: the invariants you must preserve and the change checklist |

## Logo

The logo is in [`assets/`](assets/). `logo.svg` is the horizontal lockup and
adapts its wordmark colour to light and dark themes. `logo-mark.svg` is the
square mark on its own, for icons and avatars. The ring stands for a reference
count, and the detached dot is a reference being released.
