// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace NetFluss.Core;

/// <summary>
/// The customisable sections of the popover. Port of the macOS <c>PopoverSection</c>; the
/// stored ids are the Mac raw values so a settings export reads the same on both platforms.
/// </summary>
public enum PopoverSection
{
    Totals,
    Usage,
    Adapters,
    Connection,
    Dns,
    Router,
    Wifi,
    Vpn,
    TopApps,
    Timer,
}

public static class PopoverSections
{
    /// <summary>The macOS default top-to-bottom order.</summary>
    public static readonly IReadOnlyList<PopoverSection> DefaultOrder =
    [
        PopoverSection.Totals,
        PopoverSection.Adapters,
        PopoverSection.Connection,
        PopoverSection.Dns,
        PopoverSection.Router,
        PopoverSection.Wifi,
        PopoverSection.Vpn,
        PopoverSection.TopApps,
        PopoverSection.Usage,
        PopoverSection.Timer,
    ];

    /// <summary>The stored id — the macOS raw value.</summary>
    public static string Id(this PopoverSection section) => section switch
    {
        PopoverSection.Totals => "totals",
        PopoverSection.Usage => "usage",
        PopoverSection.Adapters => "adapters",
        PopoverSection.Connection => "connection",
        PopoverSection.Dns => "dns",
        PopoverSection.Router => "router",
        PopoverSection.Wifi => "wifi",
        PopoverSection.Vpn => "vpn",
        PopoverSection.TopApps => "topApps",
        PopoverSection.Timer => "timer",
        _ => section.ToString(),
    };

    /// <summary>The English label, which is also the localization key on both platforms.</summary>
    public static string DisplayKey(this PopoverSection section) => section switch
    {
        PopoverSection.Totals => "Download / Upload",
        PopoverSection.Usage => "Data Usage",
        PopoverSection.Adapters => "Network adapters",
        PopoverSection.Connection => "Network flow",
        PopoverSection.Dns => "DNS",
        PopoverSection.Router => "Router",
        PopoverSection.Wifi => "Wi-Fi Networks",
        PopoverSection.Vpn => "VPN",
        PopoverSection.TopApps => "Top Apps",
        PopoverSection.Timer => "Traffic Timer",
        _ => section.ToString(),
    };

    public static PopoverSection? FromId(string id)
    {
        foreach (var section in Enum.GetValues<PopoverSection>())
        {
            if (string.Equals(section.Id(), id, StringComparison.Ordinal))
            {
                return section;
            }
        }

        return null;
    }

    /// <summary>
    /// The stored order made whole: unknown ids dropped, duplicates collapsed to their first
    /// position, and any section the stored list predates appended in default order.
    ///
    /// <para>Appending rather than resetting is the point. A settings file written before a
    /// section existed must not lose the user's arrangement the day that section ships —
    /// it should simply gain the new section at the end, which is what macOS does.</para>
    /// </summary>
    public static IReadOnlyList<PopoverSection> Resolve(IEnumerable<string>? stored)
    {
        var ordered = new List<PopoverSection>();
        var seen = new HashSet<PopoverSection>();

        foreach (var id in stored ?? [])
        {
            if (FromId(id) is { } section && seen.Add(section))
            {
                ordered.Add(section);
            }
        }

        foreach (var section in DefaultOrder)
        {
            if (seen.Add(section))
            {
                ordered.Add(section);
            }
        }

        return ordered;
    }
}

/// <summary>How the popover shows addresses. Mirrors the macOS <c>connectionStatusMode</c>.</summary>
public enum ConnectionDisplayMode
{
    /// <summary>A path from this PC through the router (and any VPN) to the internet.</summary>
    Flow,

    /// <summary>External, internal and router addresses as rows with copy buttons.</summary>
    List,

    /// <summary>No address section at all.</summary>
    None,
}
