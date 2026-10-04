// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using NetFluss.Core;
using Xunit;

namespace NetFluss.Core.Tests;

public sealed class DashboardMeterTests
{
    [Fact]
    public void CompactTextsShareOneScale()
    {
        var metrics = new DashboardMetrics(11_000_000, 1_400_000, null, "local");
        Assert.Equal(("12.4", "11.0", "1.4"), metrics.CompactTexts(useBits: false));
        Assert.Equal("MB/s", metrics.Unit(useBits: false));
        Assert.Equal(("99.2", "88.0", "11.2"), metrics.CompactTexts(useBits: true));
        Assert.Equal("Mb/s", metrics.Unit(useBits: true));
    }

    [Fact]
    public void LargeValuesDropTheDecimal()
        => Assert.Equal(("910", "801", "109"), new DashboardMetrics(801_000, 109_000, null, "local").CompactTexts(false));

    [Fact]
    public void RouterMaximaBecomeBytes()
    {
        var metrics = DashboardMetrics.Router(new RouterBandwidth(1, 2, 250_000_000, 50_000_000), "fritzbox");
        Assert.Equal(37_500_000, metrics.MaxTotal);
        Assert.Equal("router:fritzbox", metrics.SourceKey);
        Assert.Null(DashboardMetrics.Router(new RouterBandwidth(1, 2, 0, 0), "openwrt").MaxTotal);
    }

    [Fact]
    public void RingAgainstALineSpeed()
    {
        var ring = new DashboardRing();
        Assert.Equal(0.5, ring.Progress(new DashboardMetrics(400, 100, 1000, "router:x"), DateTimeOffset.UnixEpoch));
        Assert.Equal(0.08, ring.Progress(new DashboardMetrics(1, 0, 1000, "router:x"), DateTimeOffset.UnixEpoch));
        Assert.Equal(0, ring.Progress(new DashboardMetrics(0, 0, 1000, "router:x"), DateTimeOffset.UnixEpoch));
        Assert.Equal(1, ring.Progress(new DashboardMetrics(5000, 0, 1000, "router:x"), DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void RingAgainstADecayingPeak()
    {
        var ring = new DashboardRing();
        var t = DateTimeOffset.UnixEpoch;
        Assert.Equal(1, ring.Progress(new DashboardMetrics(1000, 0, null, "local"), t));
        Assert.Equal(0.5, ring.Progress(new DashboardMetrics(500, 0, null, "local"), t), 3);

        // Ten quiet seconds later the peak has decayed to 0.92^10 of itself.
        var later = ring.Progress(new DashboardMetrics(434, 0, null, "local"), t.AddSeconds(10));
        Assert.True(later is > 0.99 and <= 1, later.ToString());

        // A new source starts its own peak.
        Assert.Equal(1, ring.Progress(new DashboardMetrics(10, 0, null, "router:x"), t.AddSeconds(11)));
    }
}
