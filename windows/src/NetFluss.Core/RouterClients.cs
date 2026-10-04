// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace NetFluss.Core;

/// <summary>
/// Fritz!Box over TR-064 — the macOS <c>FritzBoxMonitor</c>. <c>GetAddonInfos</c> returns live
/// byte rates without authentication; <c>GetCommonLinkProperties</c> the line's maxima.
/// Plain HTTP on port 49000, as AVM serves it.
/// </summary>
public static class FritzBoxClient
{
    private const string Service = "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1";

    public static async Task<RouterBandwidth> FetchAsync(HttpClient http, string host, CancellationToken cancellation = default)
    {
        var xml = await CallAsync(http, host, "GetAddonInfos", cancellation).ConfigureAwait(false);
        return ParseAddonInfos(xml);
    }

    public static async Task<(ulong MaxDown, ulong MaxUp)> FetchLinkAsync(HttpClient http, string host, CancellationToken cancellation = default)
        => ParseLinkProperties(await CallAsync(http, host, "GetCommonLinkProperties", cancellation).ConfigureAwait(false));

    /// <summary>Only host characters reach the URL — the address is user-typed.</summary>
    public static bool IsSafeHost(string host)
        => host.Length > 0 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']');

    private static async Task<string> CallAsync(HttpClient http, string host, string action, CancellationToken cancellation)
    {
        var trimmed = host.Trim();
        if (trimmed.Length == 0 || trimmed == "—" || !IsSafeHost(trimmed))
        {
            throw new RouterException(RouterFailure.InvalidAddress);
        }

        var hostPart = trimmed.Contains(':', StringComparison.Ordinal) && !trimmed.StartsWith('[') ? $"[{trimmed}]" : trimmed;
        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body><u:{action} xmlns:u="{Service}"/></s:Body>
            </s:Envelope>
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{hostPart}:49000/igdupnp/control/WANCommonIFC1")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"{Service}#{action}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new RouterException(RouterFailure.HttpStatus, status: (int)response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    private static Dictionary<string, string> Values(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Descendants()
                .Where(e => !e.HasElements)
                .GroupBy(e => e.Name.LocalName)
                .ToDictionary(g => g.Key, g => g.First().Value.Trim());
        }
        catch (System.Xml.XmlException e)
        {
            throw new RouterException(RouterFailure.Parse, inner: e);
        }
    }

    public static RouterBandwidth ParseAddonInfos(string xml)
    {
        var values = Values(xml);
        if (!Double(values, "NewByteReceiveRate", out var rx) || !Double(values, "NewByteSendRate", out var tx))
        {
            throw new RouterException(RouterFailure.Parse);
        }

        return new RouterBandwidth(rx, tx, 0, 0);
    }

    public static (ulong MaxDown, ulong MaxUp) ParseLinkProperties(string xml)
    {
        var values = Values(xml);
        return (ULong(values, "NewLayer1DownstreamMaxBitRate"), ULong(values, "NewLayer1UpstreamMaxBitRate"));
    }

    private static bool Double(Dictionary<string, string> values, string key, out double value)
    {
        value = 0;
        return values.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static ulong ULong(Dictionary<string, string> values, string key)
        => values.TryGetValue(key, out var text) && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary>Shared JSON helpers: router firmwares disagree about numbers versus strings.</summary>
internal static class RouterJson
{
    internal static ulong ULong(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<ulong>(out var u))
        {
            return u;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return (ulong)Math.Max(0, l);
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d > 0 ? (ulong)d : 0;
        }

        return value.TryGetValue<string>(out var s) && ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    internal static double Double(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        return value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    internal static int Int(JsonNode? node, int fallback = 0)
    {
        if (node is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        }

        return value.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    internal static bool Bool(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<bool>(out var b))
        {
            return b;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i != 0;
        }

        return value.TryGetValue<string>(out var s) && s.ToLowerInvariant() is "1" or "true" or "yes";
    }

    internal static string? String(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>
    /// A typed address as a base URL. Bare hosts are HTTPS only — no silent downgrade of
    /// credentials to plain HTTP; an explicit http:// is honoured for routers that need it.
    /// </summary>
    internal static Uri BaseUri(string address, bool allowHttp)
    {
        var trimmed = address.Trim();
        if (trimmed.Length == 0)
        {
            throw new RouterException(RouterFailure.InvalidAddress);
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
                !(uri.Scheme == Uri.UriSchemeHttps || (allowHttp && uri.Scheme == Uri.UriSchemeHttp)))
            {
                throw new RouterException(RouterFailure.InvalidAddress);
            }

            return new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri;
        }

        return Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var https)
            ? new UriBuilder(https.Scheme, https.Host, https.Port).Uri
            : throw new RouterException(RouterFailure.InvalidAddress);
    }
}

/// <summary>
/// UniFi gateways through the controller's local API — the macOS <c>UniFiMonitor</c>: a
/// local admin login (UniFi OS first, then the legacy controller on 8443), or a Network
/// API key, which survives 2FA and never expires.
/// </summary>
public sealed class UniFiClient(HttpClient http)
{
    private static readonly string[] GatewayTypes = ["ugw", "udm", "udr", "uxg", "ucg", "udw"];

    private string? _cookie;
    private string? _sessionHost;

    public async Task<RouterBandwidth> FetchAsync(string host, string username, string password, CancellationToken cancellation = default)
    {
        var trimmed = Validate(host);
        if (_cookie is null || _sessionHost != trimmed)
        {
            await LoginAsync(trimmed, username, password, cancellation).ConfigureAwait(false);
        }

        Exception? transport = null;
        foreach (var url in DevicePaths(trimmed))
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Get, url, null, cancellation, ("Cookie", _cookie!)).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The session expired: sign in again and retry once.
                    _cookie = null;
                    await LoginAsync(trimmed, username, password, cancellation).ConfigureAwait(false);
                    using var retry = await SendAsync(HttpMethod.Get, url, null, cancellation, ("Cookie", _cookie!)).ConfigureAwait(false);
                    if (retry.StatusCode == HttpStatusCode.OK)
                    {
                        return ParseDevices(await retry.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
                    }

                    continue;
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return ParseDevices(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
                }
            }
            catch (RouterException)
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
            {
                transport ??= e;
            }
        }

        throw new RouterException(RouterFailure.Transport, inner: transport);
    }

    public async Task<RouterBandwidth> FetchWithKeyAsync(string host, string apiKey, CancellationToken cancellation = default)
    {
        var trimmed = Validate(host);
        var key = apiKey.Trim();
        if (key.Length == 0)
        {
            throw new RouterException(RouterFailure.AuthFailed);
        }

        var authFailed = false;
        Exception? transport = null;
        foreach (var url in DevicePaths(trimmed))
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Get, url, null, cancellation, ("X-API-KEY", key), ("Accept", "application/json")).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    authFailed = true;
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return ParseDevices(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
                }
            }
            catch (RouterException)
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
            {
                transport ??= e;
            }
        }

        throw authFailed ? new RouterException(RouterFailure.AuthFailed) : new RouterException(RouterFailure.Transport, inner: transport);
    }

    private async Task LoginAsync(string host, string username, string password, CancellationToken cancellation)
    {
        var paths = new List<string> { $"https://{host}/api/auth/login" };
        if (!host.Contains(':', StringComparison.Ordinal))
        {
            paths.Add($"https://{host}:8443/api/login");
        }

        var sawResponse = false;
        var twoFactor = false;
        Exception? transport = null;
        var body = JsonSerializer.Serialize(new { username, password });

        foreach (var url in paths)
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Post, url, body, cancellation).ConfigureAwait(false);
                sawResponse = true;
                if (response.StatusCode == HttpStatusCode.OK && CookieHeader(response) is { Length: > 0 } cookie)
                {
                    _cookie = cookie;
                    _sessionHost = host;
                    return;
                }

                // UniFi OS answers a right password on a 2FA account with HTTP 499 or a
                // {"required":"2fa"} body.
                if ((int)response.StatusCode == 499)
                {
                    twoFactor = true;
                }
                else
                {
                    var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
                    twoFactor |= RequiresTwoFactor(text);
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
            {
                transport ??= e;
            }
        }

        if (twoFactor)
        {
            throw new RouterException(RouterFailure.TwoFactorRequired);
        }

        throw sawResponse ? new RouterException(RouterFailure.AuthFailed) : new RouterException(RouterFailure.Transport, inner: transport);
    }

    internal static bool RequiresTwoFactor(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["required"] is JsonValue required &&
                   required.TryGetValue<string>(out var text) &&
                   (text.Contains("2fa", StringComparison.OrdinalIgnoreCase) || text.Contains("mfa", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? CookieHeader(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("; ", values.Select(v => v.Split(';')[0].Trim()).Where(v => v.Contains('=', StringComparison.Ordinal)))
            : null;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? json, CancellationToken cancellation, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await http.SendAsync(request, cancellation).ConfigureAwait(false);
    }

    private static string Validate(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.Length == 0 || !Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out _))
        {
            throw new RouterException(RouterFailure.InvalidAddress);
        }

        return trimmed;
    }

    private static IEnumerable<string> DevicePaths(string host)
    {
        yield return $"https://{host}/proxy/network/api/s/default/stat/device";
        if (!host.Contains(':', StringComparison.Ordinal))
        {
            yield return $"https://{host}:8443/api/s/default/stat/device";
        }
    }

    /// <summary>The gateway's WAN rates from <c>stat/device</c>; max speed is in Mbps there.</summary>
    public static RouterBandwidth ParseDevices(string json)
    {
        JsonArray? devices;
        try
        {
            devices = JsonNode.Parse(json)?["data"] as JsonArray;
        }
        catch (JsonException e)
        {
            throw new RouterException(RouterFailure.Parse, inner: e);
        }

        if (devices is null)
        {
            throw new RouterException(RouterFailure.Parse);
        }

        var objects = devices.OfType<JsonObject>().ToList();

        // Known console types first; otherwise whatever device carries a WAN uplink —
        // access points and switches never report wan1.
        var gateway = objects.FirstOrDefault(d => RouterJson.String(d["type"]) is { } type && GatewayTypes.Contains(type))
                      ?? objects.FirstOrDefault(d => d["wan1"] is JsonObject)
                      ?? throw new RouterException(RouterFailure.NoGateway);

        var wan = gateway["wan1"] as JsonObject ?? gateway["uplink"] as JsonObject ?? new JsonObject();
        var max = RouterJson.ULong(wan["max_speed"]);
        if (max == 0)
        {
            max = RouterJson.ULong(wan["speed"]);
        }

        return new RouterBandwidth(RouterJson.Double(wan["rx_bytes-r"]), RouterJson.Double(wan["tx_bytes-r"]), max * 1_000_000, max * 1_000_000);
    }
}

