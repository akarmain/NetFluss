// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NetFluss.Core;
using Windows.Networking.Connectivity;

namespace NetFluss.App;

/// <summary>
/// Per-app traffic history from Windows' own data-usage records — the numbers behind
/// Settings → Network &amp; internet → Data usage.
///
/// <para><b>Why this and not a trace.</b> The macOS app samples <c>netstat</c> every twenty
/// seconds in the background to build its app history. The Windows trace needs the helper
/// and would have to run all day for the same purpose. Windows already records per-app
/// traffic for every connection profile, unprivileged, at no cost to anyone — and has been
/// doing so since before NetFluss was installed, so app statistics have history from the
/// first launch.</para>
///
/// <para><b>The trade-off is freshness.</b> Windows flushes these records periodically
/// rather than live: a download from the last few minutes may not appear yet. That is
/// fine for history and is why live Top Apps uses the trace instead.</para>
/// </summary>
internal static class WindowsAppUsage
{
    private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bytes per app name for the span, summed across every network the PC has used.</summary>
    internal static async Task<IReadOnlyDictionary<string, TrafficAmounts>> QueryAsync(DateTime start, DateTime end)
    {
        var totals = new Dictionary<string, TrafficAmounts>(StringComparer.OrdinalIgnoreCase);
        var states = new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare };

        IReadOnlyList<ConnectionProfile> profiles;
        try
        {
            profiles = NetworkInformation.GetConnectionProfiles();
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException)
        {
            return totals;
        }

        // Profiles that share an underlying network report the same records; one entry per
        // profile name avoids counting them twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            if (!seen.Add(profile.ProfileName ?? string.Empty))
            {
                continue;
            }

            IReadOnlyList<AttributedNetworkUsage> usage;
            try
            {
                usage = await profile.GetAttributedNetworkUsageAsync(
                    new DateTimeOffset(start),
                    new DateTimeOffset(end),
                    states);
            }
            catch (Exception e) when (e is COMException or UnauthorizedAccessException or ArgumentException)
            {
                // A profile for a network long gone can refuse; the rest still count.
                continue;
            }

            foreach (var entry in usage)
            {
                var name = NameFor(entry.AttributionId, entry.AttributionName);
                if (!totals.TryGetValue(name, out var amounts))
                {
                    amounts = new TrafficAmounts();
                    totals[name] = amounts;
                }

                amounts.Add(entry.BytesReceived, entry.BytesSent);
            }
        }

        return totals;
    }

    /// <summary>
    /// The same name Top Apps would show, so an app reads identically live and in history.
    /// Desktop apps arrive as an NT device path and an empty name; packaged apps carry their
    /// own display name.
    /// </summary>
    private static string NameFor(string? id, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return "System";
        }

        lock (NameCache)
        {
            if (NameCache.TryGetValue(id, out var cached))
            {
                return cached;
            }
        }

        var path = DevicePathToDos(id);
        var resolved = path is not null && File.Exists(path) ? FriendlyName(path) : Path.GetFileNameWithoutExtension(id);
        if (string.IsNullOrEmpty(resolved))
        {
            resolved = id;
        }

        lock (NameCache)
        {
            NameCache[id] = resolved;
        }

        return resolved;
    }

    private static string FriendlyName(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var description = info.FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description) && description.Length <= 48 &&
                !fileName.Equals("svchost", StringComparison.OrdinalIgnoreCase))
            {
                return description;
            }
        }
        catch (FileNotFoundException)
        {
        }

        return fileName;
    }

    /// <summary>"\device\harddiskvolume3\…" → "C:\…" by asking each drive letter what it maps to.</summary>
    private static string? DevicePathToDos(string devicePath)
    {
        if (!devicePath.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase))
        {
            return devicePath;
        }

        foreach (var drive in Environment.GetLogicalDrives())
        {
            var letter = drive.TrimEnd('\\');
            var target = new StringBuilder(260);
            if (QueryDosDevice(letter, target, target.Capacity) == 0)
            {
                continue;
            }

            var device = target.ToString();
            if (devicePath.StartsWith(device + @"\", StringComparison.OrdinalIgnoreCase))
            {
                return letter + devicePath[device.Length..];
            }
        }

        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int max);
}
