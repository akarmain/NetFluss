// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NetFluss.Native;

/// <summary>
/// Which process is listening on a loopback port. Used before NetFluss trusts OpenVPN's
/// management port with a password and VPN credentials: the helper picks a free port and
/// OpenVPN binds it a moment later, and in between another user's program could bind it
/// first and wait for exactly that conversation.
/// </summary>
public static class TcpListeners
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>The process listening on 127.0.0.1 (or any address) at <paramref name="port"/>, or null.</summary>
    public static int? OwnerOf(int port)
    {
        var size = 0;
        var result = GetExtendedTcpTable(nint.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (result != ErrorInsufficientBuffer)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0)
            {
                return null;
            }

            // MIB_TCPTABLE_OWNER_PID: DWORD count, then rows of six DWORDs —
            // state, local address, local port, remote address, remote port, owning PID.
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + (i * 24);
                var address = (uint)Marshal.ReadInt32(row + 4);
                var rowPort = BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt32(row + 8));
                var pid = Marshal.ReadInt32(row + 20);

                // 127.0.0.1 in network order reads as 0x0100007F; 0.0.0.0 also covers loopback.
                if (rowPort == port && (address == 0x0100007F || address == 0))
                {
                    return pid;
                }
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(nint table, ref int size, bool order, int family, int tableClass, int reserved);
}
