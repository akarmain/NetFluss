// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using NetFluss.Core;

namespace NetFluss.App.Settings;

/// <summary>Statistics — the macOS Statistics pane, plus opening and resetting the history.</summary>
internal static class StatisticsPage
{
    internal static FrameworkElement Build(PreferencesContext context)
    {
        var settings = context.Settings;
        var page = new StackPanel();

        page.Children.Add(Kit.Header(Kit.L("Statistics")));
        page.Children.Add(Kit.Card(
            Kit.L("Collect historical statistics"),
            Kit.L("Keeps minute, hourly and daily totals per adapter on this PC, for the Statistics window and the Data Usage section. Off by default; collecting costs one subtraction per adapter per tick."),
            Kit.Switch(settings, nameof(AppSettings.CollectStatistics))));

        var usage = Kit.Card(
            Kit.L("Display usage summary on popover"),
            Kit.L("Shows today's and this month's upload, download, and total data in the popover. Requires historical statistics collection."),
            Kit.Switch(settings, nameof(AppSettings.ShowUsageSummary)));
        Kit.EnableWhen(usage, settings, nameof(AppSettings.CollectStatistics));
        page.Children.Add(usage);

        page.Children.Add(Kit.Card(
            Kit.L("Collect app statistics"),
            Kit.L("Top download and upload apps come from Windows' own data usage records — the numbers behind Settings → Network & internet → Data usage. It costs nothing to keep on, goes back to before NetFluss was installed, and is updated by Windows about once an hour."),
            Kit.Switch(settings, nameof(AppSettings.CollectAppStatistics))));

        page.Children.Add(Kit.Header(Kit.L("History")));
        page.Children.Add(Kit.Card(
            Kit.L("Bandwidth Statistics"),
            Kit.L("Download and upload history by adapter and by app, from the last hour to the last year."),
            Kit.Button(Kit.L("Open…"), context.Commands.ShowStatistics)));

        // A destructive action gets a confirmation step inline rather than a modal dialog.
        var status = Kit.Caption(string.Empty);
        var confirm = Kit.Button(Kit.L("Delete history"), () => { }, accent: true);
        var cancel = Kit.Button(Kit.L("Cancel"), () => { });
        cancel.Margin = new Thickness(8, 0, 0, 0);
        var confirmRow = Kit.Row(confirm, cancel);
        confirmRow.Visibility = Visibility.Collapsed;
        var reset = Kit.Button(Kit.L("Reset Statistics…"), () => { });

        reset.Click += (_, _) =>
        {
            reset.Visibility = Visibility.Collapsed;
            confirmRow.Visibility = Visibility.Visible;
            status.Text = Kit.L("This deletes all recorded adapter history. Windows' own app records are not affected.");
        };
        cancel.Click += (_, _) =>
        {
            reset.Visibility = Visibility.Visible;
            confirmRow.Visibility = Visibility.Collapsed;
            status.Text = string.Empty;
        };
        confirm.Click += (_, _) =>
        {
            context.Statistics.Reset();
            reset.Visibility = Visibility.Visible;
            confirmRow.Visibility = Visibility.Collapsed;
            status.Text = Kit.L("Statistics were reset.");
        };

        var left = new StackPanel();
        left.Children.Add(Kit.Text(Kit.L("Reset Statistics")));
        left.Children.Add(Kit.Caption(Kit.L("Starts the adapter history over from now.")));
        left.Children.Add(status);
        page.Children.Add(Kit.Card(left, new Grid { Children = { reset, confirmRow } }));

        return page;
    }
}
