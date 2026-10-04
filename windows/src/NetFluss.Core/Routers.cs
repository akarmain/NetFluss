// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace NetFluss.Core;

/// <summary>The four router integrations, as on macOS.</summary>
public enum RouterKind
{
    FritzBox,
    UniFi,
    OpenWrt,
    OpnSense,
}

public static class RouterKinds
{
    public static string DisplayName(this RouterKind kind) => kind switch
    {
        RouterKind.FritzBox => "Fritz!Box",
        RouterKind.UniFi => "UniFi",
        RouterKind.OpenWrt => "OpenWRT",
        _ => "OPNsense",
    };

    /// <summary>The Credential Manager service names; the macOS keychain services, renamed.</summary>
    public static string CredentialService(this RouterKind kind) => kind switch
    {
        RouterKind.UniFi => "unifi",
        RouterKind.OpenWrt => "openwrt",
        RouterKind.OpnSense => "opnsense",
        _ => "fritzbox",
    };
}

/// <summary>
/// One reading of router-wide WAN traffic. Rates are bytes per second; the link maxima
/// are bits per second, zero when the router does not say.
/// </summary>
public sealed record RouterBandwidth(double RxRate, double TxRate, ulong MaxDownBits, ulong MaxUpBits);

/// <summary>A router's raw WAN byte counters, for the routers that only report totals.</summary>
public sealed record RouterCounterSample(ulong RxBytes, ulong TxBytes, ulong LinkSpeedMbps, DateTimeOffset Timestamp)
{
    /// <summary>Rates between two samples; null when no time passed. A counter reset reads as zero.</summary>
    public static RouterBandwidth? Rates(RouterCounterSample previous, RouterCounterSample current)
    {
        var seconds = (current.Timestamp - previous.Timestamp).TotalSeconds;
        if (seconds <= 0)
        {
            return null;
        }

        var rx = current.RxBytes >= previous.RxBytes ? current.RxBytes - previous.RxBytes : 0;
        var tx = current.TxBytes >= previous.TxBytes ? current.TxBytes - previous.TxBytes : 0;
        var link = (current.LinkSpeedMbps > 0 ? current.LinkSpeedMbps : previous.LinkSpeedMbps) * 1_000_000;
        return new RouterBandwidth(rx / seconds, tx / seconds, link, link);
    }
}

/// <summary>What went wrong talking to a router, before it is put into words.</summary>
public enum RouterFailure
{
    InvalidAddress,
    AuthFailed,
    TwoFactorRequired,
    NoGateway,
    UbusUnavailable,
    HttpStatus,
    RpcFailure,
    NoWan,
    Parse,
    Transport,
    MissingCredentials,
}

public sealed class RouterException : Exception
{
    public RouterException(RouterFailure failure, string? detail = null, int status = 0, Exception? inner = null)
        : base(detail ?? failure.ToString(), inner)
    {
        Failure = failure;
        Detail = detail;
        Status = status;
    }

    public RouterFailure Failure { get; }

    public string? Detail { get; }

    /// <summary>The HTTP status or ubus code, when there is one.</summary>
    public int Status { get; }
}

/// <summary>
/// Trust-on-first-use certificate pinning for the router monitors — the macOS
/// <c>TLSPinStore</c>. Routers use self-signed certificates no trust store can vouch for,
/// and accepting any certificate would let a machine on the LAN collect the admin
/// password. So the first key a router presents is remembered, and a silent change is
/// refused until the user re-enters the router's address.
/// </summary>
public sealed class RouterPinStore
{
    private readonly string? _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _pins;
    private readonly HashSet<string> _mismatched = [];

    public RouterPinStore(string? path)
    {
        _path = path;
        _pins = Load(path);
    }

    public static string DefaultPath(string localAppData) => Path.Combine(localAppData, "NetFluss", "router-pins.json");

    public static string HostKey(string host, int port) => $"{host.ToLowerInvariant()}:{port}";

