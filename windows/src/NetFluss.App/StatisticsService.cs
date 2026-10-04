// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using NetFluss.Core;

namespace NetFluss.App;

/// <summary>
/// Collects adapter history from the monitor's counters and answers the Statistics window
/// and the popover's Data Usage section. Port of the macOS <c>StatisticsManager</c>.
///
/// <para>Collection is a subtraction per adapter per tick on numbers the meter already has,
/// so it costs nothing measurable and runs whether or not anything is on screen — history
/// with holes in it whenever the popover was closed would be no history at all. Writing to
/// disk happens every five minutes and on exit, never per tick.</para>
/// </summary>
internal sealed class StatisticsService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly NetworkMonitorService _monitor;
    private readonly StatisticsStore _store;
    private readonly Dictionary<string, (ulong Rx, ulong Tx)> _previous = new(StringComparer.OrdinalIgnoreCase);
    private StatisticsStore? _demo;
    private DateTime _lastFlushCheck = DateTime.MinValue;

    internal StatisticsService(SettingsStore settings, NetworkMonitorService monitor)
    {
        _settings = settings;
        _monitor = monitor;
        _store = new StatisticsStore(StatisticsStore.DefaultPath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

        _monitor.Ticked += (_, _) => Ingest();
    }

    /// <summary>Raised after each tick that recorded something, for the live Data Usage block.</summary>
    internal event EventHandler? Recorded;

    /// <summary>
    /// Whether the sample-data preview can be offered: the Mac's NETFLUSS_SAMPLE_STATISTICS
    /// switch, kept as a developer affordance rather than a user setting.
    /// </summary>
    internal static bool DemoAvailable =>
        Environment.GetEnvironmentVariable("NETFLUSS_SAMPLE_STATISTICS") == "1";

    internal bool IsShowingDemo => _demo is not null;

    internal void SetDemo(bool enabled)
        => _demo = enabled ? new StatisticsStore(StatisticsDemoData.MakeArchive(DateTime.Now)) : null;

    internal UsageSummary Usage()
        => _settings.Settings.CollectStatistics
            ? _store.Usage(DateTime.Now, _settings.Settings.ExcludeTunnelAdapters)
            : UsageSummary.Empty;

    internal Task<StatisticsReport> ReportAsync(StatisticsRange range)
        => BuildAsync((store, apps) => store.Report(range, DateTime.Now, Names(), Hidden(), _settings.Settings.ExcludeTunnelAdapters, apps),
            RangeStart(range), DateTime.Now);

    internal Task<StatisticsReport> ReportAsync(DateTime start, DateTime end)
        => BuildAsync((store, apps) => store.Report(start, end, DateTime.Now, Names(), Hidden(), _settings.Settings.ExcludeTunnelAdapters, apps),
            start, end);

    /// <summary>Wipes the recorded adapter history.</summary>
    internal void Reset()
    {
        _store.Clear();
        _store.Flush(force: true, DateTime.Now);
    }

    internal void Flush() => _store.Flush(force: true, DateTime.Now);

    private async Task<StatisticsReport> BuildAsync(
        Func<StatisticsStore, IReadOnlyDictionary<string, TrafficAmounts>?, StatisticsReport> build,
        DateTime start,
        DateTime end)
    {
        var demo = _demo;

        // App rows come from Windows' own records unless the preview is showing, whose
        // generated archive carries its own.
        IReadOnlyDictionary<string, TrafficAmounts>? apps = null;
        if (demo is null && _settings.Settings.CollectAppStatistics)
        {
            apps = await WindowsAppUsage.QueryAsync(start, end);
        }
        else if (demo is null)
        {
            apps = new Dictionary<string, TrafficAmounts>();
        }

        return await Task.Run(() => build(demo ?? _store, apps));
    }

    private static DateTime RangeStart(StatisticsRange range)
    {
        var now = DateTime.Now;
        return range switch
        {
            StatisticsRange.LastHour => now.AddHours(-1),
            StatisticsRange.Last24Hours => now.AddHours(-24),
            StatisticsRange.Last7Days => now.Date.AddDays(-6),
            StatisticsRange.Last30Days => now.Date.AddDays(-29),
            _ => now.Date.AddDays(-364),
        };
    }

    private IReadOnlyDictionary<string, string> Names() => _settings.Settings.AdapterCustomNames;

    private ISet<string> Hidden() => _settings.Settings.HiddenApps.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Turns the monitor's cumulative counters into deltas. A counter that went backwards
    /// (adapter reset, sleep, a dock replugged) is a new baseline, not traffic — adding the
    /// whole counter would book days of bytes into one minute.
    /// </summary>
    private void Ingest()
    {
        var sample = _monitor.LastSample;
        var collecting = _settings.Settings.CollectStatistics;
        var deltas = new List<AdapterDelta>();

        foreach (var adapter in sample)
        {
            // Mirrors and loopback never carry internet traffic of their own; recording them
            // would double every number the moment a total is taken.
            if (adapter.IsNonInternet)
            {
                continue;
            }

            if (collecting && _previous.TryGetValue(adapter.Id, out var previous))
            {
                var rx = adapter.RxBytes >= previous.Rx ? adapter.RxBytes - previous.Rx : 0;
                var tx = adapter.TxBytes >= previous.Tx ? adapter.TxBytes - previous.Tx : 0;
                if (rx > 0 || tx > 0)
                {
                    var name = _settings.Settings.AdapterDisplayName(adapter.Id, adapter.DisplayName);
                    deltas.Add(new AdapterDelta(adapter.Id, name, adapter.IsTunnel, rx, tx));
                }
            }

            _previous[adapter.Id] = (adapter.RxBytes, adapter.TxBytes);
        }

        if (deltas.Count == 0)
        {
            return;
        }

        var now = DateTime.Now;
        _store.RecordAdapterDeltas(deltas, now);

        if (now - _lastFlushCheck >= TimeSpan.FromMinutes(1))
        {
            _lastFlushCheck = now;
            _ = Task.Run(() => _store.Flush(force: false, now));
        }

        Recorded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Flush();
}
