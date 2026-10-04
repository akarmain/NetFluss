// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;

namespace NetFluss.Core;

/// <summary>Frequency band of a Wi-Fi channel.</summary>
public enum WifiBand
{
    Band2GHz,
    Band5GHz,
    Band6GHz,
}

/// <summary>
/// The connected network's radio details, shown by the (i) button on a Wi-Fi adapter card.
/// Port of the macOS <c>WifiDetail</c>.
///
/// <para><b>No noise or SNR on Windows.</b> CoreWLAN reports the noise floor; the Native
/// Wifi API does not expose it anywhere, so those two rows are absent rather than faked.</para>
/// </summary>
public sealed record WifiDetail
{
    public string? Ssid { get; init; }

    public string? Bssid { get; init; }

    /// <summary>"Wi-Fi 6 (802.11ax)" and friends, worded exactly as the Mac shows them.</summary>
    public string? PhyMode { get; init; }

    public string? Security { get; init; }

    public int? Channel { get; init; }

    public WifiBand? Band { get; init; }

    /// <summary>Received signal strength in dBm.</summary>
    public int? Rssi { get; init; }

    /// <summary>Windows' own 0–100 link quality, which it derives from RSSI.</summary>
    public int? SignalQuality { get; init; }

    public double? TxRateMbps { get; init; }

    public double? RxRateMbps { get; init; }

    /// <summary>The Windows profile the connection was made with.</summary>
    public string? ProfileName { get; init; }
}

/// <summary>
/// One row of the Wi-Fi switcher. Port of the macOS <c>WifiNetwork</c>, including the
/// synthesised rows for pinned networks that are currently out of range.
/// </summary>
public sealed record WifiNetwork
{
    public required string Ssid { get; init; }

    public string? Bssid { get; init; }

    public int? Rssi { get; init; }

    public int? SignalQuality { get; init; }

    public bool IsSecured { get; init; }

    public string? Security { get; init; }

    public int? Channel { get; init; }

    public WifiBand? Band { get; init; }

    public bool IsCurrent { get; init; }

    /// <summary>Windows already holds a profile for it, so joining needs no password.</summary>
    public bool IsSaved { get; init; }

    public bool IsPinned { get; init; }

    /// <summary>False for a pinned network synthesised because it is out of range.</summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>The Windows profile to connect with, when one exists.</summary>
    public string? ProfileName { get; init; }

    /// <summary>Raw DOT11_AUTH_ALGORITHM, needed to build a profile for a new network.</summary>
    public uint AuthAlgorithm { get; init; }

    /// <summary>Raw DOT11_CIPHER_ALGORITHM, needed to build a profile for a new network.</summary>
    public uint CipherAlgorithm { get; init; }

    public string Id => Ssid;
}

/// <summary>
/// Pure functions behind the Wi-Fi UI: channel arithmetic, labels, and list ordering.
/// Kept out of the interop layer so they are testable without a radio.
/// </summary>
public static class WifiFormat
{
    /// <summary>Band for a centre frequency in MHz, or null when it is none of the three.</summary>
    public static WifiBand? BandFromFrequency(int megahertz) => megahertz switch
    {
        >= 2400 and < 2500 => WifiBand.Band2GHz,
        >= 4900 and < 5925 => WifiBand.Band5GHz,
        >= 5925 and <= 7125 => WifiBand.Band6GHz,
        _ => null,
    };

    /// <summary>IEEE channel number for a centre frequency in MHz.</summary>
    public static int? ChannelFromFrequency(int megahertz)
    {
        if (megahertz == 2484)
        {
            return 14;
        }

        if (megahertz is >= 2412 and < 2484)
        {
            return (megahertz - 2407) / 5;
        }

        // 5935 MHz is the one 6 GHz channel that breaks the arithmetic (channel 2).
        if (megahertz == 5935)
        {
            return 2;
        }

        if (megahertz is >= 5950 and <= 7125)
        {
            return (megahertz - 5950) / 5;
        }

        if (megahertz is >= 4900 and < 5925)
        {
            return (megahertz - 5000) / 5;
        }

        return null;
    }

    public static string BandLabel(WifiBand band) => band switch
    {
        WifiBand.Band2GHz => "2.4 GHz",
        WifiBand.Band5GHz => "5 GHz",
        WifiBand.Band6GHz => "6 GHz",
        _ => string.Empty,
    };

