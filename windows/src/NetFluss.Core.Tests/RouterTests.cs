// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using NetFluss.Core;
using Xunit;

namespace NetFluss.Core.Tests;

[Collection(LocalizationCollection.Name)]
public sealed class RouterTests
{
    [Fact]
    public void FritzBoxAddonInfos()
    {
        const string xml = """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
            <u:GetAddonInfosResponse xmlns:u="urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1">
            <NewByteSendRate>12345</NewByteSendRate>
            <NewByteReceiveRate>987654</NewByteReceiveRate>
            <NewX_AVM_DE_TotalBytesSent64>100</NewX_AVM_DE_TotalBytesSent64>
            </u:GetAddonInfosResponse></s:Body></s:Envelope>
            """;
        var bandwidth = FritzBoxClient.ParseAddonInfos(xml);
        Assert.Equal(987654, bandwidth.RxRate);
        Assert.Equal(12345, bandwidth.TxRate);
    }

    [Fact]
    public void FritzBoxLinkPropertiesAndBadXml()
    {
        var link = FritzBoxClient.ParseLinkProperties("<r><NewLayer1DownstreamMaxBitRate>250000000</NewLayer1DownstreamMaxBitRate><NewLayer1UpstreamMaxBitRate>40000000</NewLayer1UpstreamMaxBitRate></r>");
        Assert.Equal((250_000_000UL, 40_000_000UL), link);
        Assert.Equal(RouterFailure.Parse, Assert.Throws<RouterException>(() => FritzBoxClient.ParseAddonInfos("<r/>")).Failure);
        Assert.Equal(RouterFailure.Parse, Assert.Throws<RouterException>(() => FritzBoxClient.ParseAddonInfos("not xml")).Failure);
    }

    [Theory]
    [InlineData("192.168.178.1", true)]
    [InlineData("fritz.box", true)]
    [InlineData("fd00::1", true)]
    [InlineData("evil/path", false)]
    [InlineData("a b", false)]
    public void FritzBoxHostIsValidated(string host, bool safe) => Assert.Equal(safe, FritzBoxClient.IsSafeHost(host));

    [Fact]
    public void UniFiFindsTheGatewayByTypeOrWanUplink()
    {
        var byType = UniFiClient.ParseDevices("""
            {"data":[{"type":"uap"},{"type":"udm","wan1":{"rx_bytes-r":1500.5,"tx_bytes-r":300,"max_speed":1000}}]}
            """);
        Assert.Equal(1500.5, byType.RxRate);
        Assert.Equal(1_000_000_000UL, byType.MaxDownBits);

        var byUplink = UniFiClient.ParseDevices("""{"data":[{"type":"usw"},{"type":"future","wan1":{"rx_bytes-r":10,"tx_bytes-r":"20"}}]}""");
        Assert.Equal(20, byUplink.TxRate);

        Assert.Equal(RouterFailure.NoGateway, Assert.Throws<RouterException>(() => UniFiClient.ParseDevices("""{"data":[{"type":"uap"}]}""")).Failure);
        Assert.Equal(RouterFailure.Parse, Assert.Throws<RouterException>(() => UniFiClient.ParseDevices("""{"x":1}""")).Failure);
    }

    [Theory]
    [InlineData("""{"required":"2fa"}""", true)]
    [InlineData("""{"required":"MFA token"}""", true)]
    [InlineData("""{"error":"bad"}""", false)]
    [InlineData("<html>", false)]
    public void UniFiTwoFactorDetection(string body, bool expected) => Assert.Equal(expected, UniFiClient.RequiresTwoFactor(body));

    [Fact]
    public void OpenWrtRpcPayloadsAndErrors()
    {
        var payload = OpenWrtClient.ParseRpc("""{"jsonrpc":"2.0","id":1,"result":[0,{"ubus_rpc_session":"abc"}]}""", out var denied);
        Assert.False(denied);
        Assert.Equal("abc", payload!["ubus_rpc_session"]!.GetValue<string>());

        Assert.Null(OpenWrtClient.ParseRpc("""{"result":[6]}""", out denied));
        Assert.True(denied);

        var notFound = Assert.Throws<RouterException>(() => OpenWrtClient.ParseRpc("""{"result":[4]}""", out _));
        Assert.Equal((RouterFailure.RpcFailure, 4), (notFound.Failure, notFound.Status));

        Assert.Equal(RouterFailure.RpcFailure, Assert.Throws<RouterException>(() => OpenWrtClient.ParseRpc("""{"error":{"code":-32002,"message":"Access denied"}}""", out _)).Failure);
        Assert.Equal(RouterFailure.UbusUnavailable, Assert.Throws<RouterException>(() => OpenWrtClient.ParseRpc("<html>", out _)).Failure);
    }

