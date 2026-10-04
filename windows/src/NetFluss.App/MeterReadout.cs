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

        _ = secondaryBrush;
    }

    internal void Update(RateTotals totals, bool useBits)
    {
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
