// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Serialization;

namespace NetFluss.Core.Vpn;

/// <summary>
/// The VPN technologies NetFluss can manage — the macOS <c>VPNProtocolKind</c>. OpenVPN and
/// WireGuard run through the NetFluss helper using the installed OpenVPN and WireGuard for
/// Windows; IKEv2 (and L2TP) use Windows' own VPN stack.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<VpnProtocol>))]
public enum VpnProtocol
{
    OpenVpn,
    WireGuard,
    Ikev2,
}

public static class VpnProtocols
{
    public static string DisplayName(this VpnProtocol protocol) => protocol switch
    {
        VpnProtocol.OpenVpn => "OpenVPN",
        VpnProtocol.WireGuard => "WireGuard",
        _ => "IKEv2 / IPsec",
    };

    /// <summary>Whether the helper runs a tool for this kind (vs. Windows' own VPN stack).</summary>
    public static bool UsesHelper(this VpnProtocol protocol) => protocol is VpnProtocol.OpenVpn or VpnProtocol.WireGuard;
}

/// <summary>One selectable server of a profile; provider bundles ship one config per server.</summary>
public sealed record VpnServer
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>"Zurich TCP", or the source file name.</summary>
    public string Label { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    public int? Port { get; init; }

    /// <summary>"udp"/"tcp" for OpenVPN.</summary>
    public string? Transport { get; init; }

    /// <summary>The config file (relative to the profile directory) this server connects with.</summary>
    public string? ConfigFileName { get; init; }
}

public sealed record VpnProfileOptions
{
    public bool AutoReconnect { get; init; }

    public bool ConnectOnLaunch { get; init; }

    /// <summary>Apply one of the DNS presets while connected; restored on disconnect.</summary>
    public bool UseProfileDns { get; init; }

    public string? DnsPresetId { get; init; }
}

/// <summary>
/// A saved VPN configuration — the macOS <c>VPNProfile</c>. Secrets are never stored here,
/// only a Credential Manager account; imported config files live in the profile's directory.
/// </summary>
public sealed record VpnProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public VpnProtocol Kind { get; init; }

    public string ConfigFileName { get; init; } = string.Empty;

    public IReadOnlyList<VpnServer> Servers { get; init; } = [];

    public int SelectedServerIndex { get; init; }

    /// <summary>OpenVPN <c>auth-user-pass</c>: connecting needs a username and password.</summary>
    public bool RequiresCredentials { get; init; }

    /// <summary>For IKEv2/L2TP: the Windows VPN connection (phonebook entry) this profile dials.</summary>
    public string? NativeEntryName { get; init; }

    /// <summary>True when NetFluss created that Windows VPN connection, so deleting the profile removes it too.</summary>
    public bool OwnsNativeEntry { get; init; }

    public string? Ikev2Server { get; init; }

    public string? Ikev2Username { get; init; }

    public VpnProfileOptions Options { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>The Credential Manager account for this profile's username and password.</summary>
    [JsonIgnore]
    public string CredentialAccount => "vpn." + Id.ToString("D");

    [JsonIgnore]
    public VpnServer? SelectedServer => SelectedServerIndex >= 0 && SelectedServerIndex < Servers.Count
        ? Servers[SelectedServerIndex]
        : Servers.FirstOrDefault();
}

public enum VpnState
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
    Failed,
}

public static class VpnStates
{
    public static bool IsBusy(this VpnState state) => state is VpnState.Connecting or VpnState.Reconnecting or VpnState.Disconnecting;

    public static bool IsActive(this VpnState state) => state is VpnState.Connected or VpnState.Connecting or VpnState.Reconnecting;
}

/// <summary>The live state of the one active profile — the macOS <c>VPNRuntimeStatus</c>.</summary>
public sealed record VpnStatus
{
    public static readonly VpnStatus Idle = new();

    public VpnState State { get; init; } = VpnState.Idle;

    public Guid? ProfileId { get; init; }

    public Guid? ServerId { get; init; }

    public DateTimeOffset? ConnectedSince { get; init; }

    public string? AssignedIp { get; init; }

    public ulong BytesIn { get; init; }

    public ulong BytesOut { get; init; }

    /// <summary>Why it failed, for <see cref="VpnState.Failed"/>.</summary>
    public string? Error { get; init; }
}
