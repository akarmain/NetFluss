// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace NetFluss.Core;

/// <summary>
/// Generated traffic for previewing the Network Slice without the helper service — the
/// counterpart of <see cref="StatisticsDemoData"/>. Documentation addresses only
/// (RFC 5737 / RFC 3849), so nothing here names a real machine.
/// </summary>
public sealed class NetworkSliceDemo
{
    private sealed record Source(string Process, string Protocol, string Remote, int RemotePort, int LocalPort, double Rx, double Tx, string? Name, string? Country);

    private static readonly Source[] Sources =
    [
        new("chrome", "tcp", "203.0.113.14", 443, 51234, 2_400_000, 120_000, "video-cdn.example", "DE"),
        new("chrome", "udp", "203.0.113.14", 443, 51240, 1_100_000, 60_000, "video-cdn.example", "DE"),
        new("chrome", "tcp", "198.51.100.7", 443, 51302, 180_000, 22_000, "news.example", "NL"),
        new("OneDrive", "tcp", "198.51.100.40", 443, 52010, 40_000, 900_000, "storage.example", "IE"),
        new("Teams", "udp", "203.0.113.90", 3478, 50020, 160_000, 150_000, "media.example", "US"),
        new("Spotify", "tcp", "198.51.100.120", 443, 53122, 210_000, 6_000, "audio.example", "SE"),
        new("svchost", "udp", "192.168.1.1", 53, 61200, 3_000, 1_500, "router.local", null),
        new("svchost", "udp", "239.255.255.250", 1900, 1900, 0, 2_200, null, null),
        new("steam", "tcp", "203.0.113.200", 27036, 49880, 600_000, 15_000, "content.example", "AT"),
        new("Code", "tcp", "198.51.100.66", 443, 54001, 35_000, 9_000, "marketplace.example", "US"),
        new("System", "tcp", "192.168.1.20", 445, 50110, 90_000, 400_000, null, null),
        new("ms-teams", "tcp", "203.0.113.91", 443, 55011, 25_000, 18_000, "chat.example", "US"),
    ];

    private readonly Random _random = new(7);
    private int _tick;

    /// <summary>Names and countries the lookups would have produced, applied up front.</summary>
    public void Prime(NetworkSlice slice)
    {
        foreach (var source in Sources)
        {
            slice.SetHostname(source.Remote, source.Name);
            slice.SetCountry(source.Remote, source.Country);
        }
    }

    /// <summary>One interval of flows; bursts come and go so the live view differs from the totals.</summary>
    public IReadOnlyList<SliceFlow> Next(TimeSpan elapsed)
    {
        _tick++;
        var seconds = elapsed.TotalSeconds;
        var flows = new List<SliceFlow>();
        for (var i = 0; i < Sources.Length; i++)
        {
            // Each source is quiet some of the time, on its own rhythm.
            if ((_tick + i) % (3 + (i % 4)) == 0)
            {
                continue;
            }

            var source = Sources[i];
            var wobble = 0.55 + _random.NextDouble() * 0.9;
            flows.Add(new SliceFlow(
                source.Process,
                source.Protocol,
                "192.168.1.104",
                source.LocalPort,
                source.Remote,
                source.RemotePort,
                (long)(source.Rx * wobble * seconds),
                (long)(source.Tx * wobble * seconds)));
        }

        return flows;
    }
}
