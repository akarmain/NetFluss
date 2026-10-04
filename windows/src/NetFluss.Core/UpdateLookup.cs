// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Http.Headers;
using System.Text.Json;

namespace NetFluss.Core;

/// <summary>A newer release than the running one.</summary>
public sealed record AvailableUpdate(string Version, string ReleaseNotes, Uri ReleasePage, Uri? Download);

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

    public static async Task<AvailableUpdate?> FetchAsync(string currentVersion, HttpMessageHandler? handler = null, CancellationToken cancellation = default)
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
        return Newest(json, currentVersion);
    }

    /// <summary>The newest non-draft, non-prerelease Windows release newer than <paramref name="currentVersion"/>.</summary>
    public static AvailableUpdate? Newest(string releasesJson, string currentVersion)
    {
        using var document = JsonDocument.Parse(releasesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        AvailableUpdate? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (Bool(release, "draft") || Bool(release, "prerelease"))
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

            best = new AvailableUpdate(version, String(release, "body") ?? string.Empty, page, DownloadAsset(release));
        }

        return best;
    }

    /// <summary>Numeric, component-wise; missing components count as zero, as on macOS.</summary>
    public static bool IsNewer(string latest, string current)
    {
        static int[] Parts(string version) =>
        [
            .. version.Split('.', '-', '+')
                .TakeWhile(part => int.TryParse(part, out _))
                .Select(int.Parse),
        ];

        var a = Parts(latest);
        var b = Parts(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x > y;
            }
        }

        return false;
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
