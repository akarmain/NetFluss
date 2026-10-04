// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetFluss.Core;

namespace NetFluss.App.Settings;

/// <summary>
/// Adapters — the macOS Adapters pane: which adapters show, what they are called, in what
/// order, and which count toward the totals.
/// </summary>
internal static class AdaptersPage
{
    internal static FrameworkElement Build(PreferencesContext context)
    {
        var settings = context.Settings;
        var monitor = context.Monitor;
        var page = new StackPanel();

        page.Children.Add(Kit.Header(Kit.L("Adapter Visibility")));
        page.Children.Add(Kit.Card(
            Kit.L("Show inactive adapters"),
            Kit.L("Most machines carry a dozen idle virtual and tunnel interfaces, so this is off by default."),
            Kit.Switch(settings, nameof(AppSettings.ShowInactiveAdapters))));
        page.Children.Add(Kit.Card(
            Kit.L("Show other adapters (VPN, virtual)"),
            Kit.L("Interfaces that are neither Wi-Fi nor Ethernet: VPN tunnels, Hyper-V and WSL switches, Bluetooth networking."),
            Kit.Switch(settings, nameof(AppSettings.ShowOtherAdapters))));

        var grace = Kit.Combo(settings, nameof(AppSettings.AdapterGraceSeconds),
        [
            new Choice(3.0, "3 s"),
            new Choice(5.0, "5 s"),
            new Choice(10.0, "10 s"),
        ], 90);
        Kit.EnableWhen(grace, settings, nameof(AppSettings.AdapterGraceEnabled));
        grace.Margin = new Thickness(0, 0, 12, 0);
        page.Children.Add(Kit.Card(
            Kit.L("Hide adapters after inactivity"),
            Kit.L("Shows an adapter only while it carries traffic, and for a few seconds after — useful for a VPN that comes and goes."),
            Kit.Row(Kit.Text(Kit.L("Hide after"), 13, "SecondaryTextBrush"), new Border { Width = 8 }, grace, Kit.Switch(settings, nameof(AppSettings.AdapterGraceEnabled)))));

        var caption = Kit.Caption(string.Empty);
        var list = new ReorderList((id, index) =>
        {
            var order = monitor.AllAdapters.Select(a => a.Id).ToList();
            settings.MoveAdapter(order, id, index);
        });
        list.Margin = new Thickness(0, 10, 0, 0);

        var reset = Kit.Button(Kit.L("Reset order"), settings.ResetAdapterOrder);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 10, 0, 0);

        var listPanel = new StackPanel();
        listPanel.Children.Add(Kit.Text(Kit.L("Which adapters to show")));
        listPanel.Children.Add(caption);
        listPanel.Children.Add(list);
        listPanel.Children.Add(reset);
        page.Children.Add(Kit.Panel(listPanel));

        page.Children.Add(Kit.Header(Kit.L("Totals")));
        page.Children.Add(Kit.Card(
            Kit.L("Only include visible adapters in totals"),
            Kit.L("When enabled, the Download/Upload summary and the meter use only adapters that are visible here."),
            Kit.Switch(settings, nameof(AppSettings.TotalsFromVisibleAdaptersOnly))));
        page.Children.Add(Kit.Card(
            Kit.L("Exclude VPN/tunnel adapters from totals"),
            Kit.L("When enabled, VPN and tunnel adapters (WireGuard, OpenVPN, built-in VPN connections) are excluded from totals. Loopback and the Windows filter interfaces are always excluded, since they only mirror other adapters. All adapters remain visible in the adapter list."),
            Kit.Switch(settings, nameof(AppSettings.ExcludeTunnelAdapters))));

        // Rows are rebuilt only when the set or order of adapters changes. The live rate in
        // each row is updated in place: rebuilding every tick destroyed the rename box the
        // user was typing in, between keystrokes.
        var rows = new Dictionary<string, (TextBlock Status, CheckBox Shown)>(StringComparer.OrdinalIgnoreCase);
        var shownOrder = new List<string>();

