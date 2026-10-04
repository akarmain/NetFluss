// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using NetFluss.Core;

namespace NetFluss.App.Popover;

/// <summary>Everything a popover section may read or ask for.</summary>
internal sealed class PopoverContext
{
    internal required NetworkMonitorService Monitor { get; init; }

    internal required SettingsStore Store { get; init; }

    internal required WifiService Wifi { get; init; }

    internal required DnsSwitcher Dns { get; init; }

    internal required TrafficService Traffic { get; init; }

    internal required HelperClient Helper { get; init; }

    internal required PrivilegedActions Privileged { get; init; }

    internal required AppCommands Commands { get; init; }

    internal AppSettings Settings => Store.Settings;

    internal bool UseBits => Store.Settings.UseBits;

    internal static string L(string key) => Localization.L(key);

    internal static string L(string key, params object?[] args) => Localization.L(key, args);
}

/// <summary>The app-level actions a surface can trigger, so sections never reach for the application object.</summary>
internal sealed class AppCommands
{
    internal required Action ShowPreferences { get; init; }

    internal required Action ShowSpeedTest { get; init; }

    internal required Action ShowStatistics { get; init; }

    internal required Action ShowNetworkSlice { get; init; }

    internal required Action ShowAbout { get; init; }

    internal required Action CopyDiagnostics { get; init; }

    internal required Action Quit { get; init; }
}

/// <summary>
/// One block of the popover. Sections are built once and refreshed in place every tick —
/// rebuilding them would destroy whatever the pointer is over, so a hover highlight or an
/// open detail flyout would flicker away once a second.
/// </summary>
internal interface IPopoverSection
{
    PopoverSection Kind { get; }

    FrameworkElement View { get; }

    /// <summary>Called on every monitor tick while the popover is visible.</summary>
    void Refresh();

    /// <summary>Called when the popover opens or closes, to start or stop any extra sampling.</summary>
    void SetActive(bool active);
}
