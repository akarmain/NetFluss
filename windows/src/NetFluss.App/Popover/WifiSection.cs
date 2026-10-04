// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NetFluss.Core;
using NetFluss.Native;

namespace NetFluss.App.Popover;

/// <summary>
/// The Wi-Fi switcher — port of the macOS <c>WifiSwitcherSection</c>.
///
/// <para>A new secured network asks for its password inline, under its own row, rather than
/// in a dialog: a separate window would take focus from the popover, which dismisses itself
/// on deactivation exactly as NSPopover does.</para>
/// </summary>
internal sealed class WifiSection : IPopoverSection
{
    private readonly PopoverContext _context;
    private readonly TextBlock _busy;
    private readonly TextBlock _status;
    private readonly StackPanel _location;
    private readonly StackPanel _rows = new() { Margin = new Thickness(8, 0, 8, 0) };
    private readonly Grid _error;
    private readonly TextBlock _errorText;
    private string? _passwordFor;
    private string _signature = string.Empty;

    internal WifiSection(PopoverContext context)
    {
        _context = context;
        _context.Wifi.Changed += (_, _) => Rebuild();

        _busy = Ui.Icon(Glyph.Refresh, 11, Ui.Secondary);
        _busy.Margin = new Thickness(0, 10, 14, 6);
        _busy.Opacity = 0.6;
        _busy.Visibility = Visibility.Collapsed;

        var header = Ui.Columns(Ui.Star, Ui.Auto);
        header.Children.Add(Ui.SectionTitle(PopoverContext.L("Wi-Fi Networks")).At(0));
        header.Children.Add(_busy.At(1));

        _status = Ui.Wrapping(string.Empty, 11);
        _status.Margin = new Thickness(14, 0, 14, 0);

        _location = Ui.Column(
            Ui.Wrapping(PopoverContext.L("Location access is required to list nearby Wi-Fi networks. Windows uses the same restriction in Settings → Privacy & security → Location."), 11),
            Ui.LinkButton(PopoverContext.L("Open Location Settings…"), OpenLocationSettings).Margin(0, 4, 0, 0));
        _location.Margin = new Thickness(14, 0, 14, 0);

        _errorText = Ui.Wrapping(string.Empty, 11);
        var warning = Ui.Icon(Glyph.Warning, 11, Ui.Orange);
        warning.VerticalAlignment = VerticalAlignment.Top;
        warning.Margin = new Thickness(0, 2, 6, 0);
        var dismiss = Ui.IconButton(Glyph.Close, PopoverContext.L("Dismiss"), () => _context.Wifi.ClearError(), 9);
        dismiss.Width = 20;
        dismiss.Height = 20;
        dismiss.VerticalAlignment = VerticalAlignment.Top;

        _error = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
        _error.Margin = new Thickness(14, 4, 10, 0);
        _error.Children.Add(warning.At(0));
        _error.Children.Add(_errorText.At(1));
        _error.Children.Add(dismiss.At(2));

        View = Ui.Column(header, _status, _location, _rows, _error).Margin(0, 0, 0, 8);
        Rebuild();
    }

    public PopoverSection Kind => PopoverSection.Wifi;

    public FrameworkElement View { get; }

    public void SetActive(bool active)
    {
        if (!active)
        {
            _passwordFor = null;
        }

        _context.Wifi.SetActive(active);
    }

    public void Refresh()
    {
    }

