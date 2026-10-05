// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Http;
using NetFluss.Core;
using NetFluss.Native;

namespace NetFluss.App;

/// <summary>What the popover and Preferences show for one router.</summary>
internal sealed record RouterState(RouterBandwidth? Bandwidth, string? Error)
{
    internal static readonly RouterState Empty = new(null, null);
}

/// <summary>
/// The router integrations' polling — the router half of the macOS <c>NetworkMonitor</c>:
/// every five seconds while something shows router traffic, each enabled router is asked
/// for its WAN rates, with exponential backoff (up to a minute) for one that keeps failing
/// so an unreachable router is not hammered.
/// </summary>
internal sealed class RouterService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A Fritz!Box that has answered before keeps its last reading through two failed polls
    /// before an error replaces it: TR-064 drops the odd request under load.
    /// </summary>
    private const int FritzBoxFailureThreshold = 3;

    private readonly NetworkMonitorService _monitor;
    private readonly SettingsStore _store;
    private readonly HttpClient _http;
    private readonly HttpClient _plainHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly UniFiClient _uniFi;
    private readonly OpenWrtClient _openWrt;
    private readonly OpnSenseClient _opnSense;
    private readonly Dictionary<RouterKind, RouterState> _states = [];
    private readonly Dictionary<RouterKind, (DateTime Next, int Failures)> _backoff = [];
    private readonly HashSet<RouterKind> _inFlight = [];
    private readonly Dictionary<RouterKind, (string Host, RouterCounterSample Sample)> _lastSample = [];
    private (ulong Down, ulong Up)? _fritzLink;
    private int _fritzFailures;
    private DateTime _lastPoll = DateTime.MinValue;
    private int _demand;

    internal RouterService(NetworkMonitorService monitor, SettingsStore store)
    {
        _monitor = monitor;
        _store = store;
        Pins = new RouterPinStore(RouterPinStore.DefaultPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        _http = Pins.CreateClient();
        _uniFi = new UniFiClient(_http);
        _openWrt = new OpenWrtClient(_http);
        _opnSense = new OpnSenseClient(_http);
        _monitor.Ticked += OnTick;
        _store.Settings.PropertyChanged += OnSettingChanged;
    }

    /// <summary>Raised on the UI thread when any router's state changes.</summary>
    internal event EventHandler? Changed;

    internal RouterPinStore Pins { get; }

    internal RouterState State(RouterKind kind) => _states.GetValueOrDefault(kind) ?? RouterState.Empty;

    /// <summary>The address in use: the one typed in Preferences, else the default gateway.</summary>
    internal string Host(RouterKind kind)
    {
        var typed = TypedHost(kind);
        return typed.Length > 0 ? typed : _monitor.Addresses.GatewayIp ?? string.Empty;
    }

    internal string TypedHost(RouterKind kind)
    {
        var settings = _store.Settings;
        return kind switch
        {
            RouterKind.FritzBox => settings.FritzBoxHost,
            RouterKind.UniFi => settings.UniFiHost,
            RouterKind.OpenWrt => settings.OpenWrtHost,
            _ => settings.OpnSenseHost,
        };
    }

    internal bool IsEnabled(RouterKind kind)
    {
        var settings = _store.Settings;
        return kind switch
        {
            RouterKind.FritzBox => settings.FritzBoxEnabled,
            RouterKind.UniFi => settings.UniFiEnabled,
            RouterKind.OpenWrt => settings.OpenWrtEnabled,
            _ => settings.OpnSenseEnabled,
        };
    }

    /// <summary>
    /// The Credential Manager key for a router: its host and port, so two routers at
    /// different addresses keep separate passwords — the macOS <c>credentialAccount</c>.
    /// </summary>
    internal static string Account(string host)
    {
        var trimmed = host.Trim();
        return Uri.TryCreate(trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed, UriKind.Absolute, out var uri)
            ? (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}")
            : trimmed;
    }

    private static string ApiKeyService => RouterKind.UniFi.CredentialService() + "-apikey";

    internal bool HasCredentials(RouterKind kind)
    {
        var account = Account(Host(kind));
        return kind == RouterKind.UniFi && _store.Settings.UniFiUseApiKey
            ? CredentialStore.Exists(ApiKeyService, account)
            : CredentialStore.Exists(kind.CredentialService(), account);
    }

    internal void SaveCredentials(RouterKind kind, string first, string second)
    {
        var account = Account(Host(kind));
        if (kind == RouterKind.UniFi && _store.Settings.UniFiUseApiKey)
        {
            CredentialStore.Save(ApiKeyService, account, string.Empty, first.Trim());
        }
        else
        {
            CredentialStore.Save(kind.CredentialService(), account, first.Trim(), second);
        }

        // New credentials are a reason to try right now, not after a backoff.
        _backoff.Remove(kind);
        PollNow();
    }

    /// <summary>A new address: forget its certificate pin (the deliberate re-trust) and poll.</summary>
    internal void AddressChanged(RouterKind kind)
    {
        Pins.ResetTrust(Host(kind));
        _lastSample.Remove(kind);
        if (kind == RouterKind.FritzBox)
        {
            _fritzLink = null;
        }

        _backoff.Remove(kind);
        PollNow();
    }

    /// <summary>Polling runs while a holder exists — the open popover or the Router page.</summary>
    internal IDisposable Acquire()
    {
        _demand++;
        _backoff.Clear();
        PollNow();
        return new Lease(this);
    }

    private void PollNow()
    {
        _lastPoll = DateTime.MinValue;
        if (_demand > 0)
        {
            Poll();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_demand > 0 && !_monitor.Quiet && DateTime.UtcNow - _lastPoll >= PollInterval)
        {
            Poll();
        }
    }

    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.FritzBoxEnabled) or nameof(AppSettings.UniFiEnabled) or nameof(AppSettings.OpenWrtEnabled)
                or nameof(AppSettings.OpnSenseEnabled) or nameof(AppSettings.UniFiUseApiKey):
                foreach (var kind in Enum.GetValues<RouterKind>().Where(k => !IsEnabled(k)))
                {
                    Clear(kind);
                }

                PollNow();
                break;
        }
    }

    private void Clear(RouterKind kind)
    {
        _lastSample.Remove(kind);
        _backoff.Remove(kind);
        if (kind == RouterKind.FritzBox)
        {
            _fritzLink = null;
            _fritzFailures = 0;
        }

        if (_states.Remove(kind))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Poll()
    {
        _lastPoll = DateTime.UtcNow;
        foreach (var kind in Enum.GetValues<RouterKind>())
        {
            if (!IsEnabled(kind) || _inFlight.Contains(kind) ||
                (_backoff.TryGetValue(kind, out var backoff) && backoff.Next > DateTime.UtcNow))
            {
                continue;
            }

            _ = PollAsync(kind);
        }
    }

    private async Task PollAsync(RouterKind kind)
    {
        _inFlight.Add(kind);
        var host = Host(kind);
        var usesAutoHost = TypedHost(kind).Length == 0;
        try
        {
            var bandwidth = await FetchAsync(kind, host);
            if (!IsEnabled(kind))
            {
                return;
            }

            _backoff.Remove(kind);
            if (kind == RouterKind.FritzBox)
            {
                _fritzFailures = 0;
            }

            // Counter-only routers need two samples before there is a rate to show.
            Set(kind, new RouterState(bandwidth ?? State(kind).Bandwidth, null));
        }
        catch (Exception e) when (e is RouterException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            if (!IsEnabled(kind))
            {
                return;
            }

            var failures = _backoff.GetValueOrDefault(kind).Failures + 1;
            var delay = TimeSpan.FromSeconds(Math.Min(60, PollInterval.TotalSeconds * Math.Pow(2, failures)));
            _backoff[kind] = (DateTime.UtcNow + delay, failures);
            _lastSample.Remove(kind);

            var message = RouterDiagnosis.Describe(kind, e, host, usesAutoHost, Pins);
            if (kind == RouterKind.FritzBox)
            {
                // A Fritz!Box keeps its last reading until it has failed a few times in a row.
                _fritzFailures++;
                var current = State(kind);
                Set(kind, current.Bandwidth is null || _fritzFailures >= FritzBoxFailureThreshold
                    ? new RouterState(_fritzFailures >= FritzBoxFailureThreshold ? null : current.Bandwidth, message)
                    : current);
            }
            else
            {
                Set(kind, new RouterState(null, message));
            }
        }
        finally
        {
            _inFlight.Remove(kind);
        }
    }

    private async Task<RouterBandwidth?> FetchAsync(RouterKind kind, string host)
    {
        switch (kind)
        {
            case RouterKind.FritzBox:
            {
                if (_fritzLink is null)
                {
                    try
                    {
                        _fritzLink = await FritzBoxClient.FetchLinkAsync(_plainHttp, host);
                    }
                    catch (Exception e) when (e is RouterException or HttpRequestException or TaskCanceledException)
                    {
                        // The maxima are a nicety; the rates below are what matters.
                    }
                }

                var rates = await FritzBoxClient.FetchAsync(_plainHttp, host);
                return rates with { MaxDownBits = _fritzLink?.Down ?? 0, MaxUpBits = _fritzLink?.Up ?? 0 };
            }

            case RouterKind.UniFi when _store.Settings.UniFiUseApiKey:
            {
                var key = CredentialStore.Load(ApiKeyService, Account(host)) ?? throw new RouterException(RouterFailure.MissingCredentials, "apikey");
                return await _uniFi.FetchWithKeyAsync(host, key.Second);
            }

            case RouterKind.UniFi:
            {
                var login = CredentialStore.Load(kind.CredentialService(), Account(host)) ?? throw new RouterException(RouterFailure.MissingCredentials);
                return await _uniFi.FetchAsync(host, login.First, login.Second);
            }

            default:
            {
                var credentials = CredentialStore.Load(kind.CredentialService(), Account(host)) ?? throw new RouterException(RouterFailure.MissingCredentials);
                var sample = kind == RouterKind.OpenWrt
                    ? await _openWrt.SampleAsync(host, credentials.First, credentials.Second)
                    : await _opnSense.SampleAsync(host, credentials.First, credentials.Second);

                RouterBandwidth? rates = null;
                if (_lastSample.TryGetValue(kind, out var previous) && previous.Host == host)
                {
                    rates = RouterCounterSample.Rates(previous.Sample, sample);
                }

                _lastSample[kind] = (host, sample);
                return rates;
            }
        }
    }

    private void Set(RouterKind kind, RouterState state)
    {
        if (State(kind) == state)
        {
            return;
        }

        _states[kind] = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The router reading the Dashboard meter style uses instead of this PC's own traffic,
    /// in the macOS order of preference: Fritz!Box, UniFi, then OpenWRT.
    /// </summary>
    internal (RouterBandwidth Bandwidth, string Key)? DashboardSource()
    {
        foreach (var kind in new[] { RouterKind.FritzBox, RouterKind.UniFi, RouterKind.OpenWrt })
        {
            if (IsEnabled(kind) && State(kind).Bandwidth is { } bandwidth)
            {
                return (bandwidth, kind.CredentialService());
            }
        }

        return null;
    }

    public void Dispose()
    {
        _monitor.Ticked -= OnTick;
        _store.Settings.PropertyChanged -= OnSettingChanged;
        _http.Dispose();
        _plainHttp.Dispose();
    }

    private sealed class Lease(RouterService owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                owner._demand = Math.Max(0, owner._demand - 1);
            }
        }
    }
}
