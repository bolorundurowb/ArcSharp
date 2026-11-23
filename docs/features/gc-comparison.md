# Comparing ArcSharp with the .NET garbage collector

ArcSharp's claim is that reference counting gives a program a smaller memory
footprint and no collector pauses. This page records a measurement of that
claim against the ordinary .NET runtime, on source that both compilers accept.
It also records where the claim does not hold.

The run below is one machine on 20 November 2026. Repeat it with
`tools\compare-gc.ps1` at the end of every milestone in the roadmap, and add
the new medians here. The script is not part of
`tools\verify.ps1`. The numbers move with the machine, and the runs are longer
than the sample suite.

## What was compared

Three programs in `bench/` were compiled twice: once with ArcSharp to a native
executable, and once with `dotnet build -c Release` against the .NET 11
runtime. The .NET build is a normal console program. It uses workstation
concurrent GC, which is the console default. Tiered compilation and dynamic
PGO were switched off so each run pays for one compilation of the methods.
Server GC was left off. Native AOT was not used: that would remove the JIT and
keep the garbage collector, which is a different comparison.

ArcSharp's executable was linked with `clang -O2`. Link-time optimisation was
left off, so `arc_retain` and `arc_release` stay real calls into the C
runtime. That matches the compiler today. The compiler's own `--clang` path
does not pass an optimisation level; the comparison script passes `-O2` so the
native program is optimised and the .NET program is a Release build.

The .NET process includes `bench/dotnet/GcReport.cs`, which ArcSharp does not
compile. A background thread polls `GC.GetGCMemoryInfo` once a millisecond and
records each collection's suspension. The thread is part of the measured .NET
process.

Both programs print the same checksum. The script fails if the checksums
differ, if ArcSharp's `live` count is not 0, or if the .NET process does not
write a `[gc]` line.

## The workloads

`baseline` prints a line and returns. It is the cost of starting the process.

`retained` builds 50 buckets of 8,000 nodes and holds them until `Main`
returns. Each node is a class with one `int` field. A bucket's array stays
under the .NET large-object threshold of 85,000 bytes, so the arrays are
ordinary objects. The nodes are not linked together. Releasing a linked chain
is recursive in the current runtime, and a long chain overflows the stack.

`churn` keeps that same set, then builds and drops a further batch five times.
Each batch is 25 buckets of 8,000 nodes. Storing the next batch releases the
previous one. On ArcSharp that release is immediate. On .NET the previous
batch becomes garbage and waits for a collection.

The checksum reads every stored value, so a compiler that deleted the objects
would produce a different number. Both runtimes agreed:

| Workload | Checksum |
|---|---|
| baseline | 0 |
| retained | 3199600000 |
| churn | 7199100000 |

## How a number was taken

The script discards two launches of each executable, then records five. The
table is the median. Wall time is the whole process, including startup. For
.NET that includes JIT. Peak working set and peak private commit come from
`GetProcessMemoryInfo` after the process has exited, while its handle is still
open. Working set includes shared libraries mapped into the process. Private
commit is the process's own pagefile usage, which is the closer figure for
"how much memory this program added".

`ARC_STATS=1` was set for the ArcSharp runs. The counters increment on every
run; the variable only turns on the extra line. `ARC_CHECK` was left off,
because checking mode keeps a registry of live objects and would raise the
peak.

## Results

Windows NT 10.0.26300, 20 processors, .NET SDK 11.0.100, clang 22.1.8.
Command:

```powershell
powershell -ExecutionPolicy Bypass -File tools\compare-gc.ps1 -Runs 5
```

A MiB here is 1,048,576 bytes. The range is the minimum and maximum of the
five recorded runs.