    private static void OpenLocationSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No Settings app (a stripped-down SKU); nothing better to offer.
        }
    }

    private void Rebuild()
    {
        var wifi = _context.Wifi;
        var networks = wifi.Networks;

        _busy.Visibility = wifi.ConnectingTo is null ? Visibility.Collapsed : Visibility.Visible;
        _errorText.Text = wifi.LastError ?? string.Empty;
        _error.Visibility = string.IsNullOrEmpty(wifi.LastError) ? Visibility.Collapsed : Visibility.Visible;

        string? status = null;
        if (!wifi.HasWifi)
        {
            status = PopoverContext.L("No Wi-Fi adapter found.");
        }
        else if (wifi.Access != WlanAccess.LocationDenied && networks.Count == 0)
        {
            status = PopoverContext.L("Scanning for networks…");
        }

        _status.Text = status ?? string.Empty;
        _status.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        _location.Visibility = wifi.HasWifi && wifi.Access == WlanAccess.LocationDenied && networks.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Rebuild rows only when something a row shows has changed; a rebuild on every read
        // would wipe a half-typed password and undo the hover under the pointer.
        var signature = string.Join('|', networks.Select(n =>
            $"{n.Ssid}:{n.IsCurrent}:{n.IsPinned}:{n.IsSaved}:{n.IsAvailable}:{WifiFormat.SignalBars(n.Rssi)}:{n.Band}"))
            + $"#{wifi.ConnectingTo}#{_passwordFor}";

        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        _rows.Children.Clear();

        foreach (var network in networks)
        {
            _rows.Children.Add(BuildRow(network));
            if (_passwordFor == network.Ssid)
            {
                _rows.Children.Add(BuildPasswordPanel(network));
            }
        }
    }

    private FrameworkElement BuildRow(WifiNetwork network)
    {
        var wifi = _context.Wifi;
        var connecting = wifi.ConnectingTo == network.Ssid;

        var mark = Ui.Icon(network.IsCurrent ? Glyph.CheckCircle : Glyph.Circle, 13, network.IsCurrent ? Ui.Green : Ui.Secondary);
        mark.Width = 18;
        mark.Margin = new Thickness(0, 0, 8, 0);

        var name = Ui.Label(network.Ssid, 12, Ui.Text, network.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal);
        name.Opacity = network.IsAvailable ? 1 : 0.7;
        var title = Ui.Row(name);

        if (network.IsSecured)
        {
            title.Children.Add(Ui.Icon(Glyph.Lock, 9, Ui.Secondary).Margin(4, 1, 0, 0));
        }

        if (network.IsSaved && !network.IsCurrent)
        {
            var star = Ui.Icon(Glyph.Star, 8, Ui.Orange).Margin(4, 1, 0, 0);
            star.Opacity = 0.8;
            star.ToolTip = PopoverContext.L("Saved network");
            title.Children.Add(star);
        }

        TextBlock subtitle;
        if (network.IsAvailable)
        {
            var parts = new List<string>();
            if (network.Band is { } band)
            {
                parts.Add(WifiFormat.BandLabel(band));
            }

            if (network.Rssi is { } rssi)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{rssi} dBm"));
            }

            subtitle = Ui.Label(string.Join("  ", parts), 10, Ui.Secondary);
        }
        else
        {
            subtitle = Ui.Label(PopoverContext.L("Not available"), 10, Ui.Secondary);
            subtitle.FontStyle = FontStyles.Italic;
        }

        var bars = WifiFormat.SignalBars(network.Rssi);
        var signal = Ui.Icon(network.IsAvailable ? Glyph.ForSignal(bars) : Glyph.Signal1, 12, Ui.Secondary);
        signal.Opacity = network.IsAvailable ? 0.45 + (0.55 * bars / 4.0) : 0.35;
        signal.Margin = new Thickness(6, 0, 0, 0);

        var content = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto, Ui.Auto);
        content.Children.Add(mark.At(0));
        content.Children.Add(Ui.Column(title, subtitle).At(1));
        content.Children.Add(signal.At(2));

        if (connecting)
        {
            var busy = Ui.Icon(Glyph.Refresh, 11, Ui.Secondary).Margin(6, 0, 0, 0);
            busy.Opacity = 0.6;
            content.Children.Add(busy.At(3));
        }

        var row = Ui.RowButton(content, () => OnRowClicked(network));
        row.IsEnabled = !network.IsCurrent && wifi.ConnectingTo is null;
        if (network.IsCurrent)
        {
            row.SetResourceReference(Control.BackgroundProperty, "PopoverAccentSoftBrush");
        }

        var pin = Ui.IconButton(
            network.IsPinned ? Glyph.Pinned : Glyph.Pin,
            network.IsPinned ? PopoverContext.L("Unpin") : PopoverContext.L("Pin to top"),
            () => wifi.TogglePin(network),
            11);
        pin.Width = 24;
        pin.Height = 24;
        if (network.IsPinned)
        {
            pin.SetResourceReference(Control.ForegroundProperty, Ui.Accent);
        }

        var detail = new Popup
        {
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -220,
            VerticalOffset = 2,
            Child = DetailCard(network),
        };

        var info = Ui.IconButton(Glyph.Info, PopoverContext.L("Details"), () => detail.IsOpen = !detail.IsOpen, 12);
        info.Width = 24;
        info.Height = 24;
        detail.PlacementTarget = info;

        var line = Ui.Columns(Ui.Star, Ui.Auto, Ui.Auto);
        line.Children.Add(row.At(0));
        line.Children.Add(pin.At(1));
        line.Children.Add(info.At(2));
        line.Children.Add(detail);
        return line;
    }

    private void OnRowClicked(WifiNetwork network)
    {
        // A secured network Windows has no profile for needs a password first; everything
        // else — saved, open, or a pinned row that is out of range — goes straight through.
        if (network.IsAvailable && network.IsSecured && !network.IsSaved)
        {
            _passwordFor = _passwordFor == network.Ssid ? null : network.Ssid;
            Rebuild();
            return;
        }

        _passwordFor = null;
        _ = _context.Wifi.ConnectAsync(network, null);
    }

    private FrameworkElement BuildPasswordPanel(WifiNetwork network)
    {
        var box = new PasswordBox { MinWidth = 120 };
        box.SetResourceReference(FrameworkElement.StyleProperty, "NfInput");
        box.SetResourceReference(PasswordBox.CaretBrushProperty, Ui.Text);

        void Submit()
        {
            var password = box.Password;
            _passwordFor = null;
            _ = _context.Wifi.ConnectAsync(network, password);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Submit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _passwordFor = null;
                Rebuild();
            }
        };

        var join = Ui.TextButton(PopoverContext.L("Join"), Submit, accent: true).Margin(6, 0, 0, 0);
        var cancel = Ui.TextButton(PopoverContext.L("Cancel"), () =>
        {
            _passwordFor = null;
            Rebuild();
        }, accent: false).Margin(6, 0, 0, 0);

        var grid = Ui.Columns(Ui.Star, Ui.Auto, Ui.Auto);
        grid.Children.Add(box.At(0));
        grid.Children.Add(join.At(1));
        grid.Children.Add(cancel.At(2));

        var caption = Ui.Label(PopoverContext.L("Password for “{0}”", network.Ssid), 11, Ui.Secondary).Margin(0, 0, 0, 4);
        var panel = Ui.Column(caption, grid);
        panel.Margin = new Thickness(34, 2, 8, 8);

        // Focus once the row is on screen, so the user can type straight away.
        panel.Loaded += (_, _) => box.Focus();
        return panel;
    }

    /// <summary>The (i) flyout for a scanned network — port of <c>WifiScanDetailPopover</c>.</summary>
    private static FrameworkElement DetailCard(WifiNetwork network)
    {
        var rows = new StackPanel();

        void Row(string label, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var grid = Ui.Columns(Ui.Fixed(76), Ui.Star);
            grid.Margin = new Thickness(0, 2, 0, 2);
            grid.Children.Add(Ui.Label(label, 11, Ui.Secondary).At(0));
            grid.Children.Add(Ui.Number(value, 11, Ui.Text, FontWeights.Medium).At(1));
            rows.Children.Add(grid);
        }

        Row(PopoverContext.L("ESSID"), network.Ssid);
        Row(PopoverContext.L("Security"), network.Security);
        Row(PopoverContext.L("Band"), network.Band is { } band ? WifiFormat.BandLabel(band) : null);
        Row(PopoverContext.L("Channel"), network.Channel is { } channel ? WifiFormat.ChannelLabel(channel, null) : null);
        Row(PopoverContext.L("RSSI"), network.Rssi is { } rssi ? string.Create(CultureInfo.InvariantCulture, $"{rssi} dBm") : null);
        Row(PopoverContext.L("BSSID"), network.Bssid);

        if (!network.IsAvailable)
        {
            Row(PopoverContext.L("Status"), PopoverContext.L("Not in range"));
        }

        if (network.IsPinned)
        {
            Row(PopoverContext.L("Pinned"), PopoverContext.L("Yes"));
        }

        return Flyout.Frame(rows, 260);
    }
}
