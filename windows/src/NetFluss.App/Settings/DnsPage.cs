// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using NetFluss.Core;

namespace NetFluss.App.Settings;

/// <summary>
/// DNS — the macOS DNS Switcher pane: the popover switcher, the presets with visibility and
/// order, custom presets of up to four servers, and applying one from here.
/// </summary>
internal static class DnsPage
{
    private const int MaximumCustomServers = 4;

    internal static FrameworkElement Build(PreferencesContext context)
    {
        var settings = context.Settings;
        var page = new StackPanel();

        page.Children.Add(Kit.Header(Kit.L("DNS Switcher")));
        page.Children.Add(Kit.Card(
            Kit.L("Show DNS switcher in popover"),
            context.Privileged.IsSilent
                ? Kit.L("The NetFluss helper is installed, so DNS changes apply without asking.")
                : Kit.L("Changing DNS needs administrator rights: each change asks for approval, unless the optional NetFluss helper is installed (General → System access)."),
            Kit.Switch(settings, nameof(AppSettings.ShowDnsSwitcher))));

        // ---- Adapter -------------------------------------------------------------
        var current = Kit.Caption(string.Empty);
        var adapterText = new StackPanel();
        adapterText.Children.Add(Kit.Text(Kit.L("Adapter")));
        adapterText.Children.Add(Kit.Caption(Kit.L("Which adapter the switcher changes. Automatic follows the one carrying the internet connection.")));
        adapterText.Children.Add(current);

        var adapters = DnsController.Read()
            .Where(state => Guid.TryParse(state.AdapterId, out _))
            .Select(state => new Choice(Guid.Parse(state.AdapterId).ToString("B"), state.AdapterName))
            .Prepend(new Choice(string.Empty, Kit.L("Automatic")))
            .ToList();

        page.Children.Add(Kit.Card(adapterText, Kit.Combo(settings, nameof(AppSettings.DnsAdapterId), adapters, 220)));

        // ---- Presets -------------------------------------------------------------
        page.Children.Add(Kit.Header(Kit.L("Presets")));
        var status = Kit.Caption(string.Empty);
        var presets = new ReorderList((id, index) => settings.MoveDnsPreset(id, index));
        presets.Margin = new Thickness(0, 8, 0, 0);

        var presetPanel = new StackPanel();
        presetPanel.Children.Add(Kit.Caption(Kit.L("Switch a preset off to keep it out of the popover. A green check marks the one in use.")));
        presetPanel.Children.Add(presets);
        presetPanel.Children.Add(status);
        page.Children.Add(Kit.Panel(presetPanel));

        // ---- Custom preset ---------------------------------------------------------
        page.Children.Add(Kit.Header(Kit.L("Add Custom DNS…")));
        var name = new TextBox { Width = 200 };
        var servers = Enumerable.Range(0, MaximumCustomServers).Select(_ => new TextBox { Width = 200, Margin = new Thickness(0, 6, 0, 0) }).ToList();
        var addStatus = Kit.Caption(string.Empty);
        var add = Kit.Button(Kit.L("Add"), () =>
        {
            var list = servers.Select(box => box.Text.Trim()).Where(text => text.Length > 0).ToList();
            var result = settings.AddDnsPreset(name.Text, list);
            if (!result.IsValid)
            {
                addStatus.Text = Kit.L(result.Error ?? "Could not add that preset.");
                addStatus.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                return;
            }

            name.Text = string.Empty;
            servers.ForEach(box => box.Text = string.Empty);
            addStatus.Text = Kit.L("Preset added.");
            addStatus.SetResourceReference(TextBlock.ForegroundProperty, "ActiveBrush");
        }, accent: true);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 12, 0, 0);

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < MaximumCustomServers + 1; i++)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var nameLabel = Kit.Text(Kit.L("Name"), 13);
        form.Children.Add(nameLabel);
        Grid.SetColumn(name, 1);
        form.Children.Add(name);
        for (var i = 0; i < MaximumCustomServers; i++)
        {
            var label = Kit.Text(Kit.L("Server {0}", i + 1), 13);
            label.Margin = new Thickness(0, 6, 0, 0);
            Grid.SetRow(label, i + 1);
            Grid.SetRow(servers[i], i + 1);
            Grid.SetColumn(servers[i], 1);
            form.Children.Add(label);
            form.Children.Add(servers[i]);
        }

        var formPanel = new StackPanel();
        formPanel.Children.Add(Kit.Caption(Kit.L("Up to four IPv4 or IPv6 servers. A preset with only IPv4 also puts IPv6 back on automatic, so a leftover IPv6 resolver cannot keep answering.")));
        form.Margin = new Thickness(0, 10, 0, 0);
        formPanel.Children.Add(form);
        formPanel.Children.Add(add);
        formPanel.Children.Add(addStatus);
        page.Children.Add(Kit.Panel(formPanel));

        string? TargetId() => !string.IsNullOrEmpty(settings.DnsAdapterId) && DnsController.AdapterName(settings.DnsAdapterId) is not null
            ? settings.DnsAdapterId
            : context.Monitor.Addresses.PrimaryAdapterId;

        var applying = false;

        void Refresh()
        {
            var target = TargetId();
            var targetName = target is null ? null : DnsController.AdapterName(target);
            var staticServers = target is null ? [] : DnsController.StaticServers(target);
            var active = DnsController.ActivePresetId(settings.AllDnsPresets(), staticServers);

            current.Text = targetName is null
                ? Kit.L("No adapter with an internet connection was found.")
                : staticServers.Count == 0
                    ? Kit.L("{0} is on automatic DNS.", targetName)
                    : Kit.L("{0} uses {1}.", targetName, string.Join(", ", staticServers));

            presets.SetRows(settings.OrderedDnsPresets().Select(preset =>
            {
                var visible = Kit.Switch(!settings.HiddenDnsPresets.Contains(preset.Id), on => settings.SetDnsPresetHidden(preset.Id, !on));
                visible.Margin = new Thickness(0, 0, 12, 0);
                visible.ToolTip = Kit.L("Show in the popover");

                var check = Kit.Icon(active == preset.Id ? Glyph.CheckCircle : string.Empty, 14, "ActiveBrush");
                check.Width = 22;

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(Kit.Text(Kit.L(preset.Name), 14, "TextBrush", active == preset.Id ? FontWeights.SemiBold : FontWeights.Normal));
                text.Children.Add(Kit.Caption(preset.IsAutomatic ? Kit.L("Automatic (DHCP)") : string.Join(", ", preset.Servers)));

                var apply = Kit.Button(Kit.L("Apply"), async () =>
                {
                    if (applying || target is null || targetName is null)
                    {
                        return;
                    }

                    applying = true;
                    status.Text = context.Privileged.IsSilent
                        ? Kit.L("Applying {0}…", Kit.L(preset.Name))
                        : Kit.L("Applying {0}… confirm the administrator prompt.", Kit.L(preset.Name));
                    var result = await context.Privileged.SetDnsAsync(target, targetName, preset.Servers);
                    applying = false;
                    status.Text = result.Message;
                    status.SetResourceReference(TextBlock.ForegroundProperty, result.Succeeded ? "ActiveBrush" : "WarningBrush");
                    Refresh();
                });
                apply.IsEnabled = active != preset.Id && targetName is not null;
                apply.Margin = new Thickness(8, 0, 0, 0);

                var actions = Kit.Row(apply);
                if (!preset.IsBuiltIn)
                {
                    var delete = Kit.IconButton("", Kit.L("Delete"), () => settings.RemoveDnsPreset(preset.Id));
                    delete.Margin = new Thickness(4, 0, 0, 0);
                    actions.Children.Add(delete);
                }

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(check, 1);
                Grid.SetColumn(text, 2);
                Grid.SetColumn(actions, 3);
                row.Children.Add(visible);
                row.Children.Add(check);
                row.Children.Add(text);
                row.Children.Add(actions);
                return (preset.Id, (FrameworkElement)row);
            }));
        }

        Kit.Watch(page, settings, changed =>
        {
            if (changed is nameof(AppSettings.CustomDnsPresets) or nameof(AppSettings.HiddenDnsPresets) or nameof(AppSettings.DnsPresetOrder) or nameof(AppSettings.DnsAdapterId))
            {
                Refresh();
            }
        });

        Refresh();
        return page;
    }
}
