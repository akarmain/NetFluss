// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using NetFluss.Core;

namespace NetFluss.App.Settings;

/// <summary>Wi-Fi — the macOS Wi-Fi Switcher pane, plus the pinned networks.</summary>
internal static class WifiPage
{
    internal static FrameworkElement Build(PreferencesContext context)
    {
        var settings = context.Settings;
        var page = new StackPanel();

        page.Children.Add(Kit.Header(Kit.L("Wi-Fi Switcher")));
        page.Children.Add(Kit.Card(
            Kit.L("Show Wi-Fi networks in popover"),
            Kit.L("Lists nearby Wi-Fi networks. Click a row to join — saved networks reconnect immediately, new secured networks ask for the password, and Windows remembers it for its own Wi-Fi menu too."),
            Kit.Switch(settings, nameof(AppSettings.ShowWifiSwitcher))));

        var count = Kit.Combo(settings, nameof(AppSettings.WifiLimitCount),
            new[] { 3, 5, 8, 10, 15, 20, 30 }.Select(n => new Choice(n, n.ToString(Localization.Culture))), 90);
        Kit.EnableWhen(count, settings, nameof(AppSettings.WifiLimitEnabled));
        count.Margin = new Thickness(0, 0, 12, 0);
        page.Children.Add(Kit.Card(
            Kit.L("Only show the strongest networks"),
            Kit.L("Pinned networks and the currently-connected network always appear regardless of this limit."),
            Kit.Row(Kit.Text(Kit.L("Maximum networks shown"), 13, "SecondaryTextBrush"), new Border { Width = 8 }, count, Kit.Switch(settings, nameof(AppSettings.WifiLimitEnabled)))));

        page.Children.Add(Kit.Header(Kit.L("Pinned")));
        var pinned = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(Kit.Caption(Kit.L("Pinned networks stay at the top of the list, and stay listed as “Not available” while out of range. Pin one with the pin button in the popover.")));
        panel.Children.Add(pinned);
        page.Children.Add(Kit.Panel(panel));

        void Refresh()
        {
            pinned.Children.Clear();
            if (settings.PinnedWifiNetworks.Count == 0)
            {
                pinned.Children.Add(Kit.Caption(Kit.L("No networks are pinned.")));
            }

            foreach (var ssid in settings.PinnedWifiNetworks)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = Kit.Icon(Glyph.Wifi, 14, "SecondaryTextBrush");
                icon.Margin = new Thickness(0, 0, 10, 0);
                var name = Kit.Text(ssid, 13);
                var unpin = Kit.IconButton(Glyph.Unpin, Kit.L("Unpin"), () => settings.SetWifiPinned(ssid, false));
                Grid.SetColumn(name, 1);
                Grid.SetColumn(unpin, 2);
                row.Children.Add(icon);
                row.Children.Add(name);
                row.Children.Add(unpin);
                pinned.Children.Add(row);
            }
        }

        Kit.Watch(page, settings, name =>
        {
            if (name == nameof(AppSettings.PinnedWifiNetworks))
            {
                Refresh();
            }
        });

        Refresh();
        return page;
    }
}