/// <summary>
/// OpenWRT over ubus JSON-RPC — the macOS <c>OpenWRTMonitor</c>: session login, then the
/// WAN device (from <c>network.interface.wan</c>, else the default-route interface in a
/// dump) and its byte counters. Rates come from the difference of two samples.
/// </summary>
public sealed class OpenWrtClient(HttpClient http)
{
    private const string AnonymousSession = "00000000000000000000000000000000";
    private const int PermissionDenied = 6;

    private string? _token;
    private Uri? _url;
    private string? _sessionHost;

    public async Task<RouterCounterSample> SampleAsync(string host, string username, string password, CancellationToken cancellation = default)
    {
        var trimmed = host.Trim();
        if (_token is null || _sessionHost != trimmed || _url is null)
        {
            await LoginAsync(trimmed, username, password, cancellation).ConfigureAwait(false);
        }

        try
        {
            return await SampleOnceAsync(cancellation).ConfigureAwait(false);
        }
        catch (RouterException e) when (e.Failure == RouterFailure.AuthFailed)
        {
            // The session expired: sign in again and retry once.
            await LoginAsync(trimmed, username, password, cancellation).ConfigureAwait(false);
            return await SampleOnceAsync(cancellation).ConfigureAwait(false);
        }
    }

    private async Task<RouterCounterSample> SampleOnceAsync(CancellationToken cancellation)
    {
        var device = await DiscoverWanAsync(cancellation).ConfigureAwait(false);
        var status = await CallAsync(_url!, _token!, "network.device", "status", new JsonObject { ["name"] = device }, cancellation).ConfigureAwait(false);
        return ParseDeviceStatus(status, DateTimeOffset.UtcNow);
    }

