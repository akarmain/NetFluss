// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using NetFluss.Core;
using Xunit;

namespace NetFluss.Core.Tests;

public class TrafficTimerTests
{
    private DateTimeOffset _now = new(2026, 10, 4, 20, 0, 0, TimeSpan.Zero);

    private TrafficTimer NewTimer() => new(() => _now);

    private static AdapterStatus Adapter(string id, ulong rx, ulong tx, bool tunnel = false, bool mirror = false) => new()
    {
        Id = id,
        DisplayName = id,
        Description = id,
        Type = AdapterType.Ethernet,
        IsTunnel = tunnel,
        IsNonInternet = mirror,
        IsUp = true,
        RxBytes = rx,
        TxBytes = tx,
    };

    [Fact]
    public void Counts_OnlyWhileRunning_AndFirstSampleIsABaseline()
    {
        var timer = NewTimer();
        timer.Ingest([Adapter("eth", 1_000, 1_000)], false);
        Assert.Equal(0UL, timer.TotalBytes);

        timer.Start();
        timer.Ingest([Adapter("eth", 1_000, 1_000)], false);
        timer.Ingest([Adapter("eth", 1_500, 1_100)], false);

        Assert.Equal(500UL, timer.DownloadBytes);
        Assert.Equal(100UL, timer.UploadBytes);
    }

    [Fact]
    public void Traffic_WhilePaused_IsNotCounted()
    {
        var timer = NewTimer();
        timer.Start();
        timer.Ingest([Adapter("eth", 0, 0)], false);
        timer.Ingest([Adapter("eth", 100, 0)], false);

        timer.Pause();
        timer.Ingest([Adapter("eth", 900, 0)], false);

        timer.Start();
        timer.Ingest([Adapter("eth", 900, 0)], false);
        timer.Ingest([Adapter("eth", 950, 0)], false);

        Assert.Equal(150UL, timer.DownloadBytes);
    }

    [Fact]
    public void Mirrors_AndExcludedTunnels_AreLeftOut()
    {
        var timer = NewTimer();
        timer.Start();
        timer.Ingest([Adapter("eth", 0, 0), Adapter("vpn", 0, 0, tunnel: true), Adapter("wfp", 0, 0, mirror: true)], excludeTunnels: true);
        timer.Ingest([Adapter("eth", 10, 0), Adapter("vpn", 50, 0, tunnel: true), Adapter("wfp", 10, 0, mirror: true)], excludeTunnels: true);

        Assert.Equal(10UL, timer.DownloadBytes);
    }

    [Fact]
    public void Elapsed_BanksRunningStretches()
    {
        var timer = NewTimer();
        timer.Start();
        _now = _now.AddSeconds(30);
        timer.Pause();
        _now = _now.AddMinutes(10);
        timer.Start();
        _now = _now.AddSeconds(15);

        Assert.Equal(TimeSpan.FromSeconds(45), timer.Elapsed());
    }

    [Fact]
    public void ARunningSession_IsSavedPaused_AndRestoredPaused()
    {
        var timer = NewTimer();
        timer.Start();
        timer.Ingest([Adapter("eth", 0, 0)], false);
        timer.Ingest([Adapter("eth", 77, 3)], false);
        _now = _now.AddSeconds(90);

        var saved = timer.Save();
        Assert.NotNull(saved);
        Assert.Equal(_now, saved.PausedAt);

        var restored = NewTimer();
        restored.Restore(saved);

        Assert.Equal(TrafficTimerState.Paused, restored.State);
        Assert.Equal(80UL, restored.TotalBytes);
        Assert.Equal(TimeSpan.FromSeconds(90), restored.Elapsed());
    }

    [Fact]
    public void Reset_ClearsEverything_AndSavesNothing()
    {
        var timer = NewTimer();
        timer.Start();
        timer.Reset();

        Assert.Equal(TrafficTimerState.Idle, timer.State);
        Assert.Null(timer.StartedAt);
        Assert.Null(timer.Save());
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(247, "04:07")]
    [InlineData(3847, "1:04:07")]
    public void Clock_ReadsLikeAPhoneStopwatch(int seconds, string expected)
        => Assert.Equal(expected, TrafficTimer.ClockText(TimeSpan.FromSeconds(seconds)));
}
