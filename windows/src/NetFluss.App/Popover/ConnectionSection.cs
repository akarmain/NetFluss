// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetFluss.Core;

namespace NetFluss.App.Popover;

/// <summary>
/// The address section, in either of its two macOS forms: the connection flow
/// (this PC → router → VPN → internet) or a plain list with copy buttons.
/// </summary>
internal sealed class ConnectionSection : IPopoverSection
{
    private const string Dash = "—";

    private readonly PopoverContext _context;
    private readonly Grid _root = new();

    private readonly FlowNode _internal;
    private readonly FlowNode _router;
    private readonly FlowNode _vpn;
    private readonly FlowNode _external;
    private readonly FrameworkElement _vpnArrow;
    private readonly Grid _flow;

    private readonly ListRow _listExternal;
    private readonly ListRow _listInternal;
    private readonly ListRow _listRouter;
    private readonly StackPanel _list;

    internal ConnectionSection(PopoverContext context)
    {
        _context = context;

        _internal = new FlowNode(Glyph.Laptop, PopoverContext.L("Internal"), Color.FromRgb(0x3B, 0x82, 0xF6));
        _router = new FlowNode(Glyph.Router, PopoverContext.L("Router"), Color.FromRgb(0xF5, 0x9E, 0x0B));
        _vpn = new FlowNode(Glyph.Shield, "VPN", Color.FromRgb(0x8B, 0x5C, 0xF6));
        _external = new FlowNode(Glyph.Globe, PopoverContext.L("External"), Color.FromRgb(0x22, 0xC5, 0x5E));

        // Star columns for the nodes and fixed ones for the chevrons, so the nodes share the
        // width evenly however many of them there are.
        _flow = Ui.Columns(Ui.Star, Ui.Fixed(14), Ui.Star, Ui.Fixed(14), Ui.Star, Ui.Fixed(14), Ui.Star);
        _flow.Margin = new Thickness(12, 10, 12, 10);
        _flow.Children.Add(_internal.View.At(0));
        _flow.Children.Add(Chevron().At(1));
        _flow.Children.Add(_router.View.At(2));
        _flow.Children.Add(Chevron().At(3));
        _flow.Children.Add(_vpn.View.At(4));
        _vpnArrow = Chevron().At(5);
        _flow.Children.Add(_vpnArrow);
        _flow.Children.Add(_external.View.At(6));

        _listExternal = new ListRow(Glyph.Globe, PopoverContext.L("External"));
        _listInternal = new ListRow(Glyph.Network, PopoverContext.L("Internal"));
        _listRouter = new ListRow(Glyph.Router, PopoverContext.L("Router"));
        _list = Ui.Column(_listExternal.View, _listInternal.View, _listRouter.View);
        _list.Margin = new Thickness(12, 8, 12, 8);

        _root.Children.Add(_flow);
        _root.Children.Add(_list);
        View = _root;
    }

    public PopoverSection Kind => PopoverSection.Connection;

    public FrameworkElement View { get; }

    public void SetActive(bool active)
    {
    }

    public void Refresh()
    {
        var addresses = _context.Monitor.Addresses;
        var external = _context.Monitor.PublicAddress;

        var internalIp = addresses.InternalIp ?? Dash;
        var gateway = addresses.GatewayIp ?? Dash;
        var publicIp = external?.Address ?? Dash;

        var flow = _context.Settings.ConnectionMode == ConnectionDisplayMode.Flow;
        _flow.Visibility = flow ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = flow ? Visibility.Collapsed : Visibility.Visible;

        if (!flow)
        {
            _listExternal.Set(publicIp);
            _listInternal.Set(internalIp);
            _listRouter.Set(gateway);
            return;
        }

        _internal.Set(internalIp);
        _router.Set(gateway);
        _external.Set(publicIp);

        // The VPN node only appears while a tunnel is actually up with an address, and the
        // grid collapses its column rather than leaving a gap where it would have been.
        var tunnels = addresses.Tunnels;
        var hasVpn = tunnels.Count > 0;
        _vpn.View.Visibility = hasVpn ? Visibility.Visible : Visibility.Collapsed;
        _vpnArrow.Visibility = _vpn.View.Visibility;
        _flow.ColumnDefinitions[4].Width = hasVpn ? Ui.Star : Ui.Fixed(0);
        _flow.ColumnDefinitions[5].Width = hasVpn ? Ui.Fixed(14) : Ui.Fixed(0);

        if (hasVpn)
        {
            _vpn.Label = tunnels.Count == 1
                ? tunnels[0].Name
                : PopoverContext.L("{0} VPNs", tunnels.Count);
            _vpn.Set(string.Join(", ", tunnels.Select(t => t.Address)));

            // macOS draws the exit country's flag emoji here. Windows has no flag glyphs in
            // its emoji font — they render as two boxed letters — so the ISO code itself is
            // shown instead, which reads the same and cannot render as a fault.
            _vpn.CountryCode = external?.CountryCode;
        }
    }

