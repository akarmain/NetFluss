// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;

namespace NetFluss.Native;

/// <summary>
/// Per-adapter DNS through <c>SetInterfaceDnsSettings</c> (Windows 10 2004+), the documented
/// API rather than a netsh command line. Needs administrator, so it is called by the helper
/// service; the unelevated app falls back to one elevated netsh run per change.
/// </summary>
public static unsafe class InterfaceDns
{
    private const uint SettingsVersion1 = 1;
    private const ulong SettingIPv6 = 0x0001;
    private const ulong SettingNameServer = 0x0002;

    /// <summary>
    /// Sets the IPv4 and IPv6 resolvers. An empty list for a family puts that family back on
    /// DHCP — both are always written, because a leftover IPv6 resolver keeps answering and
    /// makes a change to IPv4 look like it did nothing.
    /// </summary>
    /// <returns>0 on success, otherwise the Win32 error.</returns>
    public static uint Set(Guid adapter, IReadOnlyList<string> servers)
    {
        var v4 = string.Join(',', servers.Where(s => !s.Contains(':')));
        var v6 = string.Join(',', servers.Where(s => s.Contains(':')));

        var status = SetFamily(adapter, v4, ipv6: false);
        if (status != 0)
        {
            return status;
        }

        status = SetFamily(adapter, v6, ipv6: true);
        if (status != 0)
        {
            return status;
        }

        FlushResolverCache();
        return 0;
    }

    private static uint SetFamily(Guid adapter, string nameServers, bool ipv6)
    {
        fixed (char* list = nameServers)
        {
            var settings = new DnsInterfaceSettings
            {
                Version = SettingsVersion1,
                Flags = SettingNameServer | (ipv6 ? SettingIPv6 : 0),
                NameServer = (nint)list,
            };

            try
            {
                return SetInterfaceDnsSettings(adapter, &settings);
            }
            catch (EntryPointNotFoundException)
            {
                // Windows 10 before 2004. ERROR_CALL_NOT_IMPLEMENTED tells the caller to
                // fall back to netsh.
                return 120;
            }
        }
    }

    /// <summary>
    /// Stale answers would otherwise keep resolving from the cache after the switch.
    /// <c>DnsFlushResolverCache</c> is exported but undocumented — what ipconfig /flushdns
    /// itself calls — so a missing export is tolerated rather than fatal.
    /// </summary>
    private static void FlushResolverCache()
    {
        try
        {
            _ = DnsFlushResolverCache();
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>DNS_INTERFACE_SETTINGS (version 1), 64 bytes on x64/ARM64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DnsInterfaceSettings
    {
        public uint Version;
        public ulong Flags;
        public nint Domain;
        public nint NameServer;
        public nint SearchList;
        public uint RegistrationEnabled;
        public uint RegisterAdapterName;
        public uint EnableLlmnr;
        public uint QueryAdapterName;
        public nint ProfileNameServer;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint SetInterfaceDnsSettings(Guid adapter, DnsInterfaceSettings* settings);

    [DllImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();
}
