// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetFluss.Core;

namespace NetFluss.App.Settings;

/// <summary>
/// Router — the macOS Router pane: Fritz!Box, UniFi, OpenWRT and OPNsense, each with its
/// switch, address (empty follows the default gateway) and credentials. Credentials go
/// to Windows Credential Manager, never to settings.json.
/// </summary>
internal static class RouterPage
{
    internal static FrameworkElement Build(PreferencesContext context)
    {
        var page = new StackPanel();
        var refreshers = new List<Action>();

        page.Children.Add(Section(context, RouterKind.FritzBox, "Fritz!Box Bandwidth", "Show Fritz!Box bandwidth in popover",
            nameof(AppSettings.FritzBoxEnabled), (s, v) => s.FritzBoxHost = v,
            "Queries your Fritz!Box via TR-064 (no authentication needed for bandwidth data). Auto uses the current default gateway. Set a fixed address if your Fritz!Box is reachable at a different IP. Port 49000 must be reachable.",
            refreshers));

        page.Children.Add(Section(context, RouterKind.UniFi, "UniFi Bandwidth", "Show UniFi bandwidth in popover",
            nameof(AppSettings.UniFiEnabled), (s, v) => s.UniFiHost = v, null, refreshers));

        page.Children.Add(Section(context, RouterKind.OpenWrt, "OpenWRT Bandwidth", "Show OpenWRT bandwidth in popover",
            nameof(AppSettings.OpenWrtEnabled), (s, v) => s.OpenWrtHost = v,
            "Queries your OpenWRT router via ubus JSON-RPC over HTTPS. Auto uses the current default gateway, which may be the wrong router on dual-router setups. Set a fixed OpenWRT IP or URL if needed — for plain HTTP, enter http://<IP>. Requires the router's admin credentials and the uhttpd-mod-ubus package.",
            refreshers));

        page.Children.Add(Section(context, RouterKind.OpnSense, "OPNsense Bandwidth", "Show OPNsense bandwidth in popover",
            nameof(AppSettings.OpnSenseEnabled), (s, v) => s.OpnSenseHost = v,
            "Queries your OPNsense router via its REST API over HTTPS. Auto uses the current default gateway. If the web interface uses plain HTTP or a different port, enter the full URL (e.g. http://192.168.1.1 or https://192.168.1.1:8443). Requires an API key and secret configured in OPNsense.",
            refreshers));

        void RefreshAll() => refreshers.ForEach(r => r());

        // The page polls while it is open, so a router can be set up without the popover.
        IDisposable? lease = null;
        page.Loaded += (_, _) =>
        {
            lease ??= context.Routers.Acquire();
            context.Routers.Changed += OnChanged;
            RefreshAll();
        };
        page.Unloaded += (_, _) =>
        {
            lease?.Dispose();
            lease = null;
            context.Routers.Changed -= OnChanged;
        };

        void OnChanged(object? sender, EventArgs e) => RefreshAll();

        Kit.Watch(page, context.Settings, _ => RefreshAll());
        return page;
    }

