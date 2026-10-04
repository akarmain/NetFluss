// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetFluss.Core;

namespace NetFluss.App;

/// <summary>
/// The rate readout used by the taskbar overlay and the floating widget.
///
/// <para>This is the thing the notification area cannot give us. Freed from a 16 px square,
/// the rates can be laid out the way the macOS menu bar lays them out — full units, real
/// hinted text, arrows that cost nothing because there is width to spare.</para>
///
/// <para>Built in code rather than XAML because both hosts size themselves around it and
/// need to measure it before they have a window to lay out in.</para>
/// </summary>
internal sealed class MeterReadout : UserControl
{
    private readonly TextBlock _downloadText = new();
    private readonly TextBlock _uploadText = new();
    private readonly TextBlock _downloadArrow = new() { Text = "↓" };
    private readonly TextBlock _uploadArrow = new() { Text = "↑" };
    private readonly Grid _root = new();

    private ReadoutStyle _style = ReadoutStyle.Unified;

    // The Dashboard styles' own parts: a dark capsule, the ring, and compact numbers.
    private readonly Border _capsule = new()
    {
        CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8, 1, 8, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Background = Frozen(Color.FromArgb(0xC7, 0, 0, 0)),
        BorderBrush = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
    };

    private readonly RingGauge _ring = new();
    private readonly TextBlock _dashTotal = new();
    private readonly TextBlock _dashDown = new();
    private readonly TextBlock _dashUp = new();
    private readonly DashboardRing _ringProgress = new();
    private Color _downloadColor = Colors.DodgerBlue;
    private Color _uploadColor = Colors.SeaGreen;

