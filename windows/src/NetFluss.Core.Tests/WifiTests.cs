// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Xml.Linq;
using NetFluss.Core;
using Xunit;

namespace NetFluss.Core.Tests;

public class WifiFormatTests
{
    [Theory]
    [InlineData(2412, 1, WifiBand.Band2GHz)]
    [InlineData(2437, 6, WifiBand.Band2GHz)]
    [InlineData(2484, 14, WifiBand.Band2GHz)]
    [InlineData(5180, 36, WifiBand.Band5GHz)]
    [InlineData(5745, 149, WifiBand.Band5GHz)]
    [InlineData(5955, 1, WifiBand.Band6GHz)]
    [InlineData(5935, 2, WifiBand.Band6GHz)]
    [InlineData(6415, 93, WifiBand.Band6GHz)]
    public void Frequency_MapsToChannelAndBand(int megahertz, int channel, WifiBand band)
    {
        Assert.Equal(channel, WifiFormat.ChannelFromFrequency(megahertz));
        Assert.Equal(band, WifiFormat.BandFromFrequency(megahertz));
    }

    [Theory]
    [InlineData(10u, WifiBand.Band5GHz, "Wi-Fi 6 (802.11ax)")]
    [InlineData(10u, WifiBand.Band6GHz, "Wi-Fi 6E (802.11ax)")]
    [InlineData(8u, WifiBand.Band5GHz, "Wi-Fi 5 (802.11ac)")]
    [InlineData(11u, WifiBand.Band6GHz, "Wi-Fi 7 (802.11be)")]
    public void PhyType_UsesTheMacLabels(uint phy, WifiBand band, string expected)
        => Assert.Equal(expected, WifiFormat.PhyLabel(phy, band));

    [Theory]
    [InlineData(7u, 4u, "WPA2 Personal")]
    [InlineData(9u, 4u, "WPA3 Personal")]
    [InlineData(6u, 4u, "WPA2 Enterprise")]
    [InlineData(1u, 0u, "Open")]
    [InlineData(1u, 0x101u, "WEP")]
    public void Security_UsesTheMacLabels(uint auth, uint cipher, string expected)
        => Assert.Equal(expected, WifiFormat.SecurityLabel(auth, cipher));

    [Fact]
    public void Quality_ConvertsToRssi_TheWayWindowsDerivesIt()
    {
        Assert.Equal(-100, WifiFormat.RssiFromQuality(0));
        Assert.Equal(-50, WifiFormat.RssiFromQuality(100));
        Assert.Equal(-60, WifiFormat.RssiFromQuality(80));
    }

    [Fact]
    public void Arrange_PutsCurrentThenPinnedThenStrongest_AndDeduplicatesMeshes()
    {
        WifiNetwork Net(string ssid, int rssi, bool current = false) => new() { Ssid = ssid, Rssi = rssi, IsCurrent = current };

        var scanned = new[]
        {
            Net("Cafe", -70),
            Net("Home", -50, current: true),
            Net("Office", -40),
            Net("Office", -65), // a second access point of the same mesh
            Net("Weak", -85),
        };

        var arranged = WifiFormat.Arrange(scanned, ["Weak", "Gone"]);

        Assert.Equal(["Home", "Weak", "Gone", "Office", "Cafe"], arranged.Select(n => n.Ssid));
        Assert.True(arranged.Single(n => n.Ssid == "Weak").IsPinned);
        Assert.False(arranged.Single(n => n.Ssid == "Gone").IsAvailable);
        Assert.Equal(-40, arranged.Single(n => n.Ssid == "Office").Rssi);
    }

    [Fact]
    public void Limit_NeverDropsPinnedOrCurrent()
    {
        var networks = new[]
        {
            new WifiNetwork { Ssid = "Home", IsCurrent = true },
            new WifiNetwork { Ssid = "Pinned", IsPinned = true },
            new WifiNetwork { Ssid = "A" },
            new WifiNetwork { Ssid = "B" },
            new WifiNetwork { Ssid = "C" },
        };

        var limited = WifiFormat.ApplyLimit(networks, enabled: true, count: 2);

        Assert.Equal(["Home", "Pinned", "A", "B"], limited.Select(n => n.Ssid));
    }

    [Fact]
    public void Bssid_IsFormattedLikeMacOS()
        => Assert.Equal("0a:1b:2c:3d:4e:5f", WifiFormat.FormatBssid([0x0A, 0x1B, 0x2C, 0x3D, 0x4E, 0x5F]));
}

public class WifiProfileTests
{
    private static readonly XNamespace Ns = "http://www.microsoft.com/networking/WLAN/profile/v1";

    [Fact]
    public void Wpa2Profile_CarriesTheSsidAndPassphrase()
    {
        var result = WifiProfile.Build("Home", 7, 4, "correct horse battery");
        Assert.True(result.IsValid);

        var xml = XDocument.Parse(result.Xml!);
        Assert.Equal("Home", xml.Descendants(Ns + "name").First().Value);
        Assert.Equal("486F6D65", xml.Descendants(Ns + "hex").Single().Value);
        Assert.Equal("WPA2PSK", xml.Descendants(Ns + "authentication").Single().Value);
        Assert.Equal("AES", xml.Descendants(Ns + "encryption").Single().Value);
        Assert.Equal("passPhrase", xml.Descendants(Ns + "keyType").Single().Value);
        Assert.Equal("correct horse battery", xml.Descendants(Ns + "keyMaterial").Single().Value);
    }

