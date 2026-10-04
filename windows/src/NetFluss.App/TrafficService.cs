// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows.Threading;
using NetFluss.Core;
using NetFluss.Native;

namespace NetFluss.App;

/// <summary>Whether per-app traffic can be shown, and if not, why not.</summary>
internal enum TrafficAvailability
{
    /// <summary>Nothing has asked for it yet.</summary>
    Idle,

    /// <summary>Sampling, from the helper or an in-process trace.</summary>
    Running,

    /// <summary>
    /// The helper service is not installed and this process cannot trace on its own. Shown
    /// as an offer to install it, the way macOS offers its privileged helper.
    /// </summary>
    NeedsHelper,

    /// <summary>The helper is installed but older than this app; it needs reinstalling.</summary>
    HelperOutdated,

    /// <summary>The helper is there but Windows refused it the trace — reported, not hidden.</summary>
    HelperFailed,
}

/// <summary>One sampling interval of per-process traffic, names resolved.</summary>
internal sealed record TrafficSample(
    TimeSpan Elapsed,
    IReadOnlyList<ProcessBytes> Processes,
    IReadOnlyDictionary<FlowKey, (long Received, long Sent)> Flows,
    IReadOnlyDictionary<int, string> ProcessNames);

/// <summary>
/// Per-process traffic for Top Apps, the Network Slice and app statistics.
///
/// <para><b>Demand-driven.</b> Each consumer takes a token while it is on screen (or, for
/// statistics, while collection is switched on) and disposes it when it is not. The trace
/// runs only while at least one token is held: the Kernel-Network provider reports every
/// send and receive, and paying for that while the popover is closed would repeat the
/// energy mistake the Mac app spent a release fixing.</para>
///
/// <para><b>Sources.</b> The helper service when it is installed — it runs the trace as
/// LocalSystem, which is the only identity Windows lets enable this provider by default.
/// Failing that, an in-process trace, which works when NetFluss itself was started as
/// administrator. Failing both, <see cref="TrafficAvailability.NeedsHelper"/>.</para>
/// </summary>
internal sealed class TrafficService : IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _timer;
    private readonly ProcessNames _names = new();
    private readonly HelperClient _helper;
    private KernelNetworkTrace? _local;
    private int _demand;
    private bool _helperStreaming;
    private bool _localUnavailable;

    internal TrafficService(HelperClient helper)
    {
        _helper = helper;
        _helper.TrafficReceived += OnHelperTraffic;
        _helper.ConnectionChanged += (_, _) => Reevaluate();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SampleInterval };
        _timer.Tick += (_, _) => SampleLocal();
    }

    /// <summary>Raised on the UI thread once per interval while running.</summary>
    internal event EventHandler<TrafficSample>? Sampled;

    internal event EventHandler? AvailabilityChanged;

    internal TrafficAvailability Availability { get; private set; } = TrafficAvailability.Idle;

    private readonly Dictionary<string, DateTime> _recentNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Apps that moved data in the last minute, for the "hide an app" picker — the Mac's
    /// "Apps that used bandwidth in the last 60 seconds".
    /// </summary>
    internal IReadOnlyList<string> RecentAppNames()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-60);
        return [.. _recentNames.Where(p => p.Value >= cutoff).Select(p => p.Key).Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    private void Remember(TrafficSample sample)
    {
        var now = DateTime.UtcNow;
        foreach (var process in sample.Processes)
        {
            if (process.Received + process.Sent > 0)
            {
                _recentNames[process.Name] = now;
            }
        }

        if (_recentNames.Count > 512)
        {
            foreach (var stale in _recentNames.Where(p => p.Value < now.AddMinutes(-10)).Select(p => p.Key).ToList())
            {
                _recentNames.Remove(stale);
            }
        }
    }

    /// <summary>Takes a share of the demand; sampling runs while any share is held.</summary>
    internal IDisposable Acquire()
    {
        _demand++;
        Reevaluate();
        return new Lease(this);
    }

    private void Release()
    {
        _demand = Math.Max(0, _demand - 1);
        Reevaluate();
    }

    private void Reevaluate()
    {
        if (_demand == 0)
        {
            StopAll();
            SetAvailability(TrafficAvailability.Idle);
            return;
        }

        // The helper wins whenever it is reachable: it can see every process, including
        // elevated ones an in-process trace in an unelevated session could not name.
        if (_helper.IsConnected)
        {
            StopLocal();
            if (!_helper.IsCurrentVersion)
            {
                SetAvailability(TrafficAvailability.HelperOutdated);
                return;
            }

            if (!_helperStreaming)
            {
                _helperStreaming = true;
                _helper.Subscribe();
            }

            SetAvailability(_helper.HelperTraceStatus is "AccessDenied" or "Failed"
                ? TrafficAvailability.HelperFailed
                : TrafficAvailability.Running);
            return;
        }

        _helperStreaming = false;

        if (_local is null && !_localUnavailable)
        {
            var trace = new KernelNetworkTrace();
            if (trace.Start() == TraceStatus.Running)
            {
                _local = trace;
                _timer.Start();
            }
            else
            {
                // A process's elevation cannot change while it runs, so a refusal now is a
                // refusal for good. Retrying on every popover open would only churn ETW
                // sessions.
                _localUnavailable = true;
                trace.Dispose();
            }
        }

        SetAvailability(_local is not null ? TrafficAvailability.Running : TrafficAvailability.NeedsHelper);

        // Keep trying the helper in the background: the user may be installing it right now
        // from the very button the NeedsHelper state shows.
        _helper.EnsureConnecting();
    }

    private void StopAll()
    {
        StopLocal();
        if (_helperStreaming)
        {
            _helperStreaming = false;
            _helper.Unsubscribe();
        }
    }

    private void StopLocal()
    {
        _timer.Stop();
        _local?.Dispose();
        _local = null;
    }

    private void SampleLocal()
    {
        if (_local is null)
        {
            return;
        }

        var snapshot = _local.TakeSnapshot();
        var processes = snapshot.ByProcess
            .Select(p => new ProcessBytes(p.Key, _names.Resolve(p.Key), p.Value.Received, p.Value.Sent))
            .ToList();

        var names = processes.ToDictionary(p => p.ProcessId, p => p.Name);
        foreach (var flow in snapshot.ByFlow.Keys)
        {
            names.TryAdd(flow.ProcessId, _names.Resolve(flow.ProcessId));
        }

        var sample = new TrafficSample(snapshot.Elapsed, processes, snapshot.ByFlow, names);
        Remember(sample);
        Sampled?.Invoke(this, sample);
    }

    private void OnHelperTraffic(object? sender, TrafficSample sample)
    {
        if (_helperStreaming)
        {
            Remember(sample);
            Sampled?.Invoke(this, sample);
        }
    }

    private void SetAvailability(TrafficAvailability value)
    {
        if (Availability == value)
        {
            return;
        }

        Availability = value;
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        StopAll();
        _helper.TrafficReceived -= OnHelperTraffic;
    }

    private sealed class Lease(TrafficService owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            owner.Release();
        }
    }
}