    [Fact]
    public void OpenWrtPicksTheDefaultRouteWithTheLowestMetric()
    {
        var interfaces = JsonNode.Parse("""
            [
              {"interface":"lan","up":true,"l3_device":"br-lan","route":[]},
              {"interface":"wan6","up":true,"l3_device":"pppoe-wan6","route":[{"target":"::","mask":0,"metric":512}]},
              {"interface":"wwan","up":true,"device":"wlan1","route":[{"target":"0.0.0.0","mask":0,"metric":10}]}
            ]
            """)!.AsArray();
        Assert.Equal("wlan1", OpenWrtClient.PickWanDevice(interfaces));

        var named = JsonNode.Parse("""[{"interface":"lan","up":true,"device":"br-lan"},{"interface":"wan","up":true,"device":"eth1"}]""")!.AsArray();
        Assert.Equal("eth1", OpenWrtClient.PickWanDevice(named));
    }

    [Fact]
    public void OpenWrtDeviceCounters()
    {
        var sample = OpenWrtClient.ParseDeviceStatus(JsonNode.Parse("""{"speed":"1000","statistics":{"rx_bytes":5000,"tx_bytes":"700"}}""")!.AsObject(), DateTimeOffset.UnixEpoch);
        Assert.Equal((5000UL, 700UL, 1000UL), (sample.RxBytes, sample.TxBytes, sample.LinkSpeedMbps));
    }

    [Fact]
    public void OpnSenseWanCounters()
    {
        var sample = OpnSenseClient.ParseTraffic("""
            {"interfaces":{"lan":{"bytes received":"1"},"wan":{"bytes received":"123456","bytes transmitted":"654","line rate":"1000000000 bit/s"}}}
            """, DateTimeOffset.UnixEpoch);
        Assert.Equal((123456UL, 654UL, 1000UL), (sample.RxBytes, sample.TxBytes, sample.LinkSpeedMbps));
        Assert.Equal(RouterFailure.NoWan, Assert.Throws<RouterException>(() => OpnSenseClient.ParseTraffic("""{"interfaces":{}}""", DateTimeOffset.UnixEpoch)).Failure);
    }

    [Fact]
    public void CounterRatesHandleResets()
    {
        var start = new RouterCounterSample(1000, 500, 100, DateTimeOffset.UnixEpoch);
        var later = new RouterCounterSample(6000, 1500, 0, DateTimeOffset.UnixEpoch.AddSeconds(5));
        var rates = RouterCounterSample.Rates(start, later)!;
        Assert.Equal((1000.0, 200.0, 100_000_000UL), (rates.RxRate, rates.TxRate, rates.MaxDownBits));

        var reset = new RouterCounterSample(10, 10, 100, DateTimeOffset.UnixEpoch.AddSeconds(10));
        Assert.Equal(0, RouterCounterSample.Rates(later, reset)!.RxRate);
        Assert.Null(RouterCounterSample.Rates(later, later));
    }

    [Theory]
    [InlineData("https://Router.lan:8443/x", "router.lan")]
    [InlineData("router.lan:8443", "router.lan")]
    [InlineData("192.168.1.1", "192.168.1.1")]
    [InlineData("[fd00::1]:443", "fd00::1")]
    public void NormalizesHosts(string address, string expected) => Assert.Equal(expected, RouterPinStore.NormalizedHost(address));

    [Fact]
    public void PinsTrustOnFirstUseAndResetPerHost()
    {
        using var rsa1 = System.Security.Cryptography.RSA.Create(2048);
        using var rsa2 = System.Security.Cryptography.RSA.Create(2048);
        using var first = SelfSigned(rsa1);
        using var second = SelfSigned(rsa2);
        var pins = new RouterPinStore(null);

        Assert.True(pins.Validate("router.lan", 443, first));
        Assert.True(pins.Validate("router.lan", 443, first));
        Assert.False(pins.Validate("router.lan", 443, second));
        Assert.True(pins.CertificateChanged("https://router.lan"));
        Assert.True(pins.Validate("other.lan", 443, second));

        pins.ResetTrust("router.lan:443");
        Assert.False(pins.CertificateChanged("router.lan"));
        Assert.True(pins.Validate("router.lan", 443, second));
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSigned(System.Security.Cryptography.RSA key)
    {
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=router", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
    }

    [Fact]
    public void DiagnosisSpeaksToTheCause()
    {
        var pins = new RouterPinStore(null);
        var refused = new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused));
        var message = RouterDiagnosis.Describe(RouterKind.OpnSense, new RouterException(RouterFailure.Transport, inner: refused), "192.168.1.1", usesAutoHost: false, pins);
        Assert.Contains("refused the HTTPS connection on port 443", message, StringComparison.Ordinal);
        Assert.Contains("http://192.168.1.1", message, StringComparison.Ordinal);

        var timeout = RouterDiagnosis.Describe(RouterKind.FritzBox, new TaskCanceledException(), "192.168.178.1", usesAutoHost: true, pins);
        Assert.Equal("Fritz!Box did not respond in time.", timeout);

        var twoFactor = RouterDiagnosis.Describe(RouterKind.UniFi, new RouterException(RouterFailure.TwoFactorRequired), "10.0.0.1", usesAutoHost: false, pins);
        Assert.Contains("two-factor", twoFactor, StringComparison.Ordinal);

        var noKey = RouterDiagnosis.Describe(RouterKind.UniFi, new RouterException(RouterFailure.MissingCredentials, "apikey"), "10.0.0.1", usesAutoHost: false, pins);
        Assert.Equal("No API key configured", noKey);
    }
}
