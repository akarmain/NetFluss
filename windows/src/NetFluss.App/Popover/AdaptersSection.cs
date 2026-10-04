// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NetFluss.Core;

namespace NetFluss.App.Popover;

/// <summary>One card per visible adapter — port of the macOS <c>AdapterCard</c> list.</summary>
internal sealed class AdaptersSection : IPopoverSection
{
    private readonly PopoverContext _context;
    private readonly StackPanel _list = new() { Margin = new Thickness(12, 10, 12, 10) };
    private readonly TextBlock _empty;
    private readonly Dictionary<string, AdapterCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reconnecting = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _order = [];

    internal AdaptersSection(PopoverContext context)
    {
        _context = context;
        _empty = Ui.Label(PopoverContext.L("No active adapters"), 12, Ui.Secondary);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Margin = new Thickness(0, 14, 0, 14);

        View = new Grid { Children = { _list, _empty } };
    }

    public PopoverSection Kind => PopoverSection.Adapters;

    public FrameworkElement View { get; }

    public void SetActive(bool active)
    {
    }

    public void Refresh()
    {
        var adapters = _context.Monitor.Adapters.ToList();
        var ids = adapters.Select(a => a.Id).ToList();

        _empty.Visibility = adapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = adapters.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Re-lay the list only when the set or order changed; otherwise update the cards
        // that are already there, so a hovered button or an open detail flyout survives.
        if (!ids.SequenceEqual(_order, StringComparer.OrdinalIgnoreCase))
        {
            _list.Children.Clear();
            foreach (var adapter in adapters)
            {
                if (!_cards.TryGetValue(adapter.Id, out var card))
                {
                    card = new AdapterCard(this);
                    _cards[adapter.Id] = card;
                }

                card.View.Margin = new Thickness(0, _list.Children.Count == 0 ? 0 : 6, 0, 0);
                _list.Children.Add(card.View);
            }

            foreach (var gone in _cards.Keys.Except(ids, StringComparer.OrdinalIgnoreCase).ToList())
            {
                _cards.Remove(gone);
            }

            _order = ids;
        }

        foreach (var adapter in adapters)
        {
            _cards[adapter.Id].Update(adapter);
        }
    }

    private async void Reconnect(AdapterStatus adapter)
    {
        if (!_reconnecting.Add(adapter.Id))
        {
            return;
        }

        Refresh();
        try
        {
            await _context.Privileged.RestartAdapterAsync(adapter);
        }
        finally
        {
            _reconnecting.Remove(adapter.Id);
            Refresh();
        }
    }

    /// <summary>A single adapter card, built once and updated in place.</summary>
    private sealed class AdapterCard
    {
        private readonly AdaptersSection _owner;
        private readonly TextBlock _icon;
        private readonly TextBlock _title;
        private readonly Button _reconnect;
        private readonly TextBlock _busy;
        private readonly Button _info;
        private readonly Border _badge;
        private readonly TextBlock _badgeText;
        private readonly TextBlock _download;
        private readonly TextBlock _upload;
        private readonly TextBlock _mode;
        private readonly Popup _detail;
        private AdapterStatus? _adapter;

        internal AdapterCard(AdaptersSection owner)
        {
            _owner = owner;

            _icon = Ui.Icon(Glyph.Network, 13);
            _icon.Width = 18;
            _icon.Margin = new Thickness(0, 0, 6, 0);

            _title = Ui.Label(string.Empty, 12, Ui.Text, FontWeights.SemiBold);

            _reconnect = Ui.IconButton(Glyph.Refresh, PopoverContext.L("Reconnect"), () =>
            {
                if (_adapter is not null)
                {
                    _owner.Reconnect(_adapter);
                }
            }, 11);
            _reconnect.Width = 22;
            _reconnect.Height = 22;

            _busy = Ui.Icon(Glyph.Refresh, 11, Ui.Secondary);
            _busy.Width = 22;
            _busy.Opacity = 0.5;
            _busy.Visibility = Visibility.Collapsed;

            _detail = new Popup
            {
                StaysOpen = false,
                AllowsTransparency = true,
                Placement = PlacementMode.Bottom,
                HorizontalOffset = -200,
                VerticalOffset = 4,
            };

            _info = Ui.IconButton(Glyph.Info, PopoverContext.L("Wi-Fi Details"), ToggleDetail, 12);
            _info.Width = 22;
            _info.Height = 22;
            _detail.PlacementTarget = _info;

            _badgeText = Ui.Label(string.Empty, 10, Ui.Secondary);
            _badge = Ui.Badge(_badgeText);
            _badge.Margin = new Thickness(4, 0, 0, 0);

            var header = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto, Ui.Auto, Ui.Auto);
            header.Children.Add(_icon.At(0));
            header.Children.Add(_title.At(1));
            header.Children.Add(new Grid { Children = { _reconnect, _busy } }.At(2));
            header.Children.Add(_info.At(3));
            header.Children.Add(_badge.At(4));

            _download = Ui.Number("—", 11);
            _upload = Ui.Number("—", 11);
            _mode = Ui.Label(string.Empty, 10, Ui.Secondary);

            var downIcon = Ui.Icon(Glyph.Down, 9, Ui.Download);
            downIcon.Margin = new Thickness(0, 0, 4, 0);
            var upIcon = Ui.Icon(Glyph.Up, 9, Ui.Upload);
            upIcon.Margin = new Thickness(12, 0, 4, 0);

