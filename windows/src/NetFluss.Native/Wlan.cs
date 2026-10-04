// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;
using System.Text;
using NetFluss.Core;

namespace NetFluss.Native;

/// <summary>Why a Wi-Fi query came back empty.</summary>
public enum WlanAccess
{
    /// <summary>The query worked.</summary>
    Ok,

    /// <summary>The machine has no Wi-Fi adapter, or the WLAN service is not running.</summary>
    NoAdapter,

    /// <summary>
    /// Windows refused because the app has no location access. Windows 11 24H2 put SSIDs,
    /// BSSIDs and scan results behind the location permission — the same restriction macOS
    /// applies to CoreWLAN — so this is an expected state, not a fault.
    /// </summary>
    LocationDenied,
}

/// <summary>One Wi-Fi radio.</summary>
public sealed record WlanInterface(Guid Id, string Description, bool IsConnected);

/// <summary>Result of a scan-list read.</summary>
public sealed record WlanScanResult(WlanAccess Access, IReadOnlyList<WifiNetwork> Networks);

/// <summary>
/// The Native Wifi API (wlanapi.dll) — Windows' counterpart of CoreWLAN.
///
/// <para>Everything here works unelevated: reading the connection, scanning, and joining
/// with a profile are all per-user operations. That is what lets the Wi-Fi switcher work
/// without the helper service the Mac needs for its Known Networks writes.</para>
///
/// <para>The structures are read at their documented byte offsets rather than marshalled
/// through <c>[StructLayout]</c> declarations. They are large, they contain fixed arrays
/// and padding that differ in subtle ways from what a sequential layout would infer, and
/// an offset that is wrong by two bytes yields plausible garbage rather than an error. The
/// offsets below are each annotated with the field they address.</para>
///
/// <para>Not thread-safe. One instance per caller, disposed when done.</para>
/// </summary>
public sealed unsafe class WlanClient : IDisposable
{
    private const uint ClientVersion = 2;
    private const uint ErrorSuccess = 0;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorServiceNotActive = 1062;
    private const uint ErrorNotFound = 1168;

    private nint _handle;

    private WlanClient(nint handle) => _handle = handle;

    /// <summary>Opens a client, or returns null when the WLAN service is absent.</summary>
    public static WlanClient? TryOpen()
    {
        try
        {
            var status = WlanOpenHandle(ClientVersion, nint.Zero, out _, out var handle);
            return status == ErrorSuccess ? new WlanClient(handle) : null;
        }
        catch (DllNotFoundException)
        {
            // Server SKUs ship without the WLAN AutoConfig feature, and so without the DLL.
            return null;
        }
    }

