// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text.Json;

namespace NetFluss.Core;

/// <summary>The internet-facing address and, when asked for, the country it geolocates to.</summary>
public sealed record PublicIp(string Address, string? CountryCode);

/// <summary>
/// Looks up the external address. Port of the macOS <c>fetchExternalIP</c>: ipify for the
/// address (IPv4 or IPv6 by preference), then ipwho.is for the country, only when something
/// on screen actually shows a flag — a lookup per refresh would hand a third party a log of
/// every network the machine has been on for no visible benefit.
/// </summary>
public sealed class PublicIpLookup : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;

    public PublicIpLookup(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFluss-Windows");
    }

    public async Task<PublicIp?> LookupAsync(bool preferIPv6, bool includeCountry, CancellationToken cancellation = default)
    {
        var address = await FetchAddressAsync(preferIPv6, cancellation).ConfigureAwait(false);
        if (address is null)
        {
            return null;
        }

        string? country = null;
        if (includeCountry)
        {
            country = await FetchCountryAsync(address, cancellation).ConfigureAwait(false);
        }

        return new PublicIp(address, country);
    }

    private async Task<string?> FetchAddressAsync(bool preferIPv6, CancellationToken cancellation)
    {
        var url = preferIPv6 ? "https://api64.ipify.org?format=json" : "https://api.ipify.org?format=json";
        try
        {
            var json = await _http.GetStringAsync(url, cancellation).ConfigureAwait(false);
            return ParseIpify(json);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<string?> FetchCountryAsync(string address, CancellationToken cancellation)
    {
        try
        {
            // The address is validated before it reaches a URL: it came from a remote
            // service, and only something that parses as an IP is allowed into a path.
            if (!IPAddress.TryParse(address, out var parsed))
            {
                return null;
            }

            var json = await _http.GetStringAsync($"https://ipwho.is/{parsed}", cancellation).ConfigureAwait(false);
            return ParseIpWhoCountry(json);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary><c>{"ip":"203.0.113.7"}</c> → the address, if it really is one.</summary>
    public static string? ParseIpify(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("ip", out var ip) &&
            ip.ValueKind == JsonValueKind.String &&
            IPAddress.TryParse(ip.GetString(), out var address))
        {
            return address.ToString();
        }

        return null;
    }

    /// <summary><c>{"success":true,"country_code":"DE",…}</c> → "DE".</summary>
    public static string? ParseIpWhoCountry(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        if (root.TryGetProperty("country_code", out var code) && code.ValueKind == JsonValueKind.String)
        {
            var value = code.GetString();
            if (value is { Length: 2 } && value.All(char.IsAsciiLetter))
            {
                return value.ToUpperInvariant();
            }
        }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
