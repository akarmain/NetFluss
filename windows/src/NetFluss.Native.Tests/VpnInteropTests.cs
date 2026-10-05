// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Sockets;
using Xunit;

namespace NetFluss.Native.Tests;

public class VpnInteropTests
{
    [Fact]
    public void ListenerOwner_IsThisProcess()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.Equal(Environment.ProcessId, TcpListeners.OwnerOf(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void NoListener_IsNull()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.Null(TcpListeners.OwnerOf(port));
    }

    [Theory]
    [InlineData("Work", "'Work'")]
    [InlineData("Joe's VPN", "'Joe''s VPN'")]
    [InlineData("Joe’s VPN'; Remove-Item x", "'Joe’’s VPN''; Remove-Item x'")]
    [InlineData("‘‚‛", "'‘‘‚‚‛‛'")]
    public void PowerShellQuoting_EscapesEveryQuoteCharacter(string value, string expected)
        => Assert.Equal(expected, RasVpn.Quote(value));
}