    /// <summary>The VPN mark and exit country, right of the rates — the 2.6 menu bar accessories.</summary>
    private readonly StackPanel _accessories = new()
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0),
    };

    private double _fontSize = 11;

    internal MeterReadout()
    {
        Focusable = false;
        IsTabStop = false;

        foreach (var block in new[] { _downloadArrow, _downloadText, _uploadArrow, _uploadText })
        {
            block.VerticalAlignment = VerticalAlignment.Center;
            TextOptions.SetTextFormattingMode(block, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(block, TextRenderingMode.ClearType);
        }

        // Tabular figures, so the numbers do not jitter sideways every tick as digits change
        // width. The macOS side gets this from the monospaced font design setting.
        var tabular = new FontFamily("Consolas, Cascadia Mono, Segoe UI");
        _downloadText.FontFamily = tabular;
        _uploadText.FontFamily = tabular;

        // The rates in one column, the accessories in the next, so a mark never shifts the
        // numbers' own alignment and disappears without leaving a gap.
        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_root, 0);
        Grid.SetColumn(_accessories, 1);
        outer.Children.Add(_root);
        outer.Children.Add(_accessories);

        Content = outer;
        BuildLayout();
    }

    /// <summary>
    /// Shows the VPN mark and the exit country after the rates, or clears them.
    ///
    /// <para>The country is shown as its ISO code in a small badge where macOS draws the flag
    /// emoji: Windows' emoji font has no flags and would render two boxed letters.</para>
    /// </summary>
    internal void SetAccessories(MeterAccessories? accessories)
    {
        _accessories.Children.Clear();
        if (accessories is null)
        {
            _accessories.Visibility = Visibility.Collapsed;
            return;
        }

        if (accessories.VpnKind is "dot" or "shield")
        {
            var brush = Freeze(accessories.VpnActive ? accessories.ActiveColor : accessories.IdleColor);
            FrameworkElement mark;

            if (accessories.VpnKind == "dot")
            {
                var diameter = Math.Max(5, Math.Round(_fontSize * 0.6));
                mark = new System.Windows.Shapes.Ellipse
                {
                    Width = diameter,
                    Height = diameter,
                    Fill = accessories.VpnActive ? brush : Brushes.Transparent,
                    Stroke = accessories.VpnActive ? null : brush,
                    StrokeThickness = 1.5,
                };
            }
            else
            {
                mark = new TextBlock
                {
                    Text = accessories.VpnActive ? "" : "",
                    FontFamily = Ui.IconFont,
                    FontSize = _fontSize + 1,
                    Foreground = brush,
                };
            }

            mark.VerticalAlignment = VerticalAlignment.Center;
            mark.ToolTip = Localization.L(accessories.VpnActive ? "VPN connected" : "No VPN connected");
            _accessories.Children.Add(mark);
        }

        if (!string.IsNullOrEmpty(accessories.Country))
        {
            var text = new TextBlock
            {
                Text = accessories.Country,
                FontSize = Math.Max(8, _fontSize - 2),
                FontWeight = FontWeights.SemiBold,
                Foreground = Freeze(accessories.IdleColor),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var badge = new Border
            {
                Child = text,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0, 3, 0),
                BorderThickness = new Thickness(1),
                BorderBrush = Freeze(accessories.IdleColor, 0x66),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(_accessories.Children.Count > 0 ? 4 : 0, 0, 0, 0),
            };

            _accessories.Children.Add(badge);
        }

        _accessories.Visibility = _accessories.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Extra width the accessories need, in DIPs, so the hosts can size around them.</summary>
    internal static double AccessoryWidth(MeterAccessories? accessories, double fontSize)
    {
        if (accessories is null)
        {
            return 0;
        }

        var width = 0.0;
        if (accessories.VpnKind is "dot" or "shield")
        {
            width += 6 + fontSize + 2;
        }

        if (!string.IsNullOrEmpty(accessories.Country))
        {
            width += 6 + (fontSize * 1.8);
        }

        return width;
    }

    internal ReadoutStyle Layout
    {
        get => _style;
        set
        {
            if (_style == value)
            {
                return;
            }

            _style = value;
            BuildLayout();
        }
    }

    internal void ApplyAppearance(double fontSize, ThemeColor download, ThemeColor upload, ThemeColor secondary)
    {
        _fontSize = fontSize;
        var downloadBrush = Freeze(download);
        var uploadBrush = Freeze(upload);
        var secondaryBrush = Freeze(secondary);

        _downloadText.Foreground = downloadBrush;
        _uploadText.Foreground = uploadBrush;
        _downloadArrow.Foreground = downloadBrush;
        _uploadArrow.Foreground = uploadBrush;

        // The stacked style halves the line height, so it needs a smaller face to fit the
        // same taskbar; the macOS stack style does exactly this.
        var effective = _style == ReadoutStyle.Stacked ? Math.Max(8, fontSize - 2) : fontSize;

        foreach (var block in new[] { _downloadText, _uploadText })
        {
            block.FontSize = effective;
        }

        foreach (var block in new[] { _downloadArrow, _uploadArrow })
        {
            block.FontSize = Math.Max(8, effective - 1);
        }

        // The macOS dashboard: numbers a point smaller, the total a point larger and semibold.
        _downloadColor = Color.FromRgb(download.R, download.G, download.B);
        _uploadColor = Color.FromRgb(upload.R, upload.G, upload.B);
        _dashDown.FontSize = _dashUp.FontSize = Math.Max(8, fontSize - 1);
        _dashTotal.FontSize = Math.Min(18, fontSize + 1);
        _ring.Width = _ring.Height = Math.Max(12, Math.Round((fontSize - 1) * 1.35) + 1);
        if (_style is ReadoutStyle.Dashboard or ReadoutStyle.DashboardBasic)
        {
            BuildLayout();
        }

        _ = secondaryBrush;
    }

    internal void Update(RateTotals totals, bool useBits, DashboardMetrics? dashboard = null)
    {
        if (_style is ReadoutStyle.Dashboard or ReadoutStyle.DashboardBasic)
        {
            UpdateDashboard(dashboard ?? DashboardMetrics.Local(totals), useBits);
            return;
        }
        if (_style == ReadoutStyle.Total)
        {
            _downloadText.Text = RateFormatter.FormatRate(totals.RxRateBps + totals.TxRateBps, useBits);
            return;
        }

        _downloadText.Text = RateFormatter.FormatRate(totals.RxRateBps, useBits);
        _uploadText.Text = RateFormatter.FormatRate(totals.TxRateBps, useBits);
    }

    private void BuildLayout()
    {
        _root.Children.Clear();
        _root.ColumnDefinitions.Clear();
        _root.RowDefinitions.Clear();

        switch (_style)
        {
            case ReadoutStyle.Dashboard or ReadoutStyle.DashboardBasic:
                _root.Children.Add(BuildDashboard());
                break;

            case ReadoutStyle.Total:
                _root.Children.Add(Row(_downloadArrow, _downloadText, showArrow: false));
                break;

            case ReadoutStyle.Stacked:
                _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                // Upload above download, matching the tray meter.
                var upper = Row(_uploadArrow, _uploadText, showArrow: true);
                var lower = Row(_downloadArrow, _downloadText, showArrow: true);
                Grid.SetRow(upper, 0);
                Grid.SetRow(lower, 1);
                _root.Children.Add(upper);
                _root.Children.Add(lower);
                break;

            default:
                var unified = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };

                unified.Children.Add(Row(_uploadArrow, _uploadText, showArrow: true));
                unified.Children.Add(new FrameworkElement { Width = 12 });
                unified.Children.Add(Row(_downloadArrow, _downloadText, showArrow: true));
                _root.Children.Add(unified);
                break;
        }
    }

    /// <summary>"◔  Σ 12.4  |  ↓ 11.0  |  ↑ 1.4" in the capsule; Basic drops the ring and the total.</summary>
    private FrameworkElement BuildDashboard()
    {
        var white = Frozen(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        var dim = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        var size = Math.Max(8, _fontSize - 1);

        TextBlock Part(string text, Brush brush, double fontSize)
        {
            var block = new TextBlock { Text = text, Foreground = brush, FontSize = fontSize, VerticalAlignment = VerticalAlignment.Center };
            TextOptions.SetTextFormattingMode(block, TextFormattingMode.Display);
            return block;
        }

        foreach (var block in new[] { _dashTotal, _dashDown, _dashUp })
        {
            (block.Parent as Panel)?.Children.Remove(block);
            block.VerticalAlignment = VerticalAlignment.Center;
            block.FontFamily = _downloadText.FontFamily;
            TextOptions.SetTextFormattingMode(block, TextFormattingMode.Display);
        }

        (_ring.Parent as Panel)?.Children.Remove(_ring);
        _dashTotal.Foreground = white;
        _dashTotal.FontWeight = FontWeights.SemiBold;

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (_style == ReadoutStyle.Dashboard)
        {
            _ring.Margin = new Thickness(0, 0, 6, 0);
            _ring.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(_ring);
            row.Children.Add(Part("Σ ", white, Math.Min(18, _fontSize + 1)));
            row.Children.Add(_dashTotal);
            row.Children.Add(Part("  |  ", dim, size));
        }

        row.Children.Add(Part("↓ ", Frozen(_downloadColor), size));
        row.Children.Add(_dashDown);
        row.Children.Add(Part("  |  ", dim, size));
        row.Children.Add(Part("↑ ", Frozen(_uploadColor), size));
        row.Children.Add(_dashUp);

        _capsule.Child = row;
        _capsule.HorizontalAlignment = HorizontalAlignment.Center;
        return _capsule;
    }

    private void UpdateDashboard(DashboardMetrics metrics, bool useBits)
    {
        var (total, down, up) = metrics.CompactTexts(useBits);
        _dashTotal.Text = total;
        _dashDown.Text = down;
        _dashUp.Text = up;
        _dashDown.Foreground = Frozen(_downloadColor);
        _dashUp.Foreground = Frozen(_uploadColor);
        _capsule.ToolTip = metrics.SourceKey.StartsWith("router:", StringComparison.Ordinal)
            ? Localization.L("Router traffic in {0}", metrics.Unit(useBits))
            : metrics.Unit(useBits);

        if (_style == ReadoutStyle.Dashboard)
        {
            // The ring blends from the download ink to the upload ink by upload's share.
            var share = metrics.Total > 0 ? metrics.Tx / metrics.Total : 0.5;
            _ring.Set(_ringProgress.Progress(metrics, DateTimeOffset.UtcNow), Blend(_downloadColor, _uploadColor, share));
        }
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round(a + ((b - a) * Math.Clamp(amount, 0, 1)));
        return Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static StackPanel Row(TextBlock arrow, TextBlock value, bool showArrow)
    {
        // Rebuild rather than reparent: a TextBlock can only have one parent, and these are
        // reused across layout changes when the user switches style.
        if (arrow.Parent is StackPanel oldArrowParent)
        {
            oldArrowParent.Children.Remove(arrow);
        }

        if (value.Parent is StackPanel oldValueParent)
        {
            oldValueParent.Children.Remove(value);
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (showArrow)
        {
            arrow.Margin = new Thickness(0, 0, 3, 0);
            row.Children.Add(arrow);
        }

        row.Children.Add(value);
        return row;
    }

    private static SolidColorBrush Freeze(ThemeColor color, byte alpha = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>What the meter shows after the rates: the VPN mark and the exit country.</summary>
internal sealed record MeterAccessories(string? VpnKind, bool VpnActive, ThemeColor ActiveColor, ThemeColor IdleColor, string? Country)
{
    /// <summary>
    /// Builds the accessories from the settings and the live state, or null when the user
    /// has neither switched on — the overwhelmingly common case, which must cost nothing.
    /// </summary>
    internal static MeterAccessories? From(AppSettings settings, bool vpnActive, string? country, ThemeColor idle)
    {
        var showVpn = settings.VpnIndicator != "off" && (vpnActive || settings.VpnShowWhenOff);
        var showCountry = settings.ShowCountryFlag && !string.IsNullOrEmpty(country);
        if (!showVpn && !showCountry)
        {
            return null;
        }

        var green = ThemeColor.FromHex("2EA043");
        var active = AccentPalette.Resolve(settings.VpnIndicatorColor, settings.VpnIndicatorColorHex, green) ?? green;

        return new MeterAccessories(showVpn ? settings.VpnIndicator : null, vpnActive, active, idle, showCountry ? country : null);
    }
}

/// <summary>The Dashboard ring: a faint full circle and an arc clockwise from twelve o'clock.</summary>
internal sealed class RingGauge : FrameworkElement
{
    private double _progress;
    private Color _color = Colors.DodgerBlue;

    internal void Set(double progress, Color color)
    {
        if (Math.Abs(progress - _progress) < 0.005 && color == _color)
        {
            return;
        }

        _progress = progress;
        _color = color;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 4)
        {
            return;
        }

        const double Stroke = 2;
        var radius = (size / 2) - (Stroke / 2);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        var track = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), Stroke);
        track.Freeze();
        dc.DrawEllipse(null, track, center, radius, radius);

        if (_progress <= 0)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(_color), Stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        if (_progress >= 0.999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = _progress * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + (radius * Math.Sin(angle)), center.Y - (radius * Math.Cos(angle)));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, _progress > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
