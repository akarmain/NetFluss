// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetFluss.Core;

namespace NetFluss.App.Settings;

/// <summary>Top Apps — the macOS Top Apps pane, including the hidden-apps list.</summary>
internal static class TopAppsPage
{
    internal static FrameworkElement Build(PreferencesContext context)
    {
        var settings = context.Settings;
        var page = new StackPanel();

        page.Children.Add(Kit.Header(Kit.L("Top Apps")));

        var helperNote = Kit.Card(
            Kit.L("Needs the NetFluss helper"),
            Kit.L("Windows only lets administrators watch per-app network traffic, so Top Apps uses the optional helper service. Install it under General → System access."),
            Kit.Button(Kit.L("General"), () => { }));
        helperNote.SetResourceReference(Border.BackgroundProperty, "NoticeBrush");
        page.Children.Add(helperNote);

        page.Children.Add(Kit.Card(
            Kit.L("Show top apps by network usage"),
            Kit.L("Shows the top 5 processes ranked by current network traffic."),
            Kit.Switch(settings, nameof(AppSettings.ShowTopApps))));

        var grace = Kit.Combo(settings, nameof(AppSettings.TopAppsGraceSeconds),
        [
            new Choice(3.0, "3 s"),
            new Choice(5.0, "5 s"),
            new Choice(10.0, "10 s"),
        ], 90);
        Kit.EnableWhen(grace, settings, nameof(AppSettings.TopAppsGraceEnabled));
        grace.Margin = new Thickness(0, 0, 12, 0);
        page.Children.Add(Kit.Card(
            Kit.L("Keep apps visible after traffic stops"),
            Kit.L("An app stays listed for a few seconds after it goes quiet, so the list does not reshuffle between bursts."),
            Kit.Row(Kit.Text(Kit.L("Visible for"), 13, "SecondaryTextBrush"), new Border { Width = 8 }, grace, Kit.Switch(settings, nameof(AppSettings.TopAppsGraceEnabled)))));

        page.Children.Add(Kit.Header(Kit.L("Hide Apps")));

        var hiddenList = new StackPanel();

        // Two ways in: pick from the apps that just used the network, or type a name for one
        // that is not running right now.
        var recent = new ComboBox { MinWidth = 220 };
        var hideRecent = Kit.Button(Kit.L("Hide"), () =>
        {
            if (recent.SelectedItem is string name)
            {
                settings.SetAppHidden(name, true);
            }
        });
        hideRecent.Margin = new Thickness(8, 0, 0, 0);

        var typed = new TextBox { MinWidth = 220 };
        var hideTyped = Kit.Button(Kit.L("Hide"), () => { });
        hideTyped.Margin = new Thickness(8, 0, 0, 0);

        void HideTyped()
        {
            var name = typed.Text.Trim();
            if (name.Length > 0)
            {
                settings.SetAppHidden(name, true);
                typed.Text = string.Empty;
            }
        }

        hideTyped.Click += (_, _) => HideTyped();
        typed.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                HideTyped();
                e.Handled = true;
            }
        };

        var recentRow = Kit.Row(recent, hideRecent);
        recentRow.Margin = new Thickness(0, 10, 0, 0);
        var typedRow = Kit.Row(typed, hideTyped);
        typedRow.Margin = new Thickness(0, 8, 0, 0);
        var addRow = Kit.Row();
        addRow.Orientation = Orientation.Vertical;
        addRow.Children.Add(recentRow);
        addRow.Children.Add(typedRow);

        var panel = new StackPanel();
        panel.Children.Add(Kit.Text(Kit.L("Hidden apps:")));
        panel.Children.Add(Kit.Caption(Kit.L("Hidden apps stay out of Top Apps and the statistics lists. Pick one that used the network in the last minute, or type its name.")));
        hiddenList.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(hiddenList);
        panel.Children.Add(addRow);
        page.Children.Add(Kit.Panel(panel));

        void Refresh()
        {
            helperNote.Visibility = context.Traffic.Availability is TrafficAvailability.NeedsHelper or TrafficAvailability.HelperOutdated or TrafficAvailability.HelperFailed ||
                                    !context.Helper.IsConnected
                ? Visibility.Visible
                : Visibility.Collapsed;

            hiddenList.Children.Clear();
            if (settings.HiddenApps.Count == 0)
            {
                hiddenList.Children.Add(Kit.Caption(Kit.L("No apps are hidden.")));
            }

            foreach (var name in settings.HiddenApps.Order(StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Kit.Text(name, 13));
                var remove = Kit.IconButton("", Kit.L("Show again"), () => settings.SetAppHidden(name, false));
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                hiddenList.Children.Add(row);
            }

            var recentNames = context.Traffic.RecentAppNames()
                .Where(name => !settings.IsAppHidden(name))
                .ToList();
            recent.ItemsSource = recentNames;
            recent.IsEnabled = hideRecent.IsEnabled = recentNames.Count > 0;
            recent.ToolTip = recentNames.Count > 0 ? null : Kit.L("No recent apps detected yet. Keep Top Apps enabled and check back shortly.");
        }

        // The "General" button on the helper note jumps to where the helper is installed.
        if (((Grid)helperNote.Child).Children.OfType<Button>().FirstOrDefault() is { } jump)
        {
            jump.Click += (_, _) => (Window.GetWindow(page) as PreferencesWindow)?.SelectTab("general");
        }

        Kit.Watch(page, settings, name =>
        {
            if (name == nameof(AppSettings.HiddenApps))
            {
                Refresh();
            }
        });

        recent.DropDownOpened += (_, _) => Refresh();
        Refresh();
        return page;
    }
}