    /// <summary>
    /// The bare lowercased host of an address as typed — "https://Router.lan:8443/x",
    /// "router.lan:8443" and "router.lan" all give "router.lan" — the host part of the pins.
    /// </summary>
    public static string NormalizedHost(string address)
    {
        var trimmed = address.Trim();
        var withScheme = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
        var host = Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) ? uri.Host : trimmed;
        return host.Trim('[', ']').ToLowerInvariant();
    }

    /// <summary>The check run on every TLS handshake to a router.</summary>
    public bool Validate(string host, int port, X509Certificate2? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        var key = HostKey(host.Trim('[', ']'), port);
        var presented = Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

        lock (_lock)
        {
            if (_pins.TryGetValue(key, out var pinned))
            {
                var matches = pinned == presented;
                if (matches)
                {
                    _mismatched.Remove(key);
                }
                else
                {
                    _mismatched.Add(key);
                }

                return matches;
            }

            _pins[key] = presented;
            _mismatched.Remove(key);
            Save();
            return true;
        }
    }

    /// <summary>Forgets a host's pins so its next connection trusts on first use again.</summary>
    public void ResetTrust(string address)
    {
        var needle = NormalizedHost(address);
        if (needle.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var key in _pins.Keys.Where(k => Matches(k, needle)).ToList())
            {
                _pins.Remove(key);
            }

            _mismatched.RemoveWhere(k => Matches(k, needle));
            Save();
        }
    }

    /// <summary>Whether the host (any port) recently presented a different key than its pin.</summary>
    public bool CertificateChanged(string address)
    {
        var needle = NormalizedHost(address);
        lock (_lock)
        {
            return needle.Length > 0 && _mismatched.Any(k => Matches(k, needle));
        }
    }

    private static bool Matches(string key, string host) => key == host || key.StartsWith(host + ":", StringComparison.Ordinal);

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_pins));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Pins then last for this session only, which is still TOFU within it.
        }
    }

    private static Dictionary<string, string> Load(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>An HTTP client whose TLS trust is this store. One per app, reused across polls.</summary>
    public HttpClient CreateClient()
    {
        // HttpClientHandler rather than SocketsHttpHandler: its callback is handed the
        // request, so the pin can be keyed by host and port as on macOS.
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
                request.RequestUri is { } uri && Validate(uri.Host, uri.Port, certificate),
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }
}

/// <summary>
/// Precise wording for router failures — the macOS <c>RouterConnectionDiagnosis</c> and the
/// <c>describe…Error</c> functions. A generic "cannot reach" hid the two real causes users
/// hit: HTTPS-only bare addresses, and a pinned certificate that changed.
/// </summary>
public static class RouterDiagnosis
{
    public static string RetrustHint => Localization.L("To re-trust it, re-enter the router address in Preferences.");

    public static string? CertificateChanged(RouterPinStore pins, RouterKind kind, string host)
        => pins.CertificateChanged(host)
            ? Localization.L("{0}'s TLS certificate changed since NetFluss first trusted it.", kind.DisplayName())
              + " " + Localization.L("If you didn't change the router, this could be an interception attempt.")
              + " " + RetrustHint
            : null;

    /// <summary>The message for any failure of one router at one address.</summary>
    public static string Describe(RouterKind kind, Exception error, string host, bool usesAutoHost, RouterPinStore pins)
    {
        if (CertificateChanged(pins, kind, host) is { } changed)
        {
            return changed;
        }

        return kind switch
        {
            RouterKind.FritzBox => DescribeFritzBox(error, host, usesAutoHost),
            RouterKind.UniFi => DescribeUniFi(error, host, usesAutoHost, pins),
            RouterKind.OpenWrt => DescribeOpenWrt(error, host, usesAutoHost, pins),
            _ => DescribeOpnSense(error, host, usesAutoHost, pins),
        };
    }

    private static string DescribeFritzBox(Exception error, string host, bool usesAutoHost)
    {
        var unreachable = usesAutoHost
            ? Localization.L("Cannot reach Fritz!Box at {0}. Set the router address manually if auto detection picked the wrong gateway.", host)
            : Localization.L("Cannot reach Fritz!Box at {0}.", host);

        return error switch
        {
            RouterException { Failure: RouterFailure.InvalidAddress } when usesAutoHost => Localization.L("No Fritz!Box gateway detected. Set the router address manually."),
            RouterException { Failure: RouterFailure.InvalidAddress } => Localization.L("Enter a valid Fritz!Box address."),
            RouterException { Failure: RouterFailure.HttpStatus, Status: > 0 } e => Localization.L("Fritz!Box TR-064 request failed (HTTP {0}).", e.Status),
            RouterException { Failure: RouterFailure.HttpStatus } => Localization.L("Fritz!Box TR-064 request failed."),
            RouterException { Failure: RouterFailure.Parse } => Localization.L("Fritz!Box returned an unexpected TR-064 response."),
            _ when IsTimeout(error) => Localization.L("Fritz!Box did not respond in time."),
            _ => unreachable,
        };
    }