    public IReadOnlyList<WlanInterface> Interfaces()
    {
        if (WlanEnumInterfaces(_handle, nint.Zero, out var list) != ErrorSuccess || list == nint.Zero)
        {
            return [];
        }

        try
        {
            // WLAN_INTERFACE_INFO_LIST: DWORD dwNumberOfItems; DWORD dwIndex; then items.
            var count = *(uint*)list;
            var result = new List<WlanInterface>((int)Math.Min(count, 16));

            for (var i = 0; i < count && i < 64; i++)
            {
                // WLAN_INTERFACE_INFO is 532 bytes: GUID (16), WCHAR[256] (512), state (4).
                var item = (byte*)list + 8 + (i * 532);
                var id = *(Guid*)item;
                var description = ReadWideString(item + 16, 256);
                var state = *(uint*)(item + 528);

                // wlan_interface_state_connected == 1.
                result.Add(new WlanInterface(id, description, state == 1));
            }

            return result;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>
    /// Details of the network an interface is joined to, or null when it is not connected.
    /// <paramref name="access"/> says why when the answer is null.
    /// </summary>
    public WifiDetail? CurrentConnection(Guid interfaceId, out WlanAccess access)
    {
        access = WlanAccess.Ok;

        var status = Query(interfaceId, OpcodeCurrentConnection, out var data, out var size);
        if (status == ErrorAccessDenied)
        {
            access = WlanAccess.LocationDenied;
            return null;
        }

        if (status != ErrorSuccess || data == nint.Zero || size < 604)
        {
            return null;
        }

        string? profile;
        string? ssid;
        byte[] bssid;
        uint phyType;
        uint quality;
        uint rxKbps;
        uint txKbps;
        bool securityEnabled;
        uint auth;
        uint cipher;

        try
        {
            var p = (byte*)data;

            // WLAN_CONNECTION_ATTRIBUTES
            //   0  WLAN_INTERFACE_STATE isState
            //   4  WLAN_CONNECTION_MODE wlanConnectionMode
            //   8  WCHAR strProfileName[256]
            // 520  WLAN_ASSOCIATION_ATTRIBUTES (68 bytes)
            // 588  WLAN_SECURITY_ATTRIBUTES (16 bytes)
            var state = *(uint*)p;
            if (state != 1)
            {
                return null;
            }

            profile = ReadWideString(p + 8, 256);

            var association = p + 520;

            // WLAN_ASSOCIATION_ATTRIBUTES
            //   0  DOT11_SSID (ULONG length + UCHAR[32])
            //  36  DOT11_BSS_TYPE
            //  40  DOT11_MAC_ADDRESS (6 bytes, then 2 of padding)
            //  48  DOT11_PHY_TYPE
            //  52  ULONG uDot11PhyIndex
            //  56  WLAN_SIGNAL_QUALITY (0–100)
            //  60  ULONG ulRxRate (kbps)
            //  64  ULONG ulTxRate (kbps)
            ssid = ReadSsid(association);
            bssid = new ReadOnlySpan<byte>(association + 40, 6).ToArray();
            phyType = *(uint*)(association + 48);
            quality = *(uint*)(association + 56);
            rxKbps = *(uint*)(association + 60);
            txKbps = *(uint*)(association + 64);

            // WLAN_SECURITY_ATTRIBUTES: BOOL enabled, BOOL oneX, auth, cipher.
            var security = p + 588;
            securityEnabled = *(int*)security != 0;
            auth = *(uint*)(security + 8);
            cipher = *(uint*)(security + 12);
        }
        finally
        {
            WlanFreeMemory(data);
        }

        int? rssi = null;
        if (Query(interfaceId, OpcodeRssi, out var rssiData, out var rssiSize) == ErrorSuccess && rssiData != nint.Zero)
        {
            try
            {
                if (rssiSize >= 4)
                {
                    var value = *(int*)rssiData;
                    rssi = value is < 0 and > -120 ? value : null;
                }
            }
            finally
            {
                WlanFreeMemory(rssiData);
            }
        }

        int? channel = null;
        if (Query(interfaceId, OpcodeChannelNumber, out var channelData, out var channelSize) == ErrorSuccess && channelData != nint.Zero)
        {
            try
            {
                if (channelSize >= 4)
                {
                    var value = *(uint*)channelData;
                    channel = value is > 0 and < 256 ? (int)value : null;
                }
            }
            finally
            {
                WlanFreeMemory(channelData);
            }
        }

        // The channel number alone cannot tell 6 GHz from 2.4/5 GHz — 6 GHz reuses the
        // numbers from 1 — so the band comes from the access point's centre frequency.
        var bss = FindBss(interfaceId, ssid, bssid);
        var band = bss?.Band;
        channel ??= bss?.Channel;
        rssi ??= bss?.Rssi;

        return new WifiDetail
        {
            Ssid = ssid,
            Bssid = WifiFormat.FormatBssid(bssid),
            ProfileName = string.IsNullOrEmpty(profile) ? null : profile,
            PhyMode = WifiFormat.PhyLabel(phyType, band),
            Security = WifiFormat.SecurityLabel(auth, cipher, securityEnabled),
            Channel = channel,
            Band = band ?? GuessBand(channel, phyType),
            Rssi = rssi ?? WifiFormat.RssiFromQuality((int)quality),
            SignalQuality = (int)quality,
            RxRateMbps = rxKbps > 0 ? rxKbps / 1000.0 : null,
            TxRateMbps = txKbps > 0 ? txKbps / 1000.0 : null,
        };
    }

    /// <summary>Asks the driver for a fresh scan. Results arrive a few seconds later.</summary>
    public WlanAccess RequestScan(Guid interfaceId)
    {
        var id = interfaceId;
        var status = WlanScan(_handle, &id, nint.Zero, nint.Zero, nint.Zero);
        return status switch
        {
            ErrorSuccess => WlanAccess.Ok,
            ErrorAccessDenied => WlanAccess.LocationDenied,
            _ => WlanAccess.NoAdapter,
        };
    }

    /// <summary>
    /// The networks currently in range, one entry per SSID with the strongest access point's
    /// radio details folded in from the BSS list.
    /// </summary>
    public WlanScanResult AvailableNetworks(Guid interfaceId)
    {
        var id = interfaceId;

        // Flag 0x2 includes hidden networks that have a profile; 0x1 would add ad-hoc
        // profiles, which are not something a tray menu should offer.
        var status = WlanGetAvailableNetworkList(_handle, &id, 0x2, nint.Zero, out var list);
        if (status == ErrorAccessDenied)
        {
            return new WlanScanResult(WlanAccess.LocationDenied, []);
        }

        if (status != ErrorSuccess || list == nint.Zero)
        {
            return new WlanScanResult(status is ErrorServiceNotActive or ErrorNotFound ? WlanAccess.NoAdapter : WlanAccess.Ok, []);
        }

        var networks = new List<WifiNetwork>();

        try
        {
            var count = *(uint*)list;
            for (var i = 0; i < count && i < 512; i++)
            {
                // WLAN_AVAILABLE_NETWORK is 628 bytes:
                //   0  WCHAR strProfileName[256]
                // 512  DOT11_SSID (36)
                // 548  DOT11_BSS_TYPE
                // 552  ULONG uNumberOfBssids
                // 556  BOOL bNetworkConnectable
                // 560  WLAN_REASON_CODE
                // 564  ULONG uNumberOfPhyTypes
                // 568  DOT11_PHY_TYPE[8]
                // 600  BOOL bMorePhyTypes
                // 604  WLAN_SIGNAL_QUALITY
                // 608  BOOL bSecurityEnabled
                // 612  DOT11_AUTH_ALGORITHM
                // 616  DOT11_CIPHER_ALGORITHM
                // 620  DWORD dwFlags
                var item = (byte*)list + 8 + (i * 628);

                var bssType = *(uint*)(item + 548);
                if (bssType != 1)
                {
                    // Infrastructure networks only; ad-hoc is not something to join from here.
                    continue;
                }

                var ssid = ReadSsid(item + 512);
                if (string.IsNullOrEmpty(ssid))
                {
                    continue;
                }

                var profile = ReadWideString(item, 256);
                var quality = *(uint*)(item + 604);
                var auth = *(uint*)(item + 612);
                var cipher = *(uint*)(item + 616);
                var flags = *(uint*)(item + 620);

                networks.Add(new WifiNetwork
                {
                    Ssid = ssid,
                    SignalQuality = (int)quality,
                    Rssi = WifiFormat.RssiFromQuality((int)quality),
                    IsSecured = WifiFormat.IsSecured(auth, cipher),
                    Security = WifiFormat.SecurityLabel(auth, cipher),
                    IsCurrent = (flags & 0x1) != 0,
                    IsSaved = (flags & 0x2) != 0,
                    ProfileName = string.IsNullOrEmpty(profile) ? null : profile,
                    AuthAlgorithm = auth,
                    CipherAlgorithm = cipher,
                });
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }

        // Fold in per-access-point detail. A failure here just leaves those columns empty.
        var bssEntries = BssList(interfaceId);
        if (bssEntries.Count > 0)
        {
            var strongest = bssEntries
                .GroupBy(entry => entry.Ssid, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(e => e.Rssi ?? -200).First(), StringComparer.Ordinal);

            for (var i = 0; i < networks.Count; i++)
            {
                if (strongest.TryGetValue(networks[i].Ssid, out var bss))
                {
                    networks[i] = networks[i] with
                    {
                        Bssid = bss.Bssid,
                        Rssi = bss.Rssi ?? networks[i].Rssi,
                        Channel = bss.Channel,
                        Band = bss.Band,
                    };
                }
            }
        }

        return new WlanScanResult(WlanAccess.Ok, networks);
    }

    /// <summary>Profiles Windows holds for this radio, in its own priority order.</summary>
    public IReadOnlyList<string> ProfileNames(Guid interfaceId)
    {
        var id = interfaceId;
        if (WlanGetProfileList(_handle, &id, nint.Zero, out var list) != ErrorSuccess || list == nint.Zero)
        {
            return [];
        }

        try
        {
            // WLAN_PROFILE_INFO_LIST: count, index, then WLAN_PROFILE_INFO { WCHAR[256]; DWORD flags } = 516 bytes.
            var count = *(uint*)list;
            var names = new List<string>();
            for (var i = 0; i < count && i < 1024; i++)
            {
                names.Add(ReadWideString((byte*)list + 8 + (i * 516), 256));
            }

            return names;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>
    /// Writes a profile. Tries an all-user profile first — what the Windows Wi-Fi flyout
    /// itself creates, so the network is then known everywhere — and falls back to a
    /// per-user one where policy reserves all-user profiles for administrators.
    /// </summary>
    public uint SetProfile(Guid interfaceId, string profileXml)
    {
        var id = interfaceId;
        uint status;
        uint reason;

        fixed (char* xml = profileXml)
        {
            status = WlanSetProfile(_handle, &id, 0, xml, null, true, nint.Zero, out reason);
            if (status == ErrorAccessDenied)
            {
                const uint WlanProfileUser = 0x2;
                status = WlanSetProfile(_handle, &id, WlanProfileUser, xml, null, true, nint.Zero, out reason);
            }
        }

        _ = reason;
        return status;
    }

    /// <summary>Starts a connection with an existing profile. Completion is asynchronous.</summary>
    public uint Connect(Guid interfaceId, string profileName)
    {
        var id = interfaceId;

        fixed (char* profile = profileName)
        {
            var parameters = new ConnectionParameters
            {
                ConnectionMode = 0, // wlan_connection_mode_profile
                Profile = (nint)profile,
                Ssid = nint.Zero,
                DesiredBssidList = nint.Zero,
                BssType = 1, // dot11_BSS_type_infrastructure
                Flags = 0,
            };

            return WlanConnect(_handle, &id, &parameters, nint.Zero);
        }
    }

    public uint Disconnect(Guid interfaceId)
    {
        var id = interfaceId;
        return WlanDisconnect(_handle, &id, nint.Zero);
    }

    public void Dispose()
    {
        if (_handle != nint.Zero)
        {
            _ = WlanCloseHandle(_handle, nint.Zero);
            _handle = nint.Zero;
        }
    }

    private readonly record struct BssInfo(string Ssid, string Bssid, int? Rssi, int? Channel, WifiBand? Band);

    private BssInfo? FindBss(Guid interfaceId, string? ssid, byte[] bssid)
    {
        var wanted = WifiFormat.FormatBssid(bssid);
        BssInfo? strongestWithSsid = null;

        foreach (var entry in BssList(interfaceId))
        {
            if (entry.Bssid == wanted)
            {
                return entry;
            }

            if (entry.Ssid == ssid && (strongestWithSsid is null || (entry.Rssi ?? -200) > (strongestWithSsid.Value.Rssi ?? -200)))
            {
                strongestWithSsid = entry;
            }
        }

        return strongestWithSsid;
    }

    private List<BssInfo> BssList(Guid interfaceId)
    {
        var id = interfaceId;
        if (WlanGetNetworkBssList(_handle, &id, nint.Zero, 1, false, nint.Zero, out var list) != ErrorSuccess || list == nint.Zero)
        {
            return [];
        }

        var result = new List<BssInfo>();
        try
        {
            // WLAN_BSS_LIST: DWORD dwTotalSize; DWORD dwNumberOfItems; then 360-byte entries.
            var total = *(uint*)list;
            var count = *(uint*)((byte*)list + 4);

            for (var i = 0; i < count && i < 1024; i++)
            {
                var offset = 8 + (i * 360);
                if (offset + 360 > total)
                {
                    break;
                }

                // WLAN_BSS_ENTRY
                //   0  DOT11_SSID (36)
                //  36  ULONG uPhyId
                //  40  DOT11_MAC_ADDRESS (6, then 2 of padding)
                //  48  DOT11_BSS_TYPE
                //  52  DOT11_PHY_TYPE
                //  56  LONG lRssi
                //  60  ULONG uLinkQuality
                //  64  BOOLEAN bInRegDomain, USHORT usBeaconPeriod at 66
                //  72  ULONGLONG ullTimestamp
                //  80  ULONGLONG ullHostTimestamp
                //  88  USHORT usCapabilityInformation
                //  92  ULONG ulChCenterFrequency (kHz)
                //  96  WLAN_RATE_SET (256)
                // 352  ULONG ulIeOffset
                // 356  ULONG ulIeSize
                var entry = (byte*)list + offset;
                var ssid = ReadSsid(entry) ?? string.Empty;
                var mac = WifiFormat.FormatBssid(new ReadOnlySpan<byte>(entry + 40, 6));
                var rssi = *(int*)(entry + 56);
                var frequencyKhz = *(uint*)(entry + 92);
                var megahertz = (int)(frequencyKhz / 1000);

                result.Add(new BssInfo(
                    ssid,
                    mac,
                    rssi is < 0 and > -120 ? rssi : null,
                    WifiFormat.ChannelFromFrequency(megahertz),
                    WifiFormat.BandFromFrequency(megahertz)));
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }

        return result;
    }

    /// <summary>
    /// Best guess when no BSS entry is visible: channels above 14 are 5 GHz unless the radio
    /// is 802.11ax, where they might equally be 6 GHz — so that case stays unknown.
    /// </summary>
    private static WifiBand? GuessBand(int? channel, uint phyType) => channel switch
    {
        null => null,
        <= 14 when phyType is 5 or 6 or 7 => WifiBand.Band2GHz,
        > 14 when phyType is 4 or 8 => WifiBand.Band5GHz,
        _ => null,
    };

    private uint Query(Guid interfaceId, uint opcode, out nint data, out uint size)
    {
        var id = interfaceId;
        return WlanQueryInterface(_handle, &id, opcode, nint.Zero, out size, out data, out _);
    }

    private static string? ReadSsid(byte* dot11Ssid)
    {
        var length = *(uint*)dot11Ssid;
        if (length is 0 or > 32)
        {
            return null;
        }

        // SSIDs are raw octets; UTF-8 is what every consumer platform writes today.
        return Encoding.UTF8.GetString(dot11Ssid + 4, (int)length);
    }

    private static string ReadWideString(byte* start, int maxChars)
    {
        var chars = (char*)start;
        var length = 0;
        while (length < maxChars && chars[length] != '\0')
        {
            length++;
        }

        return new string(chars, 0, length);
    }

    private const uint OpcodeCurrentConnection = 7;
    private const uint OpcodeChannelNumber = 8;
    private const uint OpcodeRssi = 0x10000102;

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectionParameters
    {
        public uint ConnectionMode;
        public nint Profile;
        public nint Ssid;
        public nint DesiredBssidList;
        public uint BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(nint handle, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(nint handle, Guid* interfaceGuid, uint opcode, nint reserved, out uint dataSize, out nint data, out uint opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanScan(nint handle, Guid* interfaceGuid, nint ssid, nint ieData, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetAvailableNetworkList(nint handle, Guid* interfaceGuid, uint flags, nint reserved, out nint networkList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetNetworkBssList(nint handle, Guid* interfaceGuid, nint ssid, uint bssType, [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, nint reserved, out nint bssList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetProfileList(nint handle, Guid* interfaceGuid, nint reserved, out nint profileList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanSetProfile(nint handle, Guid* interfaceGuid, uint flags, char* profileXml, char* allUserProfileSecurity, [MarshalAs(UnmanagedType.Bool)] bool overwrite, nint reserved, out uint reasonCode);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanConnect(nint handle, Guid* interfaceGuid, ConnectionParameters* parameters, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanDisconnect(nint handle, Guid* interfaceGuid, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint memory);
}