| Workload | Runtime | Wall time | Peak working set | Peak private commit |
|---|---|---|---|---|
| baseline | ArcSharp | 14.7 ms (14.4–16.6) | 4,038,656 (3.9 MiB) | 663,552 (0.6 MiB) |
| baseline | .NET | 61.0 ms (57.5–62.4) | 24,211,456 (23.1 MiB) | 6,582,272 (6.3 MiB) |
| retained | ArcSharp | 60.2 ms (59.5–63.0) | 26,181,632 (25.0 MiB) | 24,489,984 (23.4 MiB) |
| retained | .NET | 59.7 ms (58.6–74.2) | 37,093,376 (35.4 MiB) | 19,554,304 (18.6 MiB) |
| churn | ArcSharp | 138.3 ms (137.8–147.4) | 37,466,112 (35.7 MiB) | 36,356,096 (34.7 MiB) |
| churn | .NET | 91.3 ms (90.5–92.6) | 64,016,384 (61.1 MiB) | 46,317,568 (44.2 MiB) |

ArcSharp accounting was the same on every run. `release = retain + dead`, and
`live` was 0, so the run neither leaked nor freed an object twice.

| Workload | alloc | retain | release |
|---|---|---|---|
| baseline | 4 | 0 | 4 |
| retained | 400,105 | 3,600,502 | 4,000,607 |
| churn | 1,400,360 | 9,601,132 | 11,001,492 |

The .NET baseline and the retained set did not collect. `GC.CollectionCount`
stayed 0. The retained set allocated about 13,438,448 managed bytes and the
heap absorbed it. The churn program collected twice on every run, one of those
a generation 1 collection, and the sampler saw both (`missed=0`). Each
collection reported a single suspension:

| Run | First pause | Second pause | Total |
|---|---|---|---|
| 1 | 4.143 ms | 14.601 ms | 18.744 ms |
| 2 | 4.261 ms | 15.173 ms | 19.434 ms |
| 3 | 4.078 ms | 15.450 ms | 19.528 ms |
| 4 | 4.301 ms | 15.066 ms | 19.367 ms |
| 5 | 3.600 ms | 14.957 ms | 18.557 ms |

The median of the first pauses is 4.1 ms and the median of the second is
15.1 ms. The median of the two pauses added together is 19.4 ms, inside a
process whose median wall time was 91.3 ms. After the last collection the
managed heap was 18,243,040 bytes, with 40,128,512 bytes committed and 12,528
bytes fragmented. The process had allocated 45,444,208 managed bytes in total.
ArcSharp has no collector, so it has no suspension to report.

## M2 medians (21 November 2026)

The same command completed five measured runs per workload and runtime on
Windows x64 with .NET SDK 11.0.100 and clang 22.1.8. All checksums matched,
and all ArcSharp runs ended with `live=0`. The ARC accounting totals matched
the original run.

| Workload | Runtime | Wall time | Peak working set | Peak private commit |
|---|---|---:|---:|---:|
| baseline | ArcSharp | 16.0 ms | 3.9 MiB | 0.6 MiB |
| baseline | .NET | 59.1 ms | 23.1 MiB | 6.3 MiB |
| retained | ArcSharp | 58.5 ms | 25.0 MiB | 23.4 MiB |
| retained | .NET | 61.3 ms | 35.3 MiB | 18.7 MiB |
| churn | ArcSharp | 137.0 ms | 35.7 MiB | 34.7 MiB |
| churn | .NET | 91.2 ms | 61.0 MiB | 44.2 MiB |

The .NET churn sampler recorded both pauses in all five runs. The median
first and second pauses were 4.012 ms and 15.142 ms. The median total pause
per run was 19.231 ms.

## M3 medians (22 November 2026)

The five-run comparison completed on Windows x64 with .NET SDK 11.0.100
and clang 22.1.8. All checksums matched and every ArcSharp run reported
`live=0`.

| Workload | Runtime | Wall time | Peak working set | Peak private commit |
|---|---|---:|---:|---:|
| baseline | ArcSharp | 17.8 ms | 3.8 MiB | 0.6 MiB |
| baseline | .NET | 58.9 ms | 23.1 MiB | 6.3 MiB |
| retained | ArcSharp | 57.7 ms | 25.0 MiB | 23.4 MiB |
| retained | .NET | 76.5 ms | 35.4 MiB | 18.6 MiB |
| churn | ArcSharp | 148.0 ms | 35.7 MiB | 34.7 MiB |
| churn | .NET | 105.8 ms | 61.0 MiB | 44.2 MiB |