        string Status(AdapterStatus adapter) => adapter.IsUp
            ? RateFormatter.FormatRate(adapter.RxRateBps + adapter.TxRateBps, settings.UseBits)
            : Kit.L("Disconnected");

        void Rebuild()
        {
            var adapters = monitor.AllAdapters.ToList();
            var ids = adapters.Select(a => a.Id).ToList();

            if (!ids.SequenceEqual(shownOrder, StringComparer.OrdinalIgnoreCase))
            {
                rows.Clear();
                shownOrder = ids;
                list.SetRows(adapters.Select(adapter =>
                {
                    var shown = Kit.Switch(!settings.IsAdapterHidden(adapter.Id), on => settings.SetAdapterHidden(adapter.Id, !on));
                    shown.Margin = new Thickness(0, 0, 12, 0);

                    var name = new TextBox
                    {
                        Text = settings.AdapterDisplayName(adapter.Id, adapter.DisplayName),
                        BorderThickness = new Thickness(0),
                        Background = System.Windows.Media.Brushes.Transparent,
                        Padding = new Thickness(0),
                        FontSize = 14,
                        ToolTip = Kit.L("Rename. Clear the field to restore the Windows name."),
                    };

                    void Commit()
                    {
                        var typed = name.Text.Trim();
                        var current = settings.AdapterCustomNames.TryGetValue(adapter.Id, out var stored) ? stored : null;

                        // The field shows the effective name, which for an unnamed adapter is
                        // the Windows one; committing that unchanged must not pin it as a
                        // custom label, or the adapter would stop following its Windows name.
                        if (current is null && (typed.Length == 0 || typed == adapter.DisplayName))
                        {
                            return;
                        }

                        settings.SetAdapterName(adapter.Id, typed);
                        if (typed.Length == 0)
                        {
                            name.Text = adapter.DisplayName;
                        }
                    }

                    name.LostFocus += (_, _) => Commit();
                    name.KeyDown += (_, e) =>
                    {
                        if (e.Key == Key.Enter)
                        {
                            Commit();
                            e.Handled = true;
                        }
                        else if (e.Key == Key.Escape)
                        {
                            name.Text = settings.AdapterDisplayName(adapter.Id, adapter.DisplayName);
                            e.Handled = true;
                        }
                    };

                    var description = Kit.Caption(adapter.Description);
                    description.TextWrapping = TextWrapping.NoWrap;
                    description.TextTrimming = TextTrimming.CharacterEllipsis;

                    var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    text.Children.Add(name);
                    text.Children.Add(description);

                    var status = Kit.Caption(Status(adapter));
                    status.Margin = new Thickness(10, 0, 8, 0);

                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(text, 1);
                    Grid.SetColumn(status, 2);
                    row.Children.Add(shown);
                    row.Children.Add(text);
                    row.Children.Add(status);

                    rows[adapter.Id] = (status, shown);
                    return (adapter.Id, (FrameworkElement)row);
                }));
            }
            else
            {
                foreach (var adapter in adapters)
                {
                    if (rows.TryGetValue(adapter.Id, out var row))
                    {
                        row.Status.Text = Status(adapter);
                        row.Shown.IsChecked = !settings.IsAdapterHidden(adapter.Id);
                    }
                }
            }

            var hidden = adapters.Count(a => settings.IsAdapterHidden(a.Id));
            caption.Text = adapters.Count == 0
                ? Kit.L("No adapters reported yet.")
                : hidden == 0
                    ? Kit.L("{0} adapters. Switch one off to keep it out of the popover.", adapters.Count)
                    : Kit.L("{0} adapters, {1} hidden.", adapters.Count, hidden);
        }

        void OnMonitor(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NetworkMonitorService.AllAdapters))
            {
                Rebuild();
            }
        }

        page.Loaded += (_, _) => monitor.PropertyChanged += OnMonitor;
        page.Unloaded += (_, _) => monitor.PropertyChanged -= OnMonitor;
        Rebuild();
        return page;
    }
}
