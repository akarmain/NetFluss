// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.Windows.Threading;
using NetFluss.Core;
using NetFluss.Native;

namespace NetFluss.App;

/// <summary>
/// The Wi-Fi switcher behind the popover section. Port of the macOS <c>WifiManager</c>.
///
/// <para>Scans only while the section is on screen. A background scan makes the radio leave
/// its channel to listen on every other one, which costs throughput and battery; Windows
/// already scans on its own schedule and the popover is the only thing here that needs a
/// fresher list than that.</para>
/// </summary>
internal sealed class WifiService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly SettingsStore _store;
    private readonly DispatcherTimer _timer;
    private DateTime _lastScan = DateTime.MinValue;
    private IReadOnlyList<WifiNetwork> _scanned = [];
    private Guid? _radio;

    internal WifiService(SettingsStore store)
    {
        _store = store;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ReadInterval };
        _timer.Tick += (_, _) => Read(scan: DateTime.UtcNow - _lastScan >= ScanInterval);
    }

    /// <summary>Raised whenever anything the section shows has changed.</summary>
    internal event EventHandler? Changed;

    internal bool HasWifi { get; private set; } = true;

    internal WlanAccess Access { get; private set; } = WlanAccess.Ok;

    /// <summary>True until the first read completes, so the section can say "Scanning…".</summary>
    internal bool IsWaitingForFirstScan { get; private set; } = true;

    /// <summary>The list as the section shows it: current, pinned, then strongest first.</summary>
    internal IReadOnlyList<WifiNetwork> Networks
    {
        get
        {
            var settings = _store.Settings;
            var arranged = WifiFormat.Arrange(_scanned, settings.PinnedWifiNetworks);
            return WifiFormat.ApplyLimit(arranged, settings.WifiLimitEnabled, settings.WifiLimitCount);
        }
    }

    /// <summary>The SSID a join is in progress for, if any.</summary>
    internal string? ConnectingTo { get; private set; }

    internal string? LastError { get; private set; }

    internal void ClearError()
    {
        LastError = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts scanning while the section is visible; stops when it is not.</summary>
    internal void SetActive(bool active)
    {
        if (active == _timer.IsEnabled)
        {
            return;
        }

        if (active)
        {
            Read(scan: true);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    internal void TogglePin(WifiNetwork network)
    {
        _store.Batch(settings => settings.SetWifiPinned(network.Ssid, !settings.IsWifiPinned(network.Ssid)));

        // A pinned network that is out of range gets a targeted rescan, which is what the
        // Mac does when a "Not available" row is tapped.
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Joins a network. A saved network or an open one joins straight away; a new secured one
    /// needs <paramref name="password"/>, and the profile written for it is then known to the
    /// Windows Wi-Fi flyout too — the equivalent of the Mac writing to Known Networks.
    /// </summary>
    internal async Task ConnectAsync(WifiNetwork network, string? password)
    {
        if (ConnectingTo is not null || _radio is not { } radio)
        {
            return;
        }

        if (!network.IsAvailable)
        {
            // Out of range: all that can usefully happen is a fresh look for it.
            Read(scan: true);
            return;
        }

        LastError = null;
        ConnectingTo = network.Ssid;
        Changed?.Invoke(this, EventArgs.Empty);

        try
        {
            var error = await Task.Run(() => Join(radio, network, password));
            if (error is not null)
            {
                LastError = error;
                return;
            }

            // WlanConnect only queues the request; success is the radio reporting the SSID.
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < ConnectTimeout)
            {
                await Task.Delay(1000);
                if (await Task.Run(() => CurrentSsid(radio)) == network.Ssid)
                {
                    return;
                }
            }

            LastError = Localization.L("Could not join “{0}”.", network.Ssid);
        }
        finally
        {
            ConnectingTo = null;
            Read(scan: false);
        }
    }

    private static string? Join(Guid radio, WifiNetwork network, string? password)
    {
        using var client = WlanClient.TryOpen();
        if (client is null)
        {
            return Localization.L("No Wi-Fi adapter found.");
        }

        var profile = network.IsSaved ? network.ProfileName ?? FindProfile(client, radio, network.Ssid) : null;

        if (profile is null || password is not null)
        {
            var built = WifiProfile.Build(network.Ssid, network.AuthAlgorithm, network.CipherAlgorithm, password);
            if (!built.IsValid)
            {
                return Localization.L(built.Error ?? "Could not join “{0}”.", network.Ssid);
            }

            var status = client.SetProfile(radio, built.Xml!);
            if (status != 0)
            {
                return Localization.L("Windows refused the network settings (error {0}).", status);
            }

            profile = network.Ssid;
        }

        var result = client.Connect(radio, profile);
        return result == 0 ? null : Localization.L("Windows refused the network settings (error {0}).", result);
    }

    private static string? FindProfile(WlanClient client, Guid radio, string ssid)
        => client.ProfileNames(radio).FirstOrDefault(name => string.Equals(name, ssid, StringComparison.Ordinal));

    private static string? CurrentSsid(Guid radio)
    {
        using var client = WlanClient.TryOpen();
        return client?.CurrentConnection(radio, out _)?.Ssid;
    }

    private void Read(bool scan)
    {
        using var client = WlanClient.TryOpen();
        var radio = client?.Interfaces().FirstOrDefault();

        if (client is null || radio is null)
        {
            HasWifi = false;
            _radio = null;
            _scanned = [];
            IsWaitingForFirstScan = false;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        HasWifi = true;
        _radio = radio.Id;

        if (scan)
        {
            _lastScan = DateTime.UtcNow;
            if (client.RequestScan(radio.Id) == WlanAccess.LocationDenied)
            {
                Access = WlanAccess.LocationDenied;
            }
        }

        var result = client.AvailableNetworks(radio.Id);
        Access = result.Access;
        _scanned = result.Networks;

        // An empty first answer straight after the scan request is the driver not having
        // finished yet, not an empty neighbourhood; keep saying "Scanning…" for it.
        if (result.Networks.Count > 0 || !scan)
        {
            IsWaitingForFirstScan = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