    /// <summary>
    /// DOT11_PHY_TYPE to the label macOS shows. "Wi-Fi 6E" is the marketing name for
    /// 802.11ax on 6 GHz, so the band is consulted for that one case.
    /// </summary>
    public static string? PhyLabel(uint phyType, WifiBand? band) => phyType switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "Wi-Fi 4 (802.11n)",
        8 => "Wi-Fi 5 (802.11ac)",
        10 => band == WifiBand.Band6GHz ? "Wi-Fi 6E (802.11ax)" : "Wi-Fi 6 (802.11ax)",
        11 => "Wi-Fi 7 (802.11be)",
        _ => null,
    };

    /// <summary>
    /// DOT11_AUTH_ALGORITHM (and the cipher, for WEP) to the macOS security labels.
    /// </summary>
    public static string SecurityLabel(uint authAlgorithm, uint cipherAlgorithm, bool securityEnabled = true)
    {
        // WEP rides on "open" or "shared key" authentication; only the cipher says so.
        const uint CipherWep40 = 0x01;
        const uint CipherWep104 = 0x05;
        const uint CipherWep = 0x101;

        return authAlgorithm switch
        {
            1 when cipherAlgorithm is CipherWep40 or CipherWep104 or CipherWep => "WEP",
            1 => securityEnabled && cipherAlgorithm != 0 ? "WEP" : "Open",
            2 => "WEP",
            3 => "WPA Enterprise",
            4 => "WPA Personal",
            5 => "WPA",
            6 => "WPA2 Enterprise",
            7 => "WPA2 Personal",
            8 => "WPA3 Enterprise",
            9 => "WPA3 Personal",
            10 => "Enhanced Open",
            11 => "WPA3 Enterprise",
            _ => "Unknown",
        };
    }

    /// <summary>Whether a network needs credentials to join.</summary>
    public static bool IsSecured(uint authAlgorithm, uint cipherAlgorithm)
        => !(authAlgorithm == 1 && cipherAlgorithm == 0) && authAlgorithm != 10;

    /// <summary>
    /// Windows' signal quality is a linear map of RSSI from -100 dBm (0) to -50 dBm (100).
    /// Used only when the driver does not report RSSI directly.
    /// </summary>
    public static int RssiFromQuality(int quality) => (Math.Clamp(quality, 0, 100) / 2) - 100;

    /// <summary>0–3 signal bars, matching the thresholds the Mac uses for its opacity steps.</summary>
    public static int SignalBars(int? rssi) => rssi switch
    {
        null => 0,
        < -80 => 1,
        < -70 => 2,
        < -60 => 3,
        _ => 4,
    };

    /// <summary>"Ch 36" or "Ch 36 (5 GHz)" for the detail rows.</summary>
    public static string ChannelLabel(int channel, WifiBand? band)
        => band is { } b
            ? string.Create(CultureInfo.InvariantCulture, $"Ch {channel} ({BandLabel(b)})")
            : string.Create(CultureInfo.InvariantCulture, $"Ch {channel}");

    /// <summary>A BSSID in the colon-separated lower-case form macOS prints.</summary>
    public static string FormatBssid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6)
        {
            return string.Empty;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{bytes[0]:x2}:{bytes[1]:x2}:{bytes[2]:x2}:{bytes[3]:x2}:{bytes[4]:x2}:{bytes[5]:x2}");
    }

    /// <summary>
    /// Orders scan results the way the Mac popover does: the current network, then pinned
    /// ones (in the user's pin order), then everything else strongest first. Pinned SSIDs
    /// that were not seen in the scan are synthesised as unavailable rows so they stay put
    /// while out of range, which is the whole point of pinning.
    /// </summary>
    public static IReadOnlyList<WifiNetwork> Arrange(
        IReadOnlyList<WifiNetwork> scanned,
        IReadOnlyList<string> pinned)
    {
        var bySsid = new Dictionary<string, WifiNetwork>(StringComparer.Ordinal);
        foreach (var network in scanned)
        {
            if (string.IsNullOrEmpty(network.Ssid))
            {
                continue;
            }

            // One row per SSID: a mesh shows the same name from several access points, and
            // listing each would offer the user five identical choices.
            if (!bySsid.TryGetValue(network.Ssid, out var existing))
            {
                bySsid[network.Ssid] = network;
                continue;
            }

            var better = (network.IsCurrent && !existing.IsCurrent) ||
                         (existing.IsCurrent == network.IsCurrent && (network.Rssi ?? -200) > (existing.Rssi ?? -200));

            var winner = better ? network : existing;
            var loser = better ? existing : network;

            // Whichever row wins, "Windows knows this network" is true if either said so.
            bySsid[network.Ssid] = winner with
            {
                IsSaved = winner.IsSaved || loser.IsSaved,
                ProfileName = winner.ProfileName ?? loser.ProfileName,
            };
        }

        var pinnedSet = new HashSet<string>(pinned, StringComparer.Ordinal);
        var result = new List<WifiNetwork>();

        var current = bySsid.Values.FirstOrDefault(n => n.IsCurrent);
        if (current is not null)
        {
            result.Add(current with { IsPinned = pinnedSet.Contains(current.Ssid) });
        }

        foreach (var ssid in pinned)
        {
            if (current is not null && current.Ssid == ssid)
            {
                continue;
            }

            result.Add(bySsid.TryGetValue(ssid, out var seen)
                ? seen with { IsPinned = true }
                : new WifiNetwork { Ssid = ssid, IsPinned = true, IsAvailable = false, IsSaved = true });
        }

        result.AddRange(bySsid.Values
            .Where(n => !n.IsCurrent && !pinnedSet.Contains(n.Ssid))
            .OrderByDescending(n => n.Rssi ?? -200)
            .ThenBy(n => n.Ssid, StringComparer.CurrentCultureIgnoreCase));

        return result;
    }

    /// <summary>
    /// The "only show the N strongest" cap. Pinned and current rows always show; the limit
    /// applies to the rest, exactly as on macOS.
    /// </summary>
    public static IReadOnlyList<WifiNetwork> ApplyLimit(IReadOnlyList<WifiNetwork> networks, bool enabled, int count)
    {
        if (!enabled)
        {
            return networks;
        }

        var remaining = Math.Max(1, count);
        var kept = new List<WifiNetwork>();
        foreach (var network in networks)
        {
            if (network.IsPinned || network.IsCurrent)
            {
                kept.Add(network);
            }
            else if (remaining > 0)
            {
                kept.Add(network);
                remaining--;
            }
        }

        return kept;
    }
}
