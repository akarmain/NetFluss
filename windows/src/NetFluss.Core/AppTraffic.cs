// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace NetFluss.Core;

/// <summary>One app's current traffic. Port of the macOS <c>AppTraffic</c>.</summary>
public sealed record AppTraffic(string Id, string Name, double RxRateBps, double TxRateBps)
{
    public double Total => RxRateBps + TxRateBps;
}

/// <summary>Bytes one process moved over a sampling interval, with the name it should be shown under.</summary>
public readonly record struct ProcessBytes(int ProcessId, string Name, long Received, long Sent);

/// <summary>
/// Turns per-process byte counts into the Top Apps list: grouped by app, rates over the
/// interval, hidden apps removed, and a grace period so a quiet app does not vanish and
/// reappear between two bursts.
///
/// <para>Grouped by display name rather than by process because that is what a person
/// means by "an app": Chrome is a dozen processes and one row. macOS groups the same way,
/// by the name netstat reports.</para>
/// </summary>
public sealed class TopAppsAggregator
{
    /// <summary>How many rows the popover shows, as on macOS.</summary>
    public const int RowCount = 5;

    private readonly Dictionary<string, (DateTimeOffset LastActive, double Rx, double Tx)> _recent =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything seen recently, for the Preferences "hide an app" picker.</summary>
    public IReadOnlyCollection<string> RecentNames => _recent.Keys;

    public IReadOnlyList<AppTraffic> Update(
        IEnumerable<ProcessBytes> processes,
        TimeSpan elapsed,
        DateTimeOffset now,
        ISet<string> hidden,
        TimeSpan? grace)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);

        var grouped = processes
            .Where(p => p.Received > 0 || p.Sent > 0)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppTraffic(
                g.Key,
                g.First().Name,
                g.Sum(p => p.Received) / seconds,
                g.Sum(p => p.Sent) / seconds))
            .ToList();

        foreach (var app in grouped)
        {
            _recent[app.Id] = (now, app.RxRateBps, app.TxRateBps);
        }

        // Forget names nothing has used for a while, so the picker does not grow forever.
        foreach (var stale in _recent.Where(p => now - p.Value.LastActive > TimeSpan.FromMinutes(10)).Select(p => p.Key).ToList())
        {
            _recent.Remove(stale);
        }

        if (grace is { } window)
        {
            // A recently active app that moved nothing this interval stays listed, at zero,
            // until its grace period runs out — a list that reshuffles every second is
            // unreadable, and that is what the Mac's grace option exists to stop.
            var present = grouped.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, state) in _recent)
            {
                if (!present.Contains(id) && now - state.LastActive <= window)
                {
                    grouped.Add(new AppTraffic(id, id, 0, 0));
                }
            }
        }

        return
        [
            .. grouped
                .Where(app => !hidden.Contains(app.Name))
                .OrderByDescending(app => app.Total)
                .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(RowCount),
        ];
    }

    public void Reset() => _recent.Clear();
}