    private async Task LoginAsync(string host, string username, string password, CancellationToken cancellation)
    {
        _token = null;
        _url = null;
        _sessionHost = null;
        var url = new Uri(RouterJson.BaseUri(host, allowHttp: true), "/ubus");
        try
        {
            var result = await CallAsync(url, AnonymousSession, "session", "login", new JsonObject { ["username"] = username, ["password"] = password }, cancellation).ConfigureAwait(false);
            _token = RouterJson.String(result["ubus_rpc_session"]) ?? throw new RouterException(RouterFailure.AuthFailed);
            _url = url;
            _sessionHost = host;
        }
        catch (RouterException e) when (e.Failure == RouterFailure.RpcFailure && e.Status == PermissionDenied)
        {
            throw new RouterException(RouterFailure.AuthFailed);
        }
    }

    private async Task<string> DiscoverWanAsync(CancellationToken cancellation)
    {
        try
        {
            var status = await CallAsync(_url!, _token!, "network.interface.wan", "status", new JsonObject(), cancellation).ConfigureAwait(false);
            if (DeviceName(status) is { } device)
            {
                return device;
            }
        }
        catch (RouterException e) when (e.Failure == RouterFailure.AuthFailed)
        {
            throw;
        }
        catch (RouterException)
        {
            // No "wan" object on this router: fall back to the full interface dump.
        }

        var dump = await CallAsync(_url!, _token!, "network.interface", "dump", new JsonObject(), cancellation).ConfigureAwait(false);
        return PickWanDevice(dump["interface"] as JsonArray ?? new JsonArray()) ?? throw new RouterException(RouterFailure.NoWan);
    }