            var rates = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
            rates.Margin = new Thickness(24, 4, 0, 0);
            rates.Children.Add(Ui.Row(downIcon, _download, upIcon, _upload).At(0));
            rates.Children.Add(_mode.At(2));

            _detail.Child = DetailCard();

            View = Ui.Card(Ui.Column(header, rates, _detail));
        }

        internal Border View { get; }

        internal void Update(AdapterStatus adapter)
        {
            _adapter = adapter;
            var useBits = _owner._context.UseBits;
            var settings = _owner._context.Settings;

            _icon.Text = Glyph.ForAdapter(adapter.Type);
            _icon.SetResourceReference(TextBlock.ForegroundProperty, adapter.IsUp ? Ui.Text : Ui.Secondary);

            // A user's own label beats the SSID beats the Windows connection name — the macOS
            // precedence. The monitor has already substituted any custom name into
            // DisplayName, so "has a custom name" is asked of the settings directly.
            var hasCustomName = settings.AdapterCustomNames.TryGetValue(adapter.Id, out var custom) &&
                                !string.IsNullOrWhiteSpace(custom);
            _title.Text = !hasCustomName && adapter.Type == AdapterType.WiFi && !string.IsNullOrEmpty(adapter.WifiSsid)
                ? adapter.WifiSsid
                : adapter.DisplayName;
            _title.ToolTip = adapter.Description;

            _download.Text = RateFormatter.FormatRate(adapter.RxRateBps, useBits);
            _upload.Text = RateFormatter.FormatRate(adapter.TxRateBps, useBits);

            var link = adapter.Type switch
            {
                AdapterType.WiFi when adapter.WifiTxRateMbps is not null => RateFormatter.FormatMbps(adapter.WifiTxRateMbps),
                AdapterType.Ethernet when adapter.LinkSpeedBps is not null => RateFormatter.FormatLinkSpeed(adapter.LinkSpeedBps, useBits: true),
                _ => string.Empty,
            };

            _badgeText.Text = link;
            _badge.Visibility = link.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            _mode.Text = adapter.Type switch
            {
                AdapterType.WiFi => adapter.Wifi?.Band is { } band ? WifiFormat.BandLabel(band) : PopoverContext.L("Wi-Fi"),
                AdapterType.Ethernet => PopoverContext.L("Ethernet"),
                _ => adapter.IsTunnel ? "VPN" : string.Empty,
            };

            var reconnecting = _owner._reconnecting.Contains(adapter.Id);
            var canReconnect = adapter.Type != AdapterType.Other && adapter.IsUp;
            _reconnect.Visibility = canReconnect && !reconnecting ? Visibility.Visible : Visibility.Collapsed;
            _busy.Visibility = reconnecting ? Visibility.Visible : Visibility.Collapsed;

            _info.Visibility = adapter.Type == AdapterType.WiFi && adapter.Wifi is not null
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (_detail.IsOpen)
            {
                _detail.Child = DetailCard();
            }
        }

        private void ToggleDetail()
        {
            _detail.Child = DetailCard();
            _detail.IsOpen = !_detail.IsOpen;
        }

        /// <summary>
        /// The (i) flyout — port of the macOS <c>WifiDetailPopover</c>. Noise and SNR are left
        /// out because Windows does not report a noise floor; a row reading "—" forever would
        /// look like a fault.
        /// </summary>
        private FrameworkElement DetailCard()
        {
            var detail = _adapter?.Wifi;
            var rows = new StackPanel();

            void Row(string label, string? value, bool copyable = false)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }

                var grid = Ui.Columns(Ui.Fixed(76), Ui.Star, Ui.Auto);
                grid.Margin = new Thickness(0, 2, 0, 2);
                grid.Children.Add(Ui.Label(label, 11, Ui.Secondary).At(0));
                grid.Children.Add(Ui.Number(value, 11, Ui.Text, FontWeights.Medium).At(1));

                if (copyable)
                {
                    Button? copy = null;
                    copy = Ui.IconButton(Glyph.Copy, PopoverContext.L("Copy"), () => Ui.CopyWithFeedback(value, copy!, Glyph.Copy), 10);
                    copy.Width = 20;
                    copy.Height = 20;
                    grid.Children.Add(copy.At(2));
                }

                rows.Children.Add(grid);
            }

            if (detail is not null)
            {
                Row(PopoverContext.L("Standard"), detail.PhyMode);
                Row(PopoverContext.L("Security"), detail.Security);
                Row(PopoverContext.L("Channel"), detail.Channel is { } channel ? WifiFormat.ChannelLabel(channel, detail.Band) : null);
                Row(PopoverContext.L("RSSI"), detail.Rssi is { } rssi ? string.Create(CultureInfo.InvariantCulture, $"{rssi} dBm") : null);
                Row(PopoverContext.L("Signal"), detail.SignalQuality is { } quality ? string.Create(CultureInfo.InvariantCulture, $"{quality} %") : null);
                Row(PopoverContext.L("ESSID"), detail.Ssid);
                Row(PopoverContext.L("BSSID"), detail.Bssid, copyable: true);
                Row(PopoverContext.L("Tx Rate"), detail.TxRateMbps is { } tx and > 0 ? string.Create(CultureInfo.InvariantCulture, $"{tx:0} Mbps") : null);
                Row(PopoverContext.L("Rx Rate"), detail.RxRateMbps is { } rx and > 0 ? string.Create(CultureInfo.InvariantCulture, $"{rx:0} Mbps") : null);
            }

            return Flyout.Frame(rows, 260);
        }
    }
}
