using System;
using System.Globalization;
using System.IO;
using System.Threading;

// Records garbage-collector activity for the .NET run of a bench workload.
// The ArcSharp build does not include this file. A background thread polls
// GC.GetGCMemoryInfo because the runtime's GC events are not delivered to an
// in-process EventListener. The thread is part of the measured .NET process.
internal static class GcReport
{
    private static readonly PauseSampler Sampler = new PauseSampler();

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Start()
    {
        Sampler.Start();
        AppDomain.CurrentDomain.ProcessExit += delegate
        {
            Sampler.StopAndWrite();
        };
    }
}

internal sealed class PauseSampler
{
    private readonly object _gate = new object();
    private Thread _thread;
    private volatile bool _stop;
    private int _observed;
    private double _totalPauseMs;
    private double _maxPauseMs;
    private string _trace = "";

    internal void Start()
    {
        _thread = new Thread(Watch);
        _thread.IsBackground = true;
        _thread.Name = "gc-pause-sampler";
        _thread.Start();
    }

    internal void StopAndWrite()
    {
        _stop = true;
        if (_thread != null)
        {
            _thread.Join(1000);
        }

        int observed;
        double total;
        double max;
        string trace;
        lock (_gate)
        {
            observed = _observed;
            total = _totalPauseMs;
            max = _maxPauseMs;
            trace = _trace;
        }

        int count0 = GC.CollectionCount(0);
        int missed = count0 - observed;
        if (missed < 0)
        {
            missed = 0;
        }

        GCMemoryInfo info = GC.GetGCMemoryInfo();
        string line = string.Format(
            CultureInfo.InvariantCulture,
            "[gc] count0={0} count1={1} count2={2} observed={3} missed={4} maxPauseMs={5:0.000} totalPauseMs={6:0.000} heap={7} committed={8} fragmented={9} allocated={10} trace={11}",
            count0,
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            observed,
            missed,
            max,
            total,
            info.HeapSizeBytes,
            info.TotalCommittedBytes,
            info.FragmentedBytes,
            GC.GetTotalAllocatedBytes(false),
            trace.Length == 0 ? "none" : trace);

        string path = Environment.GetEnvironmentVariable("ARC_GC_REPORT");
        if (!string.IsNullOrEmpty(path))
        {
            File.WriteAllText(path, line + Environment.NewLine);
        }
    }

    private void Watch()
    {
        long index = -1;
        while (!_stop)
        {
            Observe(ref index);
            Thread.Sleep(1);
        }

        Observe(ref index);
    }

    private void Observe(ref long index)
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        if (info.Index == index)
        {
            return;
        }

        // Index 0 is the state before any collection. Its pause span can still
        // contain a zero duration, which is not a collection.
        if (info.Index == 0)
        {
            index = 0;
            return;
        }

        index = info.Index;
        double sampleMax = 0;
        double sampleTotal = 0;
        int count = 0;
        foreach (TimeSpan pause in info.PauseDurations)
        {
            double ms = pause.TotalMilliseconds;
            sampleTotal += ms;
            count++;
            if (ms > sampleMax)
            {
                sampleMax = ms;
            }
        }

        if (count == 0)
        {
            return;
        }

        lock (_gate)
        {
            _observed++;
            _totalPauseMs += sampleTotal;
            if (sampleMax > _maxPauseMs)
            {
                _maxPauseMs = sampleMax;
            }

            if (_trace.Length > 0)
            {
                _trace += ",";
            }

            _trace += string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1:0.000}/{2:0.000}",
                info.Index,
                sampleMax,
                sampleTotal);
        }
    }
}
