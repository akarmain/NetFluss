// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;

namespace NetFluss.Core;

/// <summary>
/// What the Dashboard meter shows: download, upload, their sum, and — when a router reports
/// its line speed — the maximum the ring is measured against. Rates in bytes per second.
/// </summary>
/// <param name="SourceKey">"local" or "router:…": the ring's peak restarts when the source changes.</param>
public sealed record DashboardMetrics(double Rx, double Tx, double? MaxTotal, string SourceKey)
{
    public double Total => Math.Max(0, Rx) + Math.Max(0, Tx);

    /// <summary>This PC's own traffic.</summary>
    public static DashboardMetrics Local(RateTotals totals) => new(totals.RxRateBps, totals.TxRateBps, null, "local");

    /// <summary>A router's WAN traffic; its line maxima (bits) become the ring's reference.</summary>
    public static DashboardMetrics Router(RouterBandwidth bandwidth, string key)
    {
        var maxBits = bandwidth.MaxDownBits + bandwidth.MaxUpBits;
        return new DashboardMetrics(bandwidth.RxRate, bandwidth.TxRate, maxBits > 0 ? maxBits / 8.0 : null, "router:" + key);
    }

    /// <summary>
    /// The three numbers on one shared scale and without units, as the macOS dashboard
    /// prints them: "12.4", "11.0", "1.4". The scale is picked from the largest value, so
    /// the three always read against each other.
    /// </summary>
    public (string Total, string Down, string Up) CompactTexts(bool useBits)
    {
        var reference = Math.Max(Total, Math.Max(Rx, Tx));
        var scale = 0;
        var adjusted = Math.Max(0, useBits ? reference * 8 : reference);
        while (adjusted >= 1000 && scale < 4)
        {
            adjusted /= 1000;
            scale++;
        }

        string Text(double bytesPerSecond)
        {
            var value = Math.Max(0, useBits ? bytesPerSecond * 8 : bytesPerSecond) / Math.Pow(1000, scale);
            return value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture);
        }

        return (Text(Total), Text(Rx), Text(Tx));
    }

    /// <summary>The unit the compact numbers share, for a tooltip: "MB/s".</summary>
    public string Unit(bool useBits)
    {
        var reference = Math.Max(0, useBits ? Math.Max(Total, Math.Max(Rx, Tx)) * 8 : Math.Max(Total, Math.Max(Rx, Tx)));
        string[] units = useBits ? ["b/s", "Kb/s", "Mb/s", "Gb/s", "Tb/s"] : ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
        var scale = 0;
        while (reference >= 1000 && scale < 4)
        {
            reference /= 1000;
            scale++;
        }

        return units[scale];
    }
}

/// <summary>
/// The Dashboard ring's fill: against the router's line speed when known, otherwise against
/// a peak that decays by 8 % a second, so the ring shows "busy for this connection" rather
/// than sitting full or empty. Port of the macOS <c>ringProgress(for:)</c>.
/// </summary>
public sealed class DashboardRing
{
    private const double MinimumVisible = 0.08;

    private double _peak;
    private string? _source;
    private DateTimeOffset? _peakAt;

    public double Progress(DashboardMetrics metrics, DateTimeOffset now)
    {
        if (metrics.MaxTotal is > 0 and var reference)
        {
            return Clamp(metrics.Total / reference);
        }

        if (_source != metrics.SourceKey)
        {
            _source = metrics.SourceKey;
            _peak = Math.Max(metrics.Total, 1);
        }
        else if (_peakAt is { } last)
        {
            var elapsed = Math.Max((now - last).TotalSeconds, 0);
            _peak = Math.Max(Math.Max(metrics.Total, _peak * Math.Pow(0.92, elapsed)), 1);
        }
        else
        {
            _peak = Math.Max(Math.Max(metrics.Total, _peak), 1);
        }

        _peakAt = now;
        return Clamp(metrics.Total / Math.Max(_peak, 1));
    }

    private static double Clamp(double ratio) => ratio <= 0 ? 0 : Math.Min(Math.Max(ratio, MinimumVisible), 1);
}
