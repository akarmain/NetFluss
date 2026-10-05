// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using NetFluss.Core;
using NetFluss.Core.Vpn;

namespace NetFluss.App.Popover;

/// <summary>
/// VPN — the macOS <c>VPNSectionView</c>: pick a profile and a server, connect, and see the
/// exit country, public address and tunnel address once connected.
/// </summary>
internal sealed class VpnSection : IPopoverSection
{
    private readonly PopoverContext _context;
    private readonly TextBlock _empty = Ui.Wrapping(string.Empty, 11);
    private readonly PopoverPicker _profiles;
    private readonly PopoverPicker _servers;
    private readonly Ellipse _dot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _state = Ui.Label(string.Empty, 11, Ui.Secondary);
    private readonly Button _action;
    private readonly TextBlock _exit = Ui.Label(string.Empty, 11, Ui.Text);
    private readonly Border _country;
    private readonly TextBlock _countryCode = Ui.Label(string.Empty, 9, Ui.Text, FontWeights.SemiBold);
    private readonly TextBlock _publicIp = Ui.Number(string.Empty, 10, Ui.Secondary);
    private readonly FrameworkElement _exitRow;
    private readonly TextBlock _tunnel = Ui.Number(string.Empty, 10, Ui.Secondary);
    private readonly TextBlock _error = Ui.Wrapping(string.Empty, 10, Ui.Red);
    private readonly FrameworkElement _body;
    private Guid? _selected;
    private bool _active;

    internal VpnSection(PopoverContext context)
    {
        _context = context;
        context.Vpn.Changed += (_, _) =>
        {
            if (_active)
            {
                Refresh();
            }
        };

        _profiles = new PopoverPicker(index =>
        {
            _selected = _context.Vpn.Profiles.ElementAtOrDefault(index)?.Id;
            Refresh();
        });

        _servers = new PopoverPicker(index =>
        {
            if (Current() is { } profile)
            {
                _context.Vpn.SelectServer(profile, index);
            }
        });

        _action = Ui.TextButton(string.Empty, Toggle, accent: false);
        _action.Padding = new Thickness(10, 3, 10, 3);

        var status = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
        status.Children.Add(_dot.Margin(0, 0, 8, 0).At(0));
        status.Children.Add(_state.At(1));
        status.Children.Add(_action.At(2));

        _country = Ui.Badge(_countryCode);
        _country.Margin = new Thickness(0, 0, 6, 0);
        _exitRow = Ui.Row(_country, _exit, _publicIp.Margin(6, 0, 0, 0));

        _body = Ui.Column(
            _profiles.View.Margin(0, 0, 0, 6),
            _servers.View.Margin(0, 0, 0, 6),
            status,
            _exitRow.Margin(0, 6, 0, 0),
            _tunnel.Margin(0, 2, 0, 0),
            _error.Margin(0, 6, 0, 0));

        var title = Ui.SectionTitle("VPN");
        var content = Ui.Column(_empty, _body);
        content.Margin = new Thickness(14, 0, 14, 10);
        View = Ui.Column(title, content);
    }

    public PopoverSection Kind => PopoverSection.Vpn;

    public FrameworkElement View { get; }

    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            Refresh();
        }
    }

    /// <summary>The chosen profile, else the connected one, else the first — as on macOS.</summary>
    private VpnProfile? Current()
    {
        var vpn = _context.Vpn;
        return (_selected is { } id ? vpn.Find(id) : null)
               ?? (vpn.Status.ProfileId is { } active ? vpn.Find(active) : null)
               ?? vpn.Profiles.FirstOrDefault();
    }

    private bool IsActive(VpnProfile profile) => _context.Vpn.Status.ProfileId == profile.Id && _context.Vpn.Status.State.IsActive();

    private void Toggle()
    {
        if (Current() is not { } profile)
        {
            return;
        }

        if (IsActive(profile))
        {
            _ = _context.Vpn.DisconnectAsync();
        }
        else
        {
            _context.Vpn.Connect(profile);
        }
    }

    public void Refresh()
    {
        var vpn = _context.Vpn;
        var profile = Current();
        _empty.Text = PopoverContext.L("No VPN profiles. Add one in Preferences → VPN.");
        _empty.Visibility = profile is null ? Visibility.Visible : Visibility.Collapsed;
        _body.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
        if (profile is null)
        {
            return;
        }

        _profiles.Set([.. vpn.Profiles.Select(p => p.Name)], vpn.Profiles.ToList().FindIndex(p => p.Id == profile.Id));
        _profiles.View.Visibility = vpn.Profiles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        var active = IsActive(profile);
        _servers.Set([.. profile.Servers.Select(s => s.Label)], profile.SelectedServerIndex);
        _servers.View.Visibility = profile.Servers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _servers.IsEnabled = !active;

        var status = vpn.Status;
        var mine = status.ProfileId == profile.Id;
        var state = mine ? status.State : VpnState.Idle;
        _dot.SetResourceReference(Shape.FillProperty, state switch
        {
            VpnState.Connected => Ui.Green,
            VpnState.Connecting or VpnState.Reconnecting or VpnState.Disconnecting => Ui.Orange,
            VpnState.Failed => Ui.Red,
            _ => Ui.Tertiary,
        });
        _state.Text = PopoverContext.L(state switch
        {
            VpnState.Connecting => "Connecting…",
            VpnState.Reconnecting => "Reconnecting…",
            VpnState.Disconnecting => "Disconnecting…",
            VpnState.Connected => "Connected",
            VpnState.Failed => "Failed",
            _ => "Not connected",
        });

        _action.Content = PopoverContext.L(active ? "Disconnect" : "Connect");
        _action.IsEnabled = !status.State.IsBusy() || active;
        _action.SetResourceReference(FrameworkElement.StyleProperty, active ? "NfSubtleButton" : "NfAccentButton");

        // Exit location: country, public address, then the tunnel's own address.
        var connected = mine && state == VpnState.Connected;
        var publicAddress = _context.Monitor.PublicAddress;
        var code = publicAddress?.CountryCode;
        _countryCode.Text = code ?? string.Empty;
        _country.Visibility = code is null ? Visibility.Collapsed : Visibility.Visible;
        _exit.Text = code is null ? string.Empty : CountryName(code);
        _publicIp.Text = publicAddress?.Address ?? string.Empty;
        _exitRow.Visibility = connected && publicAddress is not null ? Visibility.Visible : Visibility.Collapsed;

        _tunnel.Text = status.AssignedIp is { } ip ? PopoverContext.L("Tunnel {0}", ip) : string.Empty;
        _tunnel.Visibility = connected && status.AssignedIp is not null ? Visibility.Visible : Visibility.Collapsed;

        _error.Text = mine && state == VpnState.Failed ? status.Error ?? string.Empty : string.Empty;
        _error.Visibility = _error.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string CountryName(string code)
    {
        try
        {
            return new RegionInfo(code).DisplayName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }
}
