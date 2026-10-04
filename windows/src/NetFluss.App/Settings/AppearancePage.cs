// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using NetFluss.Core;

namespace NetFluss.App.Settings;

/// <summary>
/// Appearance — theme, the rate colours, the address display, and the order and visibility
/// of the popover sections: the parts of the macOS Appearance pane that are not about where
/// the meter sits.
/// </summary>
internal static class AppearancePage
{
    internal static FrameworkElement Build(PreferencesContext context)
    {
        var settings = context.Settings;
        var page = new StackPanel();

        page.Children.Add(Kit.Header(Kit.L("Theme")));
        page.Children.Add(Kit.Card(
            Kit.L("Theme"),
            Kit.L("Sets the popover, the widget and the rate colours. A colour below set to anything but Automatic keeps that colour."),
            Kit.Combo(settings, nameof(AppSettings.ThemeId),
                AppTheme.All.Select(theme => new Choice(theme.Id, theme.Id == "system" ? Kit.L("System") : theme.DisplayName)))));

        page.Children.Add(Kit.Header(Kit.L("Colours")));
        var (systemDownload, systemUpload) = SystemTheme.DefaultInk();
        page.Children.Add(Kit.Card(
            Kit.L("Download arrow ↓"),
            null,
            Kit.ColorPicker(settings, nameof(AppSettings.DownloadAccent), nameof(AppSettings.DownloadCustomHex),
                () => settings.ResolveRateColors(systemDownload, systemUpload).Download)));
        page.Children.Add(Kit.Card(
            Kit.L("Upload arrow ↑"),
            null,
            Kit.ColorPicker(settings, nameof(AppSettings.UploadAccent), nameof(AppSettings.UploadCustomHex),
                () => settings.ResolveRateColors(systemDownload, systemUpload).Upload)));

        page.Children.Add(Kit.Header(Kit.L("IP addresses")));
        page.Children.Add(Kit.Card(
            Kit.L("IP display"),
            Kit.L("Flow draws the path from this PC through the router (and a VPN) to the internet; list shows each address with a copy button."),
            Kit.Combo(settings, nameof(AppSettings.ConnectionMode),
            [
                new Choice(ConnectionDisplayMode.Flow, Kit.L("Flow")),
                new Choice(ConnectionDisplayMode.List, Kit.L("List")),
                new Choice(ConnectionDisplayMode.None, Kit.L("None")),
            ], 140)));
        page.Children.Add(Kit.Card(
            Kit.L("External IP"),
            Kit.L("Which public address to show. Looked up via ipify.org."),
            Kit.Combo(settings, nameof(AppSettings.ExternalIPv6),
            [
                new Choice(false, "IPv4"),
                new Choice(true, "IPv6"),
            ], 140)));

        page.Children.Add(Kit.Header(Kit.L("Popover sections")));
        var sections = new ReorderList((key, index) =>
        {
            var order = settings.SectionOrder().ToList();
            var section = order.First(s => s.Id() == key);
            order.Remove(section);
            order.Insert(Math.Clamp(index, 0, order.Count), section);
            settings.SetSectionOrder(order);
        });

        var panel = new StackPanel();
        panel.Children.Add(Kit.Caption(Kit.L("Drag to reorder sections in the popover. Toggling a row mirrors the corresponding setting on the DNS, Wi-Fi, Top Apps and Statistics pages.")));
        sections.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(sections);
        page.Children.Add(Kit.Panel(panel));

        void Refresh()
        {
            sections.SetRows(settings.SectionOrder()
                .Where(section => section is not PopoverSection.Vpn)
                .Select(section =>
                {
                    var label = Kit.Text(Kit.L(section.DisplayKey()));
                    var note = section switch
                    {
                        PopoverSection.Router => Kit.L("Appears when a router is configured."),
                        PopoverSection.Usage when !settings.CollectStatistics => Kit.L("Turns on statistics collection."),
                        _ => null,
                    };

                    var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    text.Children.Add(label);
                    if (note is not null)
                    {
                        text.Children.Add(Kit.Caption(note));
                    }

                    var toggle = Kit.Switch(settings.IsSectionEnabled(section), on => settings.SetSectionEnabled(section, on));
                    // Router can only be switched off here; on needs a router chosen in the Router page.
                    toggle.IsEnabled = section is not PopoverSection.Router || settings.AnyRouterEnabled;
                    toggle.Margin = new Thickness(0, 0, 12, 0);

                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    Grid.SetColumn(text, 1);
                    row.Children.Add(toggle);
                    row.Children.Add(text);
                    return (section.Id(), (FrameworkElement)row);
                }));
        }

        Kit.Watch(page, settings, name =>
        {
            if (name is nameof(AppSettings.PopoverSectionOrder) or nameof(AppSettings.CollectStatistics) ||
                name?.StartsWith("Show", StringComparison.Ordinal) == true || name == nameof(AppSettings.ConnectionMode))
            {
                Refresh();
            }
        });

        Refresh();
        return page;
    }
}