    private async Task<JsonObject> CallAsync(Uri url, string session, string obj, string method, JsonObject arguments, CancellationToken cancellation)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "call",
            ["params"] = new JsonArray(session, obj, method, arguments),
        };

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new RouterException(RouterFailure.Transport, inner: e);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return ParseRpc(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false), out var denied) is { } payload
                        ? payload
                        : throw (denied ? Reset() : new RouterException(RouterFailure.Parse));
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new RouterException(RouterFailure.AuthFailed);
                case HttpStatusCode.NotFound:
                    throw new RouterException(RouterFailure.UbusUnavailable);
                default:
                    throw new RouterException(RouterFailure.HttpStatus, status: (int)response.StatusCode);
            }
        }

        RouterException Reset()
        {
            _token = null;
            return new RouterException(RouterFailure.AuthFailed);
        }
    }

    /// <summary>
    /// A ubus reply's payload. Throws for JSON-RPC and ubus errors; returns null with
    /// <paramref name="permissionDenied"/> set when the session is no longer valid.
    /// </summary>
    public static JsonObject? ParseRpc(string json, out bool permissionDenied)
    {
        permissionDenied = false;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw new RouterException(RouterFailure.UbusUnavailable);
        }

        if (root is not JsonObject obj)
        {
            throw new RouterException(RouterFailure.UbusUnavailable);
        }

        if (obj["error"] is JsonObject error)
        {
            throw new RouterException(RouterFailure.RpcFailure, RouterJson.String(error["message"])?.Trim() ?? "JSON-RPC error", RouterJson.Int(error["code"]));
        }

        if (obj["result"] is not JsonArray { Count: > 0 } result)
        {
            throw new RouterException(RouterFailure.UbusUnavailable);
        }

        var code = RouterJson.Int(result[0]);
        if (code == PermissionDenied)
        {
            permissionDenied = true;
            return null;
        }

        if (code != 0)
        {
            throw new RouterException(RouterFailure.RpcFailure, StatusMessage(code), code);
        }

        return result.Count < 2 ? new JsonObject() : result[1] as JsonObject ?? throw new RouterException(RouterFailure.Parse);
    }

    private static string StatusMessage(int code) => code switch
    {
        1 => "Invalid command",
        2 => "Invalid argument",
        3 => "Method not found",
        4 => "Not found",
        5 => "No response",
        6 => "Permission denied",
        7 => "Request timed out",
        8 => "Operation not supported",
        10 => "Connection failed",
        11 => "Out of memory",
        12 => "Parsing message data failed",
        13 => "System error",
        _ => "Unknown error: " + code.ToString(CultureInfo.InvariantCulture),
    };

    public static RouterCounterSample ParseDeviceStatus(JsonObject device, DateTimeOffset timestamp)
    {
        var statistics = device["statistics"] as JsonObject ?? new JsonObject();
        return new RouterCounterSample(RouterJson.ULong(statistics["rx_bytes"]), RouterJson.ULong(statistics["tx_bytes"]), RouterJson.ULong(device["speed"]), timestamp);
    }

    private static string? DeviceName(JsonNode? iface)
        => RouterJson.String(iface?["l3_device"]) ?? RouterJson.String(iface?["device"]);

    /// <summary>The active interface with the lowest-metric default route, else one named wan*.</summary>
    public static string? PickWanDevice(JsonArray interfaces)
    {
        var all = interfaces.OfType<JsonObject>().ToList();
        var active = all.Where(i => RouterJson.Bool(i["up"]) || RouterJson.Bool(i["pending"])).ToList();

        static IEnumerable<JsonObject> DefaultRoutes(JsonObject iface)
            => (iface["route"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Where(r => RouterJson.Int(r["mask"], -1) == 0 && (RouterJson.String(r["target"]) ?? string.Empty) is "0.0.0.0" or "::" or "");

        var routed = active
            .Select(i => (Device: DeviceName(i), Routes: DefaultRoutes(i).ToList()))
            .Where(x => x.Device is not null && x.Routes.Count > 0)
            .OrderBy(x => x.Routes.Min(r => RouterJson.Int(r["metric"], int.MaxValue)))
            .Select(x => x.Device)
            .FirstOrDefault();

        return routed
               ?? active.Where(i => (RouterJson.String(i["interface"]) ?? string.Empty).StartsWith("wan", StringComparison.OrdinalIgnoreCase)).Select(DeviceName).FirstOrDefault(d => d is not null)
               ?? active.Select(DeviceName).FirstOrDefault(d => d is not null)
               ?? all.Select(DeviceName).FirstOrDefault(d => d is not null);
    }
}

/// <summary>
/// OPNsense through its REST API — the macOS <c>OPNsenseMonitor</c>: API key and secret as
/// Basic auth, <c>/api/diagnostics/traffic/interface</c> for the WAN's counters.
/// </summary>
public sealed class OpnSenseClient(HttpClient http)
{
    private const string Path = "api/diagnostics/traffic/interface";

    public async Task<RouterCounterSample> SampleAsync(string host, string apiKey, string apiSecret, CancellationToken cancellation = default)
    {
        var baseUri = RouterJson.BaseUri(host, allowHttp: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, Path));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:{apiSecret}")));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new RouterException(RouterFailure.Transport, inner: e);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new RouterException(RouterFailure.AuthFailed);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new RouterException(RouterFailure.HttpStatus, status: (int)response.StatusCode);
            }

            return ParseTraffic(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false), DateTimeOffset.UtcNow);
        }
    }

    /// <summary>The WAN entry ("wan", else the first "wan*") of the traffic interface reply.</summary>
    public static RouterCounterSample ParseTraffic(string json, DateTimeOffset timestamp)
    {
        JsonObject? interfaces;
        try
        {
            interfaces = JsonNode.Parse(json)?["interfaces"] as JsonObject;
        }
        catch (JsonException e)
        {
            throw new RouterException(RouterFailure.Parse, inner: e);
        }

        if (interfaces is null)
        {
            throw new RouterException(RouterFailure.Parse);
        }

        var ids = interfaces.Select(p => p.Key).ToList();
        var wan = ids.FirstOrDefault(id => id.Equals("wan", StringComparison.OrdinalIgnoreCase))
                  ?? ids.FirstOrDefault(id => id.StartsWith("wan", StringComparison.OrdinalIgnoreCase))
                  ?? ids.FirstOrDefault();
        if (wan is null || interfaces[wan] is not JsonObject data)
        {
            throw new RouterException(RouterFailure.NoWan);
        }

        // "line rate" reads like "1000000000 bit/s".
        var lineRate = RouterJson.String(data["line rate"])?.Split(' ')[0];
        var mbps = ulong.TryParse(lineRate, NumberStyles.None, CultureInfo.InvariantCulture, out var bits) ? bits / 1_000_000 : 0;
        return new RouterCounterSample(RouterJson.ULong(data["bytes received"]), RouterJson.ULong(data["bytes transmitted"]), mbps, timestamp);
    }
}
