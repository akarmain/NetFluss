// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using NetFluss.Core;
using Xunit;

namespace NetFluss.Core.Tests;

public sealed class SpeedTestHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nf-speedtest-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "speedtest-history.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SpeedTestResult Result(int minutesAgo, double download = 100) => new()
    {
        Provider = SpeedTestProvider.Cloudflare,
        StartedAt = DateTimeOffset.Now.AddMinutes(-minutesAgo).AddSeconds(-20),
        FinishedAt = DateTimeOffset.Now.AddMinutes(-minutesAgo),
        DownloadMbps = download,
        UploadMbps = 20,
        LatencyMs = 9.5,
    };

    [Fact]
    public void NewestComesFirstAndSurvivesReload()
    {
        var history = new SpeedTestHistory(FilePath);
        history.Add(Result(10, 50));
        history.Add(Result(1, 75));

        var reloaded = new SpeedTestHistory(FilePath);
        Assert.Equal([75.0, 50.0], reloaded.Results.Select(r => r.DownloadMbps!.Value));
    }

    [Fact]
    public void KeepsOnlyTheLastThirty()
    {
        var history = new SpeedTestHistory(FilePath);
        for (var i = 40; i > 0; i--)
        {
            history.Add(Result(i, i));
        }

        Assert.Equal(SpeedTestHistory.MaximumCount, history.Results.Count);
        Assert.Equal(1, history.Results[0].DownloadMbps);
        Assert.Equal(SpeedTestHistory.MaximumCount, new SpeedTestHistory(FilePath).Results.Count);
    }

    [Fact]
    public void NotesAreTrimmedAndBlankRemovesThem()
    {
        var history = new SpeedTestHistory(FilePath);
        var result = Result(1);
        history.Add(result);

        Assert.True(history.SetNote(result.Id, "  Hotel Wi-Fi  "));
        Assert.Equal("Hotel Wi-Fi", new SpeedTestHistory(FilePath).Results[0].Note);

        Assert.True(history.SetNote(result.Id, "   "));
        Assert.Null(new SpeedTestHistory(FilePath).Results[0].Note);

        Assert.False(history.SetNote(Guid.NewGuid(), "x"));
    }

    [Fact]
    public void ProviderIsStoredWithTheMacRawValue()
    {
        var history = new SpeedTestHistory(FilePath);
        history.Add(Result(1) with { Provider = SpeedTestProvider.MLab });

        Assert.Contains("\"provider\": \"mlab\"", File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.Equal(SpeedTestProvider.MLab, new SpeedTestHistory(FilePath).Results[0].Provider);
    }

    [Fact]
    public void CorruptFileStartsEmpty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(new SpeedTestHistory(FilePath).Results);
    }

    [Fact]
    public void WithoutPathNothingIsWritten()
    {
        var history = new SpeedTestHistory(null);
        history.Add(Result(1));
        Assert.Single(history.Results);
        Assert.False(Directory.Exists(_directory));
    }
}
