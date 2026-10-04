// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetFluss.Core;

namespace NetFluss.App;

/// <summary>
/// "Copy Network Diagnostics…" — port of the macOS <c>NetworkDiagnostics</c> capture: record
/// every adapter's counters for thirty seconds while the user makes some traffic, then put a
/// plain-text report on the clipboard for a GitHub issue. Nothing is sent anywhere.
///
/// <para>Hidden from the menu unless Shift is held, the way the Mac hides it behind Option:
/// it is a support tool, not a feature, and an ordinary menu should not carry it.</para>
/// </summary>
internal sealed class DiagnosticsWindow : Window
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

    private readonly NetworkMonitorService _monitor;
    private readonly HelperClient _helper;
    private readonly SettingsStore _store;
    private readonly List<(DateTime At, IReadOnlyList<AdapterStatus> Adapters)> _snapshots = [];
    private readonly StackPanel _body = new() { Margin = new Thickness(22) };
    private DispatcherTimer? _countdown;
    private DateTime _started;

    internal DiagnosticsWindow(NetworkMonitorService monitor, HelperClient helper, SettingsStore store, SurfacePalette surface, ThemeColor download, ThemeColor upload)
    {
        _monitor = monitor;
        _helper = helper;
        _store = store;

        Title = Localization.L("Copy Network Diagnostics…");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Ui.UiFont;
        UseLayoutRounding = true;

        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NetFluss;component/PopoverResources.xaml") });
        ThemeBrushes.Apply(Resources, surface, download, upload);
        SetResourceReference(BackgroundProperty, "PopoverBackgroundBrush");
        SourceInitialized += (_, _) => ThemeBrushes.ApplyFrame(this, surface.IsDark);

        Content = _body;
        ShowIntro();
        Closed += (_, _) => Stop();
    }

    private void ShowIntro()
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.Label(Localization.L("Record network diagnostics?"), 16, Ui.Text, FontWeights.SemiBold));
        _body.Children.Add(Ui.Wrapping(
            Localization.L("NetFluss will record per-interface byte counters for {0} seconds. After you click Start, begin a large download (for example a Speed Test) and let it run until the capture finishes. The diagnostics will then be copied to your clipboard so you can paste them into a GitHub issue.\n\nNo network traffic is sent — only local counters from your machine are recorded.", Duration.TotalSeconds),
            13).Margin(0, 10, 0, 18));

        var buttons = Ui.Row(
            Ui.TextButton(Localization.L("Cancel"), Close, accent: false),
            Ui.TextButton(Localization.L("Start"), Start, accent: true).Margin(8, 0, 0, 0));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        _body.Children.Add(buttons);
    }

    private void Start()
    {
        _snapshots.Clear();
        _started = DateTime.Now;
        _monitor.Ticked += OnTick;

        _body.Children.Clear();
        _body.Children.Add(Ui.Label(Localization.L("Recording…"), 16, Ui.Text, FontWeights.SemiBold));
        var remaining = Ui.Label(string.Empty, 13, Ui.Secondary).Margin(0, 10, 0, 18);
        _body.Children.Add(remaining);

        var cancel = Ui.TextButton(Localization.L("Cancel"), Close, accent: false);
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        _body.Children.Add(cancel);

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _countdown.Tick += (_, _) =>
        {
            var left = Duration - (DateTime.Now - _started);
            if (left <= TimeSpan.Zero)
            {
                Finish();
                return;
            }

            remaining.Text = Localization.L("{0} seconds left. Start a large download now.", Math.Ceiling(left.TotalSeconds));
        };
        _countdown.Start();
    }

    private void OnTick(object? sender, EventArgs e) => _snapshots.Add((DateTime.Now, _monitor.LastSample));

    private void Stop()
    {
        _countdown?.Stop();
        _monitor.Ticked -= OnTick;
    }

    private void Finish()
    {
        Stop();
        var copied = Ui.Copy(Report());

        _body.Children.Clear();
        _body.Children.Add(Ui.Label(Localization.L(copied ? "Diagnostics copied to clipboard" : "The clipboard is busy. Try again in a moment."), 16, Ui.Text, FontWeights.SemiBold));
        _body.Children.Add(Ui.Wrapping(
            Localization.L("{0} snapshots were captured. Paste them into a GitHub issue so the developer can see what each adapter reported.", _snapshots.Count),
            13).Margin(0, 10, 0, 18));

        var ok = Ui.TextButton(Localization.L("OK"), Close, accent: true);
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        _body.Children.Add(ok);
    }

    private string Report()
    {
        var invariant = CultureInfo.InvariantCulture;
        var lines = new StringBuilder();
        void Line(string text = "") => lines.Append(text).Append('\n');

        Line("=== NetFluss network diagnostics ===");
        Line($"Generated: {DateTimeOffset.Now.ToString("o", invariant)}");
        Line($"NetFluss:  {HelperClient.AppVersion} (Windows)");
        Line($"Windows:   {Environment.OSVersion.VersionString} ({RuntimeInformation.OSDescription.Trim()})");
        Line($"Arch:      {RuntimeInformation.OSArchitecture}");
        Line($"Helper:    {(_helper.IsConnected ? $"connected, version {_helper.HelperVersion}, trace {_helper.HelperTraceStatus ?? "idle"}" : "not connected")}");
        Line($"Window:    {(DateTime.Now - _started).TotalSeconds.ToString("0.0", invariant)} s ({_snapshots.Count} snapshots)");
        Line();

        if (_snapshots.Count == 0)
        {
            Line("(no samples were captured — is the monitor running?)");
            return lines.ToString();
        }

        var first = _snapshots[0];
        var last = _snapshots[^1];
        var span = Math.Max(0.001, (last.At - first.At).TotalSeconds);

        Line("=== Interfaces observed ===");
        foreach (var adapter in last.Adapters)
        {
            var link = adapter.LinkSpeedBps is { } bps ? $" link={(bps / 1_000_000.0).ToString("0", invariant)} Mbps" : string.Empty;
            var ssid = adapter.WifiSsid is { } s ? $" ssid=\"{s}\"" : string.Empty;
            var flags = (adapter.IsTunnel ? " tunnel" : string.Empty) + (adapter.IsNonInternet ? " mirror/loopback" : string.Empty);
            Line($"  {adapter.Id}  [{adapter.DisplayName}] \"{adapter.Description}\" type={adapter.Type} {(adapter.IsUp ? "up" : "down")}{flags}{link}{ssid}");
        }

        Line();
        Line("=== Per-interface byte deltas across window ===");
        Line($"Window: {span.ToString("0.00", invariant)} s");
        foreach (var adapter in last.Adapters.Where(a => !a.IsNonInternet))
        {
            var start = first.Adapters.FirstOrDefault(a => a.Id == adapter.Id);
            if (start is null)
            {
                continue;
            }

            var rx = adapter.RxBytes >= start.RxBytes ? adapter.RxBytes - start.RxBytes : 0;
            var tx = adapter.TxBytes >= start.TxBytes ? adapter.TxBytes - start.TxBytes : 0;
            Line($"  {adapter.DisplayName,-28} Δrx {rx,14}  Δtx {tx,14}  avg rx {(rx / span).ToString("0", invariant),12} B/s  avg tx {(tx / span).ToString("0", invariant),12} B/s");
        }

        Line();
        Line("=== Timeline (internet-carrying adapters, summed) ===");
        foreach (var (at, adapters) in _snapshots)
        {
            var rx = adapters.Where(a => !a.IsNonInternet).Sum(a => a.RxRateBps);
            var tx = adapters.Where(a => !a.IsNonInternet).Sum(a => a.TxRateBps);
            Line($"  +{(at - first.At).TotalSeconds.ToString("00.0", invariant)} s  rx {rx.ToString("0", invariant),12} B/s  tx {tx.ToString("0", invariant),12} B/s");
        }

        Line();
        Line("=== Addresses ===");
        var addresses = _monitor.Addresses;
        Line($"  internal {addresses.InternalIp ?? "—"}  router {addresses.GatewayIp ?? "—"}  tunnels {string.Join(", ", addresses.Tunnels.Select(t => $"{t.Name}={t.Address}"))}");
        if (addresses.PrimaryAdapterId is { } primary)
        {
            var dns = DnsController.StaticServers(primary);
            Line($"  primary DNS: {(dns.Count == 0 ? "automatic (DHCP)" : string.Join(", ", dns))}");
        }

        var settings = _store.Settings;
        Line();
        Line("=== Settings ===");
        Line($"  interval {settings.RefreshIntervalSeconds.ToString(invariant)} s, surface {settings.MeterSurface}, bits {settings.UseBits}, statistics {settings.CollectStatistics}, hidden adapters {settings.HiddenAdapters.Count}, exclude tunnels {settings.ExcludeTunnelAdapters}");

        var errors = CrashLog.Tail(4000);
        if (errors.Length > 0)
        {
            Line();
            Line("=== Recent errors ===");
            Line(errors.TrimEnd());
        }

        return lines.ToString();
    }
}
