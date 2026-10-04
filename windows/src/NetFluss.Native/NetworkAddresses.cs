// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetFluss.Core;

namespace NetFluss.Native;

/// <summary>An active tunnel and the address it holds, for the flow view's VPN node.</summary>
public sealed record TunnelAddress(string AdapterId, string Name, string Address);

/// <summary>The local half of the connection section: this PC, its router, and any VPN.</summary>
public sealed record LocalAddresses(
    string? InternalIp,
    string? GatewayIp,
    string? PrimaryAdapterId,
    IReadOnlyList<TunnelAddress> Tunnels)
{
    public static readonly LocalAddresses Empty = new(null, null, null, []);
}

/// <summary>
/// Reads the addresses the popover shows, without privileges.
///
/// <para>"Internal" is the address of the physical adapter that carries the default route —
/// what macOS calls the primary service — rather than whichever adapter Windows happens to
/// prefer right now. With a full-tunnel VPN up, the preferred route is the tunnel, and
/// showing its address as "Internal" beside the router would draw a path that does not
/// exist. The tunnel gets its own node instead.</para>
/// </summary>
public static class NetworkAddresses
{
    public static LocalAddresses Read()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return LocalAddresses.Empty;
        }

        var bestIndex = BestInterfaceIndex();

        (NetworkInterface Nic, IPInterfaceProperties Props, int Metric, string Ip, string Gateway)? primary = null;
        var tunnels = new List<TunnelAddress>();

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var ipv4 = props.UnicastAddresses
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !IsLinkLocal(a));

            if (ipv4 is null)
            {
                continue;
            }

            var description = nic.Description ?? string.Empty;
            var isTunnel = nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel ||
                           AdapterClassifier.IsTunnelInterface(0, 0, description);

            var gateway = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

            if (isTunnel)
            {
                tunnels.Add(new TunnelAddress(Guid.TryParse(nic.Id, out var tunnelGuid) ? tunnelGuid.ToString("B") : nic.Id, nic.Name, ipv4.ToString()));
                continue;
            }

            if (gateway is null)
            {
                continue;
            }

            var index = SafeIndex(props);
            var metric = index == bestIndex ? int.MinValue : SafeMetric(nic);

            if (primary is null || metric < primary.Value.Metric)
            {
                primary = (nic, props, metric, ipv4.ToString(), gateway.ToString());
            }
        }

        if (primary is not { } chosen)
        {
            return new LocalAddresses(null, null, null, tunnels);
        }

        var id = Guid.TryParse(chosen.Nic.Id, out var guid) ? guid.ToString("B") : chosen.Nic.Id;
        return new LocalAddresses(chosen.Ip, chosen.Gateway, id, tunnels);
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static int SafeIndex(IPInterfaceProperties props)
    {
        try
        {
            return props.GetIPv4Properties()?.Index ?? -1;
        }
        catch (NetworkInformationException)
        {
            return -1;
        }
    }

    /// <summary>
    /// The interface metric is not exposed by <see cref="NetworkInterface"/>; speed is the
    /// closest proxy for Windows' automatic metric, which ranks faster links first.
    /// </summary>
    private static int SafeMetric(NetworkInterface nic)
    {
        try
        {
            var speed = nic.Speed;
            return speed > 0 ? (int)Math.Max(int.MinValue + 1, -Math.Min(speed / 1_000_000, int.MaxValue)) : 0;
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The interface Windows would route an internet-bound packet through. Asked about a
    /// public address rather than looked up in the routing table, so metrics, VPN split
    /// routes and policy are all already applied.
    /// </summary>
    private static int BestInterfaceIndex()
    {
        try
        {
            // 1.1.1.1 in network byte order. Nothing is sent; this is a routing table query.
            const uint Destination = 0x01010101;
            return GetBestInterface(Destination, out var index) == 0 ? (int)index : -1;
        }
        catch (DllNotFoundException)
        {
            return -1;
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint destAddr, out uint bestIfIndex);
}
