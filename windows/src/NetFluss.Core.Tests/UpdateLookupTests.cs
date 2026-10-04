// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using NetFluss.Core;
using Xunit;

namespace NetFluss.Core.Tests;

public class UpdateLookupTests
{
    /// <summary>Both apps release from one repository; the Mac's release is newest here.</summary>
    private const string Releases = """
        [
          { "tag_name": "v2.6", "draft": false, "prerelease": false, "body": "Mac", "html_url": "https://github.com/rana-gmbh/NetFluss/releases/tag/v2.6",
            "assets": [ { "name": "Netfluss-2.6.zip", "browser_download_url": "https://github.com/x/Netfluss-2.6.zip" } ] },
          { "tag_name": "win-v1.1.0", "draft": false, "prerelease": true, "body": "beta", "html_url": "https://github.com/rana-gmbh/NetFluss/releases/tag/win-v1.1.0", "assets": [] },
          { "tag_name": "win-v1.0.2", "draft": false, "prerelease": false, "body": "Fixes", "html_url": "https://github.com/rana-gmbh/NetFluss/releases/tag/win-v1.0.2",
            "assets": [
              { "name": "NetFluss-1.0.2-arm64.msi", "browser_download_url": "https://github.com/x/NetFluss-1.0.2-arm64.msi" },
              { "name": "NetFluss-1.0.2-x64.msi", "browser_download_url": "https://github.com/x/NetFluss-1.0.2-x64.msi" }
            ] },
          { "tag_name": "win-v1.0.1", "draft": false, "prerelease": false, "body": "Older", "html_url": "https://github.com/rana-gmbh/NetFluss/releases/tag/win-v1.0.1", "assets": [] }
        ]
        """;

    [Fact]
    public void OnlyWindowsReleases_AreOffered()
    {
        var update = UpdateLookup.Newest(Releases, "1.0.0");

        Assert.NotNull(update);
        Assert.Equal("1.0.2", update.Version);
        Assert.Equal("Fixes", update.ReleaseNotes);
        Assert.EndsWith(".msi", update.Download!.ToString());
    }

    [Fact]
    public void Prereleases_AreSkipped()
        => Assert.NotEqual("1.1.0", UpdateLookup.Newest(Releases, "1.0.0")?.Version);

    [Fact]
    public void UpToDate_IsNull()
        => Assert.Null(UpdateLookup.Newest(Releases, "1.0.2"));

    [Theory]
    [InlineData("1.0.10", "1.0.9", true)]
    [InlineData("1.1", "1.0.9", true)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData("2.0.0-beta", "1.9.9", true)]
    public void Versions_CompareNumerically(string latest, string current, bool newer)
        => Assert.Equal(newer, UpdateLookup.IsNewer(latest, current));

    [Fact]
    public void NonHttpsLinks_AreIgnored()
    {
        const string json = """[{ "tag_name": "win-v9.0.0", "html_url": "http://evil.example/x", "assets": [] }]""";
        Assert.Null(UpdateLookup.Newest(json, "1.0.0"));
    }
}