The churn sampler observed both .NET pauses in three of the five runs. The
median total for those three complete traces was 20.490 ms. The ArcSharp
churn workload recorded 9,200,856 retains and 10,601,216 releases per run.

## M4 medians (23 November 2026)

The five-run comparison completed on Windows x64 with .NET SDK 11.0.100
and clang 22.1.8, after the deterministic finalisers landed. All checksums
matched and every ArcSharp run reported `live=0`.

| Workload | Runtime | Wall time | Peak working set | Peak private commit |
|---|---|---:|---:|---:|
| baseline | ArcSharp | 18.3 ms | 3.9 MiB | 0.6 MiB |
| baseline | .NET | 59.0 ms | 23.2 MiB | 6.3 MiB |
| retained | ArcSharp | 63.9 ms | 25.1 MiB | 23.4 MiB |
| retained | .NET | 75.0 ms | 35.4 MiB | 18.6 MiB |
| churn | ArcSharp | 147.4 ms | 35.8 MiB | 34.8 MiB |
| churn | .NET | 105.1 ms | 61.0 MiB | 44.1 MiB |

The ARC traffic did not change. The churn workload recorded 9,200,856 retains
and 10,601,216 releases per run, as in M3, and the retained workload recorded
3,200,351 retains and 3,600,456 releases. None of the three workloads declares a
finaliser.

The churn sampler saw both .NET pauses in one of the five runs, where they
were 5.525 ms and 17.306 ms. The other four runs saw only the second pause,
between 15.771 ms and 17.767 ms. The median of the longest pause in each run
was 16.896 ms. ArcSharp has no collector, so it has no pause to report.

Compared with M3, the ArcSharp wall time was 0.5 ms higher on the baseline,
6.2 ms higher on the retained set, and 0.6 ms lower on the churn program. The
retained set is the workload that destroys 400,000 objects in one go at exit,
which now goes through the worklist and a flag check on every object. That is
a plausible cause of the retained difference, but it was not isolated, and the
machine shows run-to-run differences of a few milliseconds. Memory use is within
0.1 MiB of M3 on every row.

## M5 medians (23 November 2026)

The five-run comparison completed on Windows x64 with .NET SDK 11.0.100
and clang 22.1.8, after exceptions landed. All checksums matched and every
ArcSharp run reported `live=0`.

| Workload | Runtime | Wall time | Peak working set | Peak private commit |
|---|---|---:|---:|---:|
| baseline | ArcSharp | 19.1 ms | 3.9 MiB | 0.6 MiB |
| baseline | .NET | 59.0 ms | 23.1 MiB | 6.3 MiB |
| retained | ArcSharp | 62.9 ms | 25.1 MiB | 23.4 MiB |
| retained | .NET | 74.3 ms | 35.4 MiB | 18.7 MiB |
| churn | ArcSharp | 149.0 ms | 35.8 MiB | 34.8 MiB |
| churn | .NET | 107.4 ms | 61.1 MiB | 44.2 MiB |

The ARC traffic did not change. The churn workload recorded 9,200,856 retains
and 10,601,216 releases per run, and the retained workload recorded 3,200,351
retains and 3,600,456 releases, the same counts as M4. None of the three
workloads throws, so none of them takes an unwind path.

What did change is the compiled code. Every call, and every null, bounds and
division check, is now followed by a load and a compare of the pending-exception
flag. Compared with M4, the ArcSharp wall time was 0.8 ms higher on the
baseline, 1.0 ms lower on the retained set, and 1.6 ms higher on the churn
program. The machine shows run-to-run differences of a few milliseconds, so
these differences are not evidence of a cost or of a gain. Memory use is within
0.1 MiB of M4 on every row.

The churn sampler saw both .NET pauses in three of the five runs, and the median
total for those three was 21.517 ms. The other two runs saw only the second
pause, at 15.448 ms and 15.491 ms. The median of the longest pause in each run
was 16.153 ms. ArcSharp has no collector, so it has no pause to report.