    /// <summary>
    /// The passphrase is user-typed and lands inside XML. Markup in it must stay data — a
    /// password that closes the element and opens another must still be that password.
    /// </summary>
    [Fact]
    public void Passphrase_WithMarkup_IsEscaped()
    {
        const string hostile = "</keyMaterial><x>&\"'";
        var result = WifiProfile.Build("Home & <Garden>", 7, 4, hostile);

        var xml = XDocument.Parse(result.Xml!);
        Assert.Equal(hostile, xml.Descendants(Ns + "keyMaterial").Single().Value);
        Assert.Equal("Home & <Garden>", xml.Descendants(Ns + "name").First().Value);
    }

    [Fact]
    public void OpenNetwork_NeedsNoKey()
    {
        var result = WifiProfile.Build("Cafe", 1, 0, null);
        Assert.True(result.IsValid);
        Assert.Empty(XDocument.Parse(result.Xml!).Descendants(Ns + "sharedKey"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public void Wpa_RejectsAPassphraseOutsideEightToSixtyThree(string password)
        => Assert.False(WifiProfile.Build("Home", 7, 4, password).IsValid);

    [Fact]
    public void Enterprise_IsDeclinedWithAReason()
    {
        var result = WifiProfile.Build("Corp", 6, 4, "whatever1");
        Assert.False(result.IsValid);
        Assert.Contains("Windows Settings", result.Error);
    }

    [Fact]
    public void Wpa3_UsesSae()
        => Assert.Contains("WPA3SAE", WifiProfile.Build("Home", 9, 4, "password123").Xml);
}

public class PublicIpParsingTests
{
    [Fact]
    public void Ipify_ReturnsTheAddress()
        => Assert.Equal("203.0.113.7", PublicIpLookup.ParseIpify("""{"ip":"203.0.113.7"}"""));

    [Fact]
    public void Ipify_RejectsSomethingThatIsNotAnAddress()
        => Assert.Null(PublicIpLookup.ParseIpify("""{"ip":"<script>"}"""));

    [Fact]
    public void IpWho_ReturnsAnUpperCaseCountryCode()
        => Assert.Equal("DE", PublicIpLookup.ParseIpWhoCountry("""{"success":true,"country_code":"de"}"""));

    [Fact]
    public void IpWho_Failure_IsNoCountry()
        => Assert.Null(PublicIpLookup.ParseIpWhoCountry("""{"success":false,"message":"rate limited"}"""));
}

public class TopAppsAggregatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Processes_AreGroupedByName_AndRanked()
    {
        var aggregator = new TopAppsAggregator();
        var apps = aggregator.Update(
            [
                new ProcessBytes(10, "Chrome", 1000, 100),
                new ProcessBytes(11, "Chrome", 3000, 100),
                new ProcessBytes(20, "Teams", 500, 500),
            ],
            TimeSpan.FromSeconds(2),
            T0,
            new HashSet<string>(),
            grace: null);

        Assert.Equal("Chrome", apps[0].Name);
        Assert.Equal(2000, apps[0].RxRateBps);
        Assert.Equal(100, apps[0].TxRateBps);
        Assert.Equal("Teams", apps[1].Name);
    }

    [Fact]
    public void HiddenApps_AreLeftOut_CaseInsensitively()
    {
        var aggregator = new TopAppsAggregator();
        var apps = aggregator.Update(
            [new ProcessBytes(1, "svchost", 9000, 0), new ProcessBytes(2, "Teams", 1, 0)],
            TimeSpan.FromSeconds(1),
            T0,
            new HashSet<string>(["SVCHOST"], StringComparer.OrdinalIgnoreCase),
            grace: null);

        Assert.Equal(["Teams"], apps.Select(a => a.Name));
    }

    [Fact]
    public void GracePeriod_KeepsAQuietAppListedUntilItExpires()
    {
        var aggregator = new TopAppsAggregator();
        var hidden = new HashSet<string>();
        var grace = TimeSpan.FromSeconds(3);

        aggregator.Update([new ProcessBytes(1, "Teams", 100, 0)], TimeSpan.FromSeconds(1), T0, hidden, grace);

        var during = aggregator.Update([], TimeSpan.FromSeconds(1), T0.AddSeconds(2), hidden, grace);
        Assert.Equal(["Teams"], during.Select(a => a.Name));
        Assert.Equal(0, during[0].Total);

        var after = aggregator.Update([], TimeSpan.FromSeconds(1), T0.AddSeconds(5), hidden, grace);
        Assert.Empty(after);
    }

    [Fact]
    public void OnlyFiveRows()
    {
        var aggregator = new TopAppsAggregator();
        var processes = Enumerable.Range(1, 9).Select(i => new ProcessBytes(i, $"App{i}", i * 100, 0));

        Assert.Equal(TopAppsAggregator.RowCount, aggregator.Update(processes, TimeSpan.FromSeconds(1), T0, new HashSet<string>(), null).Count);
    }
}

public class HelperProtocolTests
{
    [Fact]
    public void Request_RoundTrips_WithCamelCaseNames()
    {
        var line = HelperProtocol.Serialize(new HelperRequest { Op = "setDns", Id = 7, Adapter = "{x}", Servers = ["1.1.1.1"] });

        Assert.Contains("\"op\":\"setDns\"", line);
        Assert.DoesNotContain('\n', line);

        var back = HelperProtocol.Deserialize<HelperRequest>(line)!;
        Assert.Equal("setDns", back.Op);
        Assert.Equal(7, back.Id);
        Assert.Equal(["1.1.1.1"], back.Servers);
    }

    [Fact]
    public void Garbage_DeserializesToNull_RatherThanThrowing()
        => Assert.Null(HelperProtocol.Deserialize<HelperRequest>("{not json"));
}
