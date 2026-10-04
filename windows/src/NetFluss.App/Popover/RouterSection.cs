// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NetFluss.Core;

namespace NetFluss.App.Popover;

/// <summary>
/// Router — the macOS <c>FritzBoxSection</c>, <c>UniFiSection</c>, <c>OpenWRTSection</c> and
/// <c>OPNsenseSection</c>: router-wide WAN download and upload for each enabled router, with
/// a bar against the line's maximum when the router reports one.
/// </summary>
internal sealed class RouterSection : IPopoverSection
{
    private readonly PopoverContext _context;
    private readonly StackPanel _panel = new();
    private readonly Dictionary<RouterKind, Block> _blocks = [];
    private IDisposable? _lease;
    private List<RouterKind> _shown = [];

    internal RouterSection(PopoverContext context)
    {
        _context = context;
        foreach (var kind in Enum.GetValues<RouterKind>())
        {
            _blocks[kind] = new Block(kind);
        }

        context.Routers.Changed += (_, _) =>
        {
            if (_lease is not null)
            {
                Refresh();
            }
        };
        View = _panel;
    }

    public PopoverSection Kind => PopoverSection.Router;

    public FrameworkElement View { get; }

    public void SetActive(bool active)
    {
        if (active)
        {
            _lease ??= _context.Routers.Acquire();
            Refresh();
        }
        else
        {
            _lease?.Dispose();
            _lease = null;
        }
    }

    public void Refresh()
    {
        var enabled = Enum.GetValues<RouterKind>().Where(_context.Routers.IsEnabled).ToList();
        foreach (var kind in enabled)
        {
            _blocks[kind].Update(_context.Routers.State(kind), _context.UseBits);
        }

        if (!enabled.SequenceEqual(_shown))
        {
            _shown = enabled;
            _panel.Children.Clear();
            foreach (var kind in enabled)
            {
                if (_panel.Children.Count > 0)
                {
                    _panel.Children.Add(Ui.Divider());
                }

                _panel.Children.Add(_blocks[kind].View);
            }
        }
    }

    /// <summary>One router's block, built once and updated in place.</summary>
    private sealed class Block
    {
        private readonly RouterKind _kind;
        private readonly Row _download = new(Glyph.Down, Ui.Download, "Download");
        private readonly Row _upload = new(Glyph.Up, Ui.Upload, "Upload");
        private readonly TextBlock _waiting = Ui.Label(string.Empty, 11, Ui.Secondary);
        private readonly TextBlock _errorText = Ui.Wrapping(string.Empty, 11);
        private readonly FrameworkElement _error;
        private readonly FrameworkElement _rates;

        internal Block(RouterKind kind)
        {
            _kind = kind;
            _rates = Ui.Column(_download.View, _upload.View.Margin(0, 4, 0, 0));

            var icon = Ui.Icon(Glyph.Warning, 11, Ui.Orange).Margin(0, 1, 6, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            var error = Ui.Columns(Ui.Auto, Ui.Star);
            error.Children.Add(icon.At(0));
            error.Children.Add(_errorText.At(1));
            _error = error;

            var title = Ui.SectionTitle(kind.DisplayName());
            title.Margin = new Thickness(14, 8, 14, 6);
            var body = Ui.Column(_rates, _waiting, _error.Margin(0, 6, 0, 0));
            body.Margin = new Thickness(14, 0, 14, 8);
            View = Ui.Column(title, body);
        }

        internal FrameworkElement View { get; }

        internal void Update(RouterState state, bool useBits)
        {
            if (state.Bandwidth is { } bandwidth)
            {
                _download.Update(bandwidth.RxRate, bandwidth.MaxDownBits, useBits);
                _upload.Update(bandwidth.TxRate, bandwidth.MaxUpBits, useBits);
            }

            // A Fritz!Box keeps showing its last reading under a warning; the other routers
            // replace the reading with the error, as on macOS.
            var showRates = state.Bandwidth is not null && (state.Error is null || _kind == RouterKind.FritzBox);
            _rates.Visibility = showRates ? Visibility.Visible : Visibility.Collapsed;

            _waiting.Text = PopoverContext.L(_kind is RouterKind.OpenWrt or RouterKind.OpnSense ? "Gathering data…" : "Connecting…");
            _waiting.Visibility = state.Bandwidth is null && state.Error is null ? Visibility.Visible : Visibility.Collapsed;

            _errorText.Text = state.Error ?? string.Empty;
            _error.Visibility = state.Error is null ? Visibility.Collapsed : Visibility.Visible;
            _error.Margin = new Thickness(0, showRates ? 6 : 0, 0, 0);
        }
    }

    /// <summary>"↓ Download   4.2 MB/s / 125 MB/s" over a utilisation bar.</summary>
    private sealed class Row
    {
        private readonly TextBlock _rate = Ui.Number(string.Empty, 11, Ui.Text, FontWeights.Medium);
        private readonly TextBlock _max = Ui.Number(string.Empty, 9, Ui.Tertiary);
        private readonly Grid _track;
        private readonly Border _fill;
        private double _fraction;

        internal Row(string glyph, string brush, string label)
        {
            var icon = Ui.Icon(glyph, 9, brush);
            icon.Width = 12;
            icon.Margin = new Thickness(0, 0, 6, 0);
            var line = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto, Ui.Auto);
            line.Children.Add(icon.At(0));
            line.Children.Add(Ui.Label(PopoverContext.L(label), 10, Ui.Secondary).At(1));
            line.Children.Add(_rate.At(2));
            line.Children.Add(_max.Margin(4, 0, 0, 0).At(3));

            (_track, _fill) = Ui.Bar(brush);
            _track.SizeChanged += (_, _) => Ui.SetFraction(_track, _fill, _fraction);
            View = Ui.Column(line, _track);
        }

        internal FrameworkElement View { get; }

        /// <summary><paramref name="bytesPerSecond"/> against a maximum in bits per second.</summary>
        internal void Update(double bytesPerSecond, ulong maxBits, bool useBits)
        {
            _rate.Text = RateFormatter.FormatRate(bytesPerSecond, useBits);
            _max.Text = maxBits > 0 ? "/ " + MaxRate(maxBits, useBits) : string.Empty;
            _track.Visibility = maxBits > 0 ? Visibility.Visible : Visibility.Collapsed;
            _fraction = maxBits > 0 ? Math.Min(1, bytesPerSecond * 8 / maxBits) : 0;
            Ui.SetFraction(_track, _fill, _fraction);
        }

        /// <summary>The line maximum, rounded the macOS way: whole units, bits or bytes.</summary>
        internal static string MaxRate(ulong bits, bool useBits)
        {
            var culture = CultureInfo.InvariantCulture;
            if (useBits)
            {
                return bits >= 1_000_000_000 ? string.Format(culture, "{0:F0} Gb/s", bits / 1e9)
                    : bits >= 1_000_000 ? string.Format(culture, "{0:F0} Mb/s", bits / 1e6)
                    : string.Format(culture, "{0:F0} Kb/s", bits / 1e3);
            }

            var bytes = bits / 8.0;
            return bytes >= 1e9 ? string.Format(culture, "{0:F0} GB/s", bytes / 1e9)
                : bytes >= 1e6 ? string.Format(culture, "{0:F0} MB/s", bytes / 1e6)
                : string.Format(culture, "{0:F0} KB/s", bytes / 1e3);
        }
    }
}