    private static FrameworkElement Section(
        PreferencesContext context,
        RouterKind kind,
        string header,
        string toggleTitle,
        string enabledProperty,
        Action<AppSettings, string> setHost,
        string? description,
        List<Action> refreshers)
    {
        var settings = context.Settings;
        var routers = context.Routers;
        var section = new StackPanel();
        section.Children.Add(Kit.Header(Kit.L(header)));

        var details = new StackPanel();
        section.Children.Add(Kit.Card(Kit.L(toggleTitle), null, Kit.Switch(settings, enabledProperty)));
        section.Children.Add(details);

        // ---- Address ---------------------------------------------------------------------
        var address = new TextBox { Width = 230, Text = routers.TypedHost(kind) };
        var placeholder = Kit.Caption(string.Empty);
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(9, 0, 0, 0);
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        var field = new Grid();
        field.Children.Add(address);
        field.Children.Add(placeholder);

        var save = Kit.Button(Kit.L("Save"), () => { });
        save.Click += (_, _) => Commit();
        save.Margin = new Thickness(8, 0, 0, 0);

        void Commit()
        {
            var value = address.Text.Trim();
            setHost(settings, value);
            routers.AddressChanged(kind);
            save.IsEnabled = false;
        }

        address.TextChanged += (_, _) =>
        {
            placeholder.Visibility = address.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            save.IsEnabled = address.Text.Trim() != routers.TypedHost(kind);
        };
        address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        save.IsEnabled = false;

        var addressText = new StackPanel();
        addressText.Children.Add(Kit.Text(Kit.L("Router address")));
        var auto = Kit.Caption(string.Empty);
        addressText.Children.Add(auto);
        details.Children.Add(Kit.Card(addressText, Kit.Row(field, save)));

        // ---- UniFi authentication ----------------------------------------------------------
        TextBlock? authCaption = null;
        if (kind == RouterKind.UniFi)
        {
            var mode = Kit.Combo(settings, nameof(AppSettings.UniFiUseApiKey),
            [
                new Choice(false, Kit.L("Local admin")),
                new Choice(true, Kit.L("API key")),
            ], 160);
            authCaption = Kit.Caption(string.Empty);
            var authText = new StackPanel();
            authText.Children.Add(Kit.Text(Kit.L("Authentication")));
            authText.Children.Add(authCaption);
            details.Children.Add(Kit.Card(authText, mode));
        }

        // ---- Credentials --------------------------------------------------------------------
        TextBlock? credentialStatus = null;
        TextBlock? credentialTitle = null;
        Button? edit = null;
        StackPanel? form = null;
        if (kind != RouterKind.FritzBox)
        {
            credentialTitle = Kit.Text(string.Empty);
            credentialStatus = Kit.Caption(string.Empty);
            var credentialText = new StackPanel();
            credentialText.Children.Add(credentialTitle);
            credentialText.Children.Add(credentialStatus);

            form = new StackPanel { Visibility = Visibility.Collapsed };
            edit = Kit.Button(Kit.L("Edit…"), () =>
            {
                BuildForm(context, kind, form, () =>
                {
                    form.Visibility = Visibility.Collapsed;
                    refreshers.ForEach(r => r());
                });
                form.Visibility = Visibility.Visible;
            });

            details.Children.Add(Kit.Card(credentialText, edit, form));
        }

        // ---- Description and live status ----------------------------------------------------
        var about = Kit.Caption(description is null ? string.Empty : Kit.L(description));
        about.TextWrapping = TextWrapping.Wrap;
        var status = Kit.Caption(string.Empty);
        status.TextWrapping = TextWrapping.Wrap;
        status.Margin = new Thickness(0, 8, 0, 0);
        var info = new StackPanel();
        info.Children.Add(about);
        info.Children.Add(status);
        details.Children.Add(Kit.Panel(info));

        refreshers.Add(() =>
        {
            var enabled = routers.IsEnabled(kind);
            details.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled)
            {
                return;
            }

            var gateway = context.Monitor.Addresses.GatewayIp ?? "—";
            placeholder.Text = Kit.L("Router IP or URL (auto uses gateway)");
            placeholder.Visibility = address.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            auto.Text = routers.TypedHost(kind).Length == 0
                ? gateway + " " + Kit.L("(auto)")
                : routers.TypedHost(kind);

            var useKey = kind == RouterKind.UniFi && settings.UniFiUseApiKey;
            if (authCaption is not null)
            {
                about.Text = Kit.L(useKey
                    ? "Queries your UniFi gateway via its local API (HTTPS) using a Network API key. Create one in the UniFi Network app under Settings → Control Plane → Integrations. API keys work with 2FA enabled and never expire."
                    : "Queries your UniFi gateway via its local API (HTTPS). Requires a local admin account on the UniFi controller. If the account has 2FA enabled, use an API key instead — password login cannot complete a 2FA challenge.");
            }

            if (credentialStatus is not null && credentialTitle is not null)
            {
                credentialTitle.Text = Kit.L(kind == RouterKind.OpnSense ? "API Credentials" : useKey ? "API key" : "Credentials");
                var configured = routers.HasCredentials(kind);
                credentialStatus.Text = Kit.L(configured ? "Configured" : "Not set");
                credentialStatus.SetResourceReference(TextBlock.ForegroundProperty, configured ? "SecondaryTextBrush" : "WarningBrush");
            }

            var state = routers.State(kind);
            if (state.Error is { } error)
            {
                status.Text = "⚠ " + error;
                status.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            }
            else if (state.Bandwidth is { } bandwidth)
            {
                status.Text = Kit.L("Connected") + " — ↓ " + RateFormatter.FormatRate(bandwidth.RxRate, settings.UseBits) +
                              "   ↑ " + RateFormatter.FormatRate(bandwidth.TxRate, settings.UseBits);
                status.SetResourceReference(TextBlock.ForegroundProperty, "ActiveBrush");
            }
            else
            {
                status.Text = string.Empty;
            }

            status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        });

        return section;
    }

    /// <summary>The inline credential editor: two fields (or one, for a UniFi API key) and Save.</summary>
    private static void BuildForm(PreferencesContext context, RouterKind kind, StackPanel form, Action done)
    {
        form.Children.Clear();
        var useKey = kind == RouterKind.UniFi && context.Settings.UniFiUseApiKey;
        var (firstLabel, secondLabel) = kind == RouterKind.OpnSense
            ? ("API Key", "API Secret")
            : useKey ? ("API key", (string?)null) : ("Username", "Password");

        var first = new TextBox { Width = 260 };
        var second = new PasswordBox { Width = 260 };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        var firstText = Kit.Text(Kit.L(firstLabel), 13);
        grid.Children.Add(firstText);
        Grid.SetColumn(first, 1);
        grid.Children.Add(first);
        if (secondLabel is not null)
        {
            var secondText = Kit.Text(Kit.L(secondLabel), 13);
            secondText.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(secondText, 1);
            Grid.SetRow(second, 1);
            Grid.SetColumn(second, 1);
            second.Margin = new Thickness(0, 8, 0, 0);
            grid.Children.Add(secondText);
            grid.Children.Add(second);
        }

        var error = Kit.Caption(string.Empty);
        error.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        error.Visibility = Visibility.Collapsed;

        var cancel = Kit.Button(Kit.L("Cancel"), done);
        var save = Kit.Button(Kit.L("Save"), () =>
        {
            if (first.Text.Trim().Length == 0 || (secondLabel is not null && second.Password.Length == 0))
            {
                error.Text = Kit.L("Fill in both fields.");
                error.Visibility = Visibility.Visible;
                return;
            }

            context.Routers.SaveCredentials(kind, first.Text, second.Password);
            second.Clear();
            done();
        }, accent: true);
        save.Margin = new Thickness(8, 0, 0, 0);
        var buttons = Kit.Row(cancel, save);
        buttons.Margin = new Thickness(0, 12, 0, 0);

        form.Children.Add(Kit.Caption(Kit.L("Saved in Windows Credential Manager for {0}.", RouterService.Account(context.Routers.Host(kind)))));
        grid.Margin = new Thickness(0, 10, 0, 0);
        form.Children.Add(grid);
        form.Children.Add(error);
        form.Children.Add(buttons);
        first.Loaded += (_, _) => first.Focus();
    }
}
