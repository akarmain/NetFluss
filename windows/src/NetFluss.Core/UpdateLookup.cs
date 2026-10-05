// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Http.Headers;
using System.Text.Json;

namespace NetFluss.Core;

/// <summary>A newer release than the running one.</summary>
/// <param name="Installer">The setup for this architecture, which the in-app updater runs.</param>
/// <param name="Checksums">The release's SHA256SUMS.txt; without it nothing is installed.</param>
/// <param name="Signature">Its signature (<see cref="UpdateSignature"/>); without a valid one nothing is installed either.</param>
public sealed record AvailableUpdate(string Version, string ReleaseNotes, Uri ReleasePage, Uri? Download, Uri? Installer = null, Uri? Checksums = null, Uri? Signature = null);

/// <summary>
/// Asks GitHub for the newest Windows release. Port of the macOS <c>UpdateLookup</c>, with
/// one deliberate difference: both apps release from the same repository, and the Mac's
/// "latest release" endpoint would hand a Windows user a macOS zip whenever the Mac shipped
/// last. Windows releases are tagged <c>win-vX.Y.Z</c>, so the list is filtered by that
/// prefix and only a Windows release can ever be offered.
/// </summary>
public static class UpdateLookup
{
    public const string TagPrefix = "win-v";

    private const string ReleasesUrl = "https://api.github.com/repos/rana-gmbh/NetFluss/releases?per_page=30";

    public static async Task<AvailableUpdate?> FetchAsync(string currentVersion, bool includePrereleases = false, HttpMessageHandler? handler = null, CancellationToken cancellation = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(15);

        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NetFluss-Windows", currentVersion));

        using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub returned HTTP {(int)response.StatusCode}.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        return Newest(json, currentVersion, includePrereleases);
    }

    /// <summary>
    /// The newest non-draft Windows release newer than <paramref name="currentVersion"/>.
    /// Pre-releases when the running build is one itself — a beta tester gets the next beta,
    /// and the release after it, which moves them back to the stable channel — or when
    /// <paramref name="includePrereleases"/> asks for them (the test channel).
    /// </summary>
    public static AvailableUpdate? Newest(string releasesJson, string currentVersion, bool includePrereleases = false)
    {
        using var document = JsonDocument.Parse(releasesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        includePrereleases |= BuildVersion.IsPrerelease(currentVersion);

        AvailableUpdate? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (Bool(release, "draft") || (Bool(release, "prerelease") && !includePrereleases))
            {
                continue;
            }

            var tag = String(release, "tag_name");
            if (tag is null || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = tag[TagPrefix.Length..];
            if (!IsNewer(version, currentVersion) || (best is not null && !IsNewer(version, best.Version)))
            {
                continue;
            }

            if (!Uri.TryCreate(String(release, "html_url"), UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            best = new AvailableUpdate(
                version,
                String(release, "body") ?? string.Empty,
                page,
                DownloadAsset(release),
                Asset(release, $"NetFluss-Setup-{version}-{Architecture}.exe"),
                Asset(release, "SHA256SUMS.txt"),
                Asset(release, UpdateSignature.AssetName));
        }

        return best;
    }

    /// <summary>
    /// Semantic-version order: numeric, component-wise, missing components counting as zero
    /// as on macOS; then a pre-release sorts below its release, so 2.6.0-beta.1 &lt;
    /// 2.6.0-beta.2 &lt; 2.6.0. Build metadata ("+commit") is ignored.
    /// </summary>
    public static bool IsNewer(string latest, string current) => Compare(latest, current) > 0;

    private static int Compare(string a, string b)
    {
        static (int[] Core, string[]? Pre) Parse(string version)
        {
            var metadata = version.IndexOf('+', StringComparison.Ordinal);
            if (metadata >= 0)
            {
                version = version[..metadata];
            }

            var dash = version.IndexOf('-', StringComparison.Ordinal);
            var core = dash >= 0 ? version[..dash] : version;
            int[] numbers = [.. core.Split('.').TakeWhile(part => int.TryParse(part, out _)).Select(int.Parse)];
            return (numbers, dash >= 0 ? version[(dash + 1)..].Split('.') : null);
        }

        var (coreA, preA) = Parse(a);
        var (coreB, preB) = Parse(b);
        for (var i = 0; i < Math.Max(coreA.Length, coreB.Length); i++)
        {
            var x = i < coreA.Length ? coreA[i] : 0;
            var y = i < coreB.Length ? coreB[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        // Same release: a pre-release is older than the release itself.
        if (preA is null || preB is null)
        {
            return (preA is null ? 1 : 0) - (preB is null ? 1 : 0);
        }

        // Identifier by identifier: numbers numerically and below words, words by ordinal;
        // a longer list wins when the shared part is equal (beta < beta.1).
        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            var numericA = int.TryParse(preA[i], out var na);
            var numericB = int.TryParse(preB[i], out var nb);
            var order = (numericA, numericB) switch
            {
                (true, true) => na.CompareTo(nb),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(preA[i], preB[i]),
            };

            if (order != 0)
            {
                return Math.Sign(order);
            }
        }

        return preA.Length.CompareTo(preB.Length);
    }

    private static string Architecture => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                                          System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>An asset by its exact name, over HTTPS only.</summary>
    private static Uri? Asset(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (string.Equals(String(asset, "name"), name, StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(String(asset, "browser_download_url"), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps)
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>
    /// The SHA-256 a SHA256SUMS.txt lists for a file ("&lt;hex&gt;  &lt;name&gt;", the sha256sum
    /// format), lowercased; null when the file is not listed or the line is malformed.
    /// </summary>
    public static string? ExpectedHash(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>The installer for this architecture, falling back to any Windows package.</summary>
    private static Uri? DownloadAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                           System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

        Uri? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = String(asset, "name") ?? string.Empty;
            if (!Uri.TryCreate(String(asset, "browser_download_url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            var isPackage = name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (!isPackage)
            {
                continue;
            }

            if (name.Contains(architecture, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            fallback ??= url;
        }

        return fallback;
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