## What the original numbers support

**The empty process is smaller.** The ArcSharp baseline commits 0.6 MiB and
touches 3.9 MiB of working set. The .NET baseline commits 6.3 MiB and touches
23.1 MiB. Most of that is the runtime and its libraries. It also finishes later
(61 ms against 15 ms), because it has to start the runtime and JIT the
method. This is a property of shipping a small native runtime. It is already
true before any object is allocated.

**Peak working set was lower for ArcSharp on all three programs.** The gap is
19 MiB on the empty program, 10 MiB on the retained set, and 25 MiB on the
churn program.

**Private commit tells a more careful story.** Subtracting the baseline, the
retained set costs ArcSharp about 22.7 MiB and .NET about 12.4 MiB. A live
object is more expensive under ArcSharp: the header is 24 bytes and every
field occupies an 8-byte slot, and the C allocator adds its own overhead. The
.NET object is smaller, and 400,000 of them fitted in the heap without a
collection. Holding a large stable graph uses less private memory on .NET.

**Dropping objects promptly is where ArcSharp's peak stays down.** From the
retained set to the churn program, ArcSharp's private commit rises by 11.3
MiB, which is one batch living beside the retained set. .NET's private commit
rises by 25.5 MiB. The collector kept the heap committed after the batches
became garbage: at exit it still reported 38.3 MiB committed for a heap of
17.4 MiB. Absolute peak commit on the churn program is 34.7 MiB for ArcSharp
and 44.2 MiB for .NET.

**There were no ArcSharp pauses, and the .NET pauses were real.** Two
suspensions, the longer of them about 15 ms, fell inside a 91 ms process.
That is the latency claim, and this run supports it. It does not, by itself,
make the program faster. The churn program still finished in 138 ms under
ArcSharp and 91 ms under .NET. The retained set was a tie, at about 60 ms.
The original churn run performed 9.6 million retains. The
[M3 lifetime pass](../roadmap.md#m3--lifetime-pass) reduced that to 9.2
million in the five-run comparison above.

## Other consequences, not measured here

These follow from the memory model. This run does not add evidence for them.

- **Destruction is tied to the last release.** For an acyclic object that is
  already true: the batch dies when the local is overwritten, on that thread,
  and the accounting line reaches `live=0`. A user finaliser (`~T()`) now runs
  at that point, as [M4](../roadmap.md#m4--deterministic-finalisers) specifies.
  The workloads here declare none, so the M4 table above measures only the
  cost of the new destruction path.
- **A cycle leaks.** The workloads are acyclic on purpose. `ARC3100` warns, the
  program stays legal, and an ignored warning leaks. The .NET collector would
  reclaim the same cycle. There is no backup collector.
- **The heap is not compacted.** `malloc` returns blocks to its free list and
  does not slide live objects together. A long-running program can keep a high
  resident size after its live set has shrunk. A short run will not show that.
  The .NET collector can compact, which is an advantage in the other direction
  over a long run.
- **The runtime is single-threaded.** The pauses here are workstation pauses
  on one heap. Server GC, and a program with many threads, are outside this
  comparison and outside the current compatibility target.
- **Finalisers and `using`.** `Dispose()` runs when a `using` block completes
  normally. That is already deterministic. It is a different mechanism from a
  finaliser, and this run does not exercise it.

## Repeating the run

```powershell
powershell -ExecutionPolicy Bypass -File tools\compare-gc.ps1 -Runs 5
```

`-Workload baseline`, `retained`, or `churn` runs one program. The script
needs the .NET 11 SDK and `clang`. It builds the compiler, builds each .NET
program, emits LLVM IR, and links with `clang -O2`.

At the end of every milestone in the [roadmap](../roadmap.md), run the same
command and record the new medians beside the table above. A slower result does not
fail those milestones. The milestones fail or pass on their own definitions
of done. The comparison only says whether the memory and pause claims still
hold, and whether the retain traffic has come down.