    private static string DescribeUniFi(Exception error, string host, bool usesAutoHost, RouterPinStore pins)
    {
        string WithHint(string message) => usesAutoHost
            ? message + " " + Localization.L("Set the controller address manually if auto detection picked the wrong gateway.")
            : message;

        return error switch
        {
            RouterException { Failure: RouterFailure.InvalidAddress } => Localization.L("Enter a valid UniFi controller address."),
            RouterException { Failure: RouterFailure.MissingCredentials, Detail: "apikey" } => Localization.L("No API key configured"),
            RouterException { Failure: RouterFailure.MissingCredentials } => Localization.L("No credentials configured"),
            RouterException { Failure: RouterFailure.AuthFailed } => Localization.L("UniFi login failed. Check the username and password — the local API needs a local admin account, not a UI.com cloud login."),
            RouterException { Failure: RouterFailure.TwoFactorRequired } => Localization.L("UniFi login failed because two-factor authentication is enabled on this account. Create a local admin account without 2FA for Netfluss to use."),
            RouterException { Failure: RouterFailure.NoGateway } => Localization.L("Connected to UniFi, but no gateway device was found in the controller response."),
            RouterException { Failure: RouterFailure.Parse } => Localization.L("UniFi returned an unexpected response."),
            _ => WithHint(Transport(RouterKind.UniFi, host, error, allowsHttp: false, pins)),
        };
    }

    private static string DescribeOpenWrt(Exception error, string host, bool usesAutoHost, RouterPinStore pins)
    {
        var autoHint = Localization.L("Auto uses the current default gateway. Set the OpenWRT address manually if that is a different router.");
        string WithHint(string message) => usesAutoHost ? message + " " + autoHint : message;

        return error switch
        {
            RouterException { Failure: RouterFailure.InvalidAddress } when usesAutoHost => Localization.L("No OpenWRT gateway address is available.") + " " + autoHint,
            RouterException { Failure: RouterFailure.InvalidAddress } => Localization.L("Enter a valid OpenWRT address or URL."),
            RouterException { Failure: RouterFailure.MissingCredentials } => Localization.L("No credentials configured"),
            RouterException { Failure: RouterFailure.AuthFailed } => Localization.L("OpenWRT login failed. Check the router credentials."),
            RouterException { Failure: RouterFailure.UbusUnavailable } => Localization.L("OpenWRT ubus is not available at {0}.", host) + " " + (usesAutoHost
                ? autoHint + " " + Localization.L("Install the uhttpd-mod-ubus package if needed.")
                : Localization.L("Install the uhttpd-mod-ubus package and check the router address.")),
            RouterException { Failure: RouterFailure.HttpStatus, Status: 404 } => WithHint(Localization.L("OpenWRT ubus was not found at {0}.", host)),
            RouterException { Failure: RouterFailure.HttpStatus } e => Localization.L("OpenWRT request failed (HTTP {0}).", e.Status),
            RouterException { Failure: RouterFailure.RpcFailure, Status: 3 or 4 } when usesAutoHost => Localization.L("OpenWRT network status is not available on {0}.", host) + " " + autoHint,
            RouterException { Failure: RouterFailure.RpcFailure, Status: 3 or 4 } => Localization.L("OpenWRT network status is not available on this router."),
            RouterException { Failure: RouterFailure.RpcFailure } e => Localization.L("OpenWRT returned an error: {0}.", e.Detail ?? string.Empty),
            RouterException { Failure: RouterFailure.NoWan } => Localization.L("OpenWRT responded, but no WAN interface could be identified."),
            RouterException { Failure: RouterFailure.Parse } => Localization.L("OpenWRT returned an unexpected ubus response."),
            _ => WithHint(Transport(RouterKind.OpenWrt, host, error, allowsHttp: true, pins)),
        };
    }

