// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace NetFluss.Core;

/// <summary>
/// A year of plausible traffic for previewing the Statistics window before any real history
/// exists — the macOS <c>StatisticsDemoData</c>, with Windows adapters and apps. Seeded, so
/// every preview (and every screenshot made from one) is the same.
/// </summary>
public static class StatisticsDemoData
{
    private sealed record Profile(string Id, string Name, double DownloadMb, double UploadMb, double WeekdayBias, double WeekendBias, bool IsTunnel = false);

    private static readonly Profile[] Adapters =
    [
        new("{demo-wifi}", "Wi-Fi", 6_400, 1_050, 1.08, 0.78),
        new("{demo-ethernet}", "Ethernet", 2_400, 760, 1.12, 0.42),
        new("{demo-vpn}", "Work VPN", 1_600, 950, 1.18, 0.18, IsTunnel: true),
        new("{demo-hyperv}", "vEthernet (WSL)", 600, 380, 1.06, 0.34),
        new("{demo-hotspot}", "Phone Hotspot", 360, 130, 0.48, 0.62),
    ];

    private static readonly Profile[] Apps =
    [
        new("edge", "Microsoft Edge", 1_300, 240, 1.04, 0.92),
        new("chrome", "Google Chrome", 1_050, 150, 0.92, 1.04),
        new("spotify", "Spotify", 670, 33, 0.88, 1.18),
        new("teams", "Microsoft Teams", 450, 410, 1.20, 0.28),
        new("zoom", "Zoom", 350, 490, 1.14, 0.26),
        new("onedrive", "Microsoft OneDrive", 290, 540, 0.94, 0.88),
        new("steam", "Steam", 900, 20, 0.62, 1.40),
        new("vscode", "Visual Studio Code", 320, 120, 1.12, 0.22),
        new("github", "GitHub Desktop", 245, 215, 1.10, 0.26),
        new("windowsupdate", "Windows Update", 390, 9, 0.78, 0.64),
        new("discord", "Discord", 210, 160, 0.70, 1.20),
        new("figma", "Figma", 205, 160, 1.06, 0.36),
    ];

    public static StatisticsArchive MakeArchive(DateTime now)
    {
        var random = new Random(20260501);
        var archive = new StatisticsArchive
        {
            CreatedAt = new DateTimeOffset(now.AddDays(-365)),
            LastAdapterSampleAt = new DateTimeOffset(now),
            LastAppSampleAt = new DateTimeOffset(now),
        };

        foreach (var adapter in Adapters)
        {
            archive.AdapterDisplayNames[adapter.Id] = adapter.Name;
            if (adapter.IsTunnel)
            {
                archive.AdapterTunnels.Add(adapter.Id);
            }
        }

        for (var day = 364; day >= 0; day--)
        {
            var date = now.Date.AddDays(-day);

            // The partial current day only gets the share of the day that has happened.
            var fraction = day == 0 ? Math.Max(0.05, now.TimeOfDay.TotalHours / 24) : 1;

            Fill(archive.AdapterDaily, StatisticsStore.DayKey(date), Adapters, date, fraction, random);
            Fill(archive.AppDaily, StatisticsStore.DayKey(date), Apps, date, fraction, random);
        }

        var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        for (var hour = 71; hour >= 0; hour--)
        {
            var date = hourStart.AddHours(-hour);
            var share = Diurnal(date.Hour) / 24;
            Fill(archive.AdapterHourly, StatisticsStore.HourKey(date), Adapters, date, share, random);
            Fill(archive.AppHourly, StatisticsStore.HourKey(date), Apps, date, share, random);
        }

        var minuteStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        for (var minute = 179; minute >= 0; minute--)
        {
            var date = minuteStart.AddMinutes(-minute);
            var share = Diurnal(date.Hour) / (24 * 60);

            // Bursts: most minutes are quiet, a few carry a download.
            var burst = random.NextDouble() < 0.12 ? 6 + (random.NextDouble() * 10) : 0.4 + random.NextDouble();
            Fill(archive.AdapterMinute, StatisticsStore.MinuteKey(date), Adapters, date, share * burst, random);
            Fill(archive.AppMinute, StatisticsStore.MinuteKey(date), Apps, date, share * burst, random);
        }

        return archive;
    }

    private static void Fill(
        Dictionary<string, Dictionary<string, TrafficAmounts>> storage,
        string key,
        Profile[] profiles,
        DateTime date,
        double fraction,
        Random random)
    {
        var bucket = new Dictionary<string, TrafficAmounts>(StringComparer.Ordinal);
        var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        // A slow seasonal swing, so the 1Y view has shape rather than flat noise.
        var season = 1 + (0.18 * Math.Sin(date.DayOfYear / 365.0 * Math.PI * 2));

        foreach (var profile in profiles)
        {
            var bias = weekend ? profile.WeekendBias : profile.WeekdayBias;
            var jitter = 0.55 + random.NextDouble();
            var scale = fraction * bias * season * jitter * 1_000_000;

            var download = (ulong)Math.Max(0, profile.DownloadMb * scale);
            var upload = (ulong)Math.Max(0, profile.UploadMb * scale);
            if (download + upload > 0)
            {
                bucket[KeyFor(profile)] = new TrafficAmounts { DownloadBytes = download, UploadBytes = upload };
            }
        }

        storage[key] = bucket;
    }

    /// <summary>Adapters are keyed by id, apps by display name — as real history is.</summary>
    private static string KeyFor(Profile profile) => profile.Id.StartsWith('{') ? profile.Id : profile.Name;

    /// <summary>Relative activity by hour of day: a night trough, a working day, an evening peak.</summary>
    private static double Diurnal(int hour) => hour switch
    {
        < 6 => 0.15,
        < 9 => 0.7,
        < 12 => 1.25,
        < 14 => 1.0,
        < 18 => 1.3,
        < 23 => 1.6,
        _ => 0.5,
    };
}