    private static FrameworkElement Chevron()
    {
        var chevron = Ui.Icon(Glyph.ChevronRight, 9, Ui.Tertiary);
        chevron.HorizontalAlignment = HorizontalAlignment.Center;
        return chevron;
    }

    /// <summary>One node of the flow: a tinted tile that copies its address when clicked.</summary>
    private sealed class FlowNode
    {
        private readonly TextBlock _icon;
        private readonly TextBlock _country;
        private readonly TextBlock _label;
        private readonly TextBlock _detail;
        private readonly Button _button;
        private readonly string _glyph;
        private string _value = Dash;

        internal FlowNode(string glyph, string label, Color tint)
        {
            _glyph = glyph;
            _icon = Ui.Icon(glyph, 15);
            _icon.Height = 18;

            _country = Ui.Label(string.Empty, 12, Ui.Text, FontWeights.Bold);
            _country.HorizontalAlignment = HorizontalAlignment.Center;
            _country.Height = 18;
            _country.Visibility = Visibility.Collapsed;

            _label = Ui.Label(label, 10, Ui.Text, FontWeights.SemiBold);
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.Margin = new Thickness(0, 3, 0, 0);

            _detail = Ui.Number(Dash, 10, Ui.Secondary);
            _detail.HorizontalAlignment = HorizontalAlignment.Center;

            var tile = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 6, 4, 6),
                Background = new SolidColorBrush(Color.FromArgb(0x26, tint.R, tint.G, tint.B)),
                Child = Ui.Column(new Grid { Children = { _icon, _country } }, _label, _detail),
            };

            _button = new Button
            {
                Content = tile,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                Template = PlainTemplate(),
            };

            _button.Click += (_, _) =>
            {
                if (_value != Dash && Ui.Copy(_value))
                {
                    ShowCopied();
                }
            };

            View = _button;
        }

        internal FrameworkElement View { get; }

        internal string Label
        {
            set => _label.Text = value;
        }

        internal string? CountryCode
        {
            set
            {
                var show = !string.IsNullOrEmpty(value);
                _country.Text = value ?? string.Empty;
                _country.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                _icon.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        internal void Set(string value)
        {
            _value = value;
            _detail.Text = value;
            _button.ToolTip = value == Dash ? null : PopoverContext.L("Click to copy {0}", value);
        }

        /// <summary>A check in place of the icon for a moment, the macOS copy feedback.</summary>
        private void ShowCopied()
        {
            _icon.Text = Glyph.Check;
            _icon.SetResourceReference(TextBlock.ForegroundProperty, Ui.Green);

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _icon.Text = _glyph;
                _icon.SetResourceReference(TextBlock.ForegroundProperty, Ui.Text);
            };

            timer.Start();
        }

        /// <summary>A button that draws only its content, with a hover lift.</summary>
        private static ControlTemplate PlainTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            template.VisualTree = presenter;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
            template.Triggers.Add(hover);
            return template;
        }
    }

    /// <summary>One row of the list view: icon, label, address, copy button.</summary>
    private sealed class ListRow
    {
        private readonly TextBlock _value;
        private readonly Button _copy;
        private string _address = Dash;

        internal ListRow(string glyph, string label)
        {
            var icon = Ui.Icon(glyph, 11, Ui.Secondary);
            icon.Width = 18;
            icon.Margin = new Thickness(0, 0, 6, 0);

            var caption = Ui.Label(label, 11, Ui.Secondary);
            _value = Ui.Number(Dash, 11, Ui.Text, FontWeights.Medium);

            _copy = Ui.IconButton(Glyph.Copy, PopoverContext.L("Copy"), () =>
            {
                if (_address != Dash)
                {
                    Ui.CopyWithFeedback(_address, _copy!, Glyph.Copy);
                }
            }, 10);
            _copy.Width = 22;
            _copy.Height = 22;

            var grid = Ui.Columns(Ui.Auto, Ui.Fixed(64), Ui.Star, Ui.Auto);
            grid.Margin = new Thickness(0, 1, 0, 1);
            grid.Children.Add(icon.At(0));
            grid.Children.Add(caption.At(1));
            grid.Children.Add(_value.At(2));
            grid.Children.Add(_copy.At(3));
            View = grid;
        }

        internal FrameworkElement View { get; }

        internal void Set(string address)
        {
            _address = address;
            _value.Text = address;
            _copy.IsEnabled = address != Dash;
        }
    }
}