    private static string DescribeOpnSense(Exception error, string host, bool usesAutoHost, RouterPinStore pins)
    {
        var autoHint = Localization.L("Auto uses the current default gateway. Set the OPNsense address manually if that is a different router.");
        string WithHint(string message) => usesAutoHost ? message + " " + autoHint : message;

        return error switch
        {
            RouterException { Failure: RouterFailure.InvalidAddress } when usesAutoHost => Localization.L("No OPNsense gateway address is available.") + " " + autoHint,
            RouterException { Failure: RouterFailure.InvalidAddress } => Localization.L("Enter a valid OPNsense address or URL."),
            RouterException { Failure: RouterFailure.MissingCredentials } => Localization.L("No credentials configured"),
            RouterException { Failure: RouterFailure.AuthFailed } => Localization.L("OPNsense login failed. Check the API credentials."),
            RouterException { Failure: RouterFailure.HttpStatus } e => Localization.L("OPNsense request failed (HTTP {0}).", e.Status),
            RouterException { Failure: RouterFailure.NoWan } => Localization.L("OPNsense responded, but no WAN interface could be identified."),
            RouterException { Failure: RouterFailure.Parse } => Localization.L("OPNsense returned an unexpected response."),
            _ => WithHint(Transport(RouterKind.OpnSense, host, error, allowsHttp: true, pins)),
        };
    }

    /// <summary>The transport half: refused, timed out, unresolvable, or a TLS failure.</summary>
    public static string Transport(RouterKind kind, string host, Exception error, bool allowsHttp, RouterPinStore pins)
    {
        var router = kind.DisplayName();
        if (CertificateChanged(pins, kind, host) is { } changed)
        {
            return changed;
        }

        var address = host.Trim();
        var hasScheme = address.Contains("://", StringComparison.Ordinal);
        var hostName = RouterPinStore.NormalizedHost(address);
        int? explicitPort = Uri.TryCreate(hasScheme ? address : "https://" + address, UriKind.Absolute, out var uri) && !uri.IsDefaultPort ? uri.Port : null;
        var usesHttps = !hasScheme || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // A TLS failure means something answered on that port (often plain HTTP), so keep
        // the port in the suggestion; a refusal means nothing listens there.
        string HttpHint(bool keepPort)
        {
            if (!allowsHttp || !usesHttps)
            {
                return string.Empty;
            }

            var port = keepPort && explicitPort is { } p ? ":" + p : string.Empty;
            return " " + Localization.L("If its web interface uses plain HTTP, enter the address as {0}.", $"http://{hostName}{port}");
        }

        if (IsTimeout(error))
        {
            return Localization.L("{0} at {1} did not respond in time.", router, address);
        }

        var socket = Find<SocketException>(error);
        var http = Find<HttpRequestException>(error);

        if (socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain ||
            http?.HttpRequestError == HttpRequestError.NameResolutionError)
        {
            return Localization.L("Cannot resolve {0}. Check the router address.", hostName);
        }

        if (socket?.SocketErrorCode == SocketError.ConnectionRefused)
        {
            if (!usesHttps)
            {
                return Localization.L("{0} at {1} refused the connection. Check the address and port.", router, address);
            }

            var portHint = explicitPort is null
                ? " " + Localization.L("If HTTPS runs on another port, enter {0}.", $"https://{hostName}:{Localization.L("<port>")}")
                : string.Empty;
            return Localization.L("{0} at {1} refused the HTTPS connection on port {2}.", router, address, explicitPort ?? 443) + HttpHint(false) + portHint;
        }

        if (http?.HttpRequestError == HttpRequestError.SecureConnectionError || Find<System.Security.Authentication.AuthenticationException>(error) is not null)
        {
            return Localization.L("The HTTPS (TLS) connection to {0} at {1} failed.", router, address) + HttpHint(true);
        }

        if (socket?.SocketErrorCode is SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.ConnectionReset)
        {
            return Localization.L("Lost the network connection to {0} at {1}.", router, address);
        }

        var reason = (socket?.Message ?? http?.Message ?? error.Message).Trim();
        return reason.Length > 0
            ? Localization.L("Cannot reach {0} at {1} ({2}).", router, address, reason)
            : Localization.L("Cannot reach {0} at {1}.", router, address);
    }

    private static bool IsTimeout(Exception error)
        => error is TaskCanceledException or TimeoutException || Find<TimeoutException>(error) is not null ||
           Find<SocketException>(error)?.SocketErrorCode == SocketError.TimedOut;

    private static T? Find<T>(Exception? error)
        where T : Exception
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is T match)
            {
                return match;
            }
        }

        return null;
    }
}
