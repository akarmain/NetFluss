// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Interop;
using NetFluss.App.Popover;
using NetFluss.Core;
using NetFluss.Tray;

namespace NetFluss.App;

/// <summary>
/// Entry point. Mirrors the macOS <c>AppDelegate</c> → <c>AppState</c> →
/// <c>NetworkMonitor</c> + <c>StatusBarController</c> wiring order.
///
/// <para>The class is not called <c>App</c> on purpose: a type named <c>App</c> inside a
/// namespace ending in <c>.App</c> resolves ambiguously in generated XAML partials.</para>
/// </summary>
public partial class NetFlussApplication : Application
{
    /// <summary>
    /// Clicking the tray icon while the popover is open deactivates it first, so by the
    /// time the click arrives the popover has already hidden itself and the toggle would
    /// immediately reopen it. Ignoring a toggle that lands right after a hide is the
    /// standard fix; NSPopover handles this for us on macOS.
    /// </summary>
    private static readonly TimeSpan ReopenSuppressionWindow = TimeSpan.FromMilliseconds(250);

    private SingleInstance? _instance;
    private SettingsStore? _store;
    private NetworkMonitorService? _monitor;
    private HelperClient? _helper;
    private TrafficService? _traffic;
    private WifiService? _wifi;
    private DnsSwitcher? _dns;
    private PrivilegedActions? _privileged;
    private AppCommands? _commands;
    private TrayIconHost? _tray;
    private PopoverWindow? _popover;
    private PreferencesWindow? _preferences;
    private TaskbarOverlayWindow? _overlay;
    private FloatingWidgetWindow? _widget;
    private SpeedTestWindow? _speedTest;
    private DateTime _popoverHiddenAt = DateTime.MinValue;
    private bool _lastIPv6;

    /// <summary>
    /// Set when the overlay was asked for but could not anchor to the taskbar, so the tray
    /// meter is standing in for it. Preferences reads this to explain itself rather than
    /// leaving the user staring at a setting that appears to do nothing.
    /// </summary>
    internal static bool OverlayFellBackToTray { get; private set; }

    internal AppCommands Commands => _commands!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashLog.Install(this);

        // One NetFluss per session. A second launch hands its arguments to the first and
        // leaves — which, with no arguments, opens the popover of the one already running.
        _instance = SingleInstance.Acquire();
        if (!_instance.IsPrimary)
        {
            _instance.Forward(e.Args);
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }

        _store = new SettingsStore(SettingsStore.DefaultPath);
        NetFluss.Core.Localization.Use(_store.Settings.Language);
        _lastIPv6 = _store.Settings.ExternalIPv6;

        _monitor = new NetworkMonitorService(TimeSpan.FromSeconds(_store.Settings.RefreshIntervalSeconds));
        _helper = new HelperClient(Dispatcher);
        _traffic = new TrafficService(_helper);
        _wifi = new WifiService(_store);
        _privileged = new PrivilegedActions(_helper);
        _dns = new DnsSwitcher(_store, _monitor, _privileged);

        _commands = new AppCommands
        {
            ShowPreferences = () => ShowPreferences(),
            ShowSpeedTest = ShowSpeedTest,
            ShowStatistics = ShowStatistics,
            ShowNetworkSlice = ShowNetworkSlice,
            ShowAbout = ShowAbout,
            CopyDiagnostics = CopyDiagnostics,
            Quit = Shutdown,
        };

        _tray = new TrayIconHost(_monitor, BuildMeterOptions(), _commands);
        _tray.LeftClicked += (_, _) => TogglePopover(Screens.CursorAnchor());

        // Preferences writes, then everything re-reads. One direction, so there is no way
        // for the tray and the settings file to disagree about what is configured.
        _store.Changed += (_, _) => ApplySettings();

        // Surfaces repaint on the same tick that drives the tray meter.
        _monitor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NetworkMonitorService.Totals))
            {
                PushTotals();
            }
        };

        ApplySettings();
        _monitor.Start();

        _instance.Listen(args => Dispatcher.BeginInvoke(() => HandleCommand(args)));

        // The helper is optional; connecting quietly in the background means a popover
        // opened later already knows whether per-app traffic is available.
        _helper.EnsureConnecting();

        HandleCommand(e.Args);
    }

    /// <summary>
    /// Command-line verbs, from this launch or forwarded from a later one. They make the
    /// app scriptable from a shortcut — "NetFluss.exe --speedtest" — and are what the
    /// verification harness drives the real UI with.
    /// </summary>
    private void HandleCommand(string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "--popover":
                ShowPopover(DefaultAnchor());
                break;
            case "--hide-popover":
                if (_popover is { IsVisible: true })
                {
                    _popover.HideAndNotify();
                }

                break;
            case "--preferences":
                ShowPreferences(args.Length > 1 ? args[1] : null);
                break;
            case "--speedtest":
                ShowSpeedTest();
                break;
            case "--statistics":
                ShowStatistics();
                break;
            case "--slice":
                ShowNetworkSlice();
                break;
            case "--about":
                ShowAbout();
                break;
            case "--quit":
                Shutdown();
                break;
            case "--snapshot" when args.Length >= 3:
                var delay = args.Length > 3 && int.TryParse(args[3], out var ms) ? ms : 3000;
                SnapshotAsync(args[1].ToLowerInvariant(), args[2], delay).ContinueWith(
                    task => CrashLog.Write("snapshot", task.Exception!),
                    TaskContinuationOptions.OnlyOnFaulted);
                break;
        }
    }

    /// <summary>
    /// Renders a window to a PNG without showing it to anyone — the verification harness.
    /// Targets: "popover", "preferences[:tab]", "speedtest".
    /// </summary>
    private async Task SnapshotAsync(string target, string path, int delayMilliseconds)
    {
        if (target == "popover")
        {
            ShowPopover(DefaultAnchor(), offScreen: true);
            if (_popover is null)
            {
                return;
            }

            await Task.Delay(delayMilliseconds);
            Snapshot.Save(_popover, path);
            _popover.HideOffScreen();
            return;
        }

        Window? window = null;
        if (target.StartsWith("preferences", StringComparison.Ordinal) && _store is not null && _monitor is not null)
        {
            var preferences = new PreferencesWindow(_store, _monitor);
            var colon = target.IndexOf(':');
            if (colon > 0)
            {
                preferences.SelectTab(target[(colon + 1)..]);
            }

            window = preferences;
        }
        else if (target == "speedtest" && _store is not null)
        {
            window = new SpeedTestWindow(_store.Settings.Theme.Surface(SystemTheme.IsAppLight()));
        }

        if (window is null)
        {
            return;
        }

        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Snapshot.OffScreen;
        window.Top = Snapshot.OffScreen;
        window.Show();
        await Task.Delay(delayMilliseconds);
        Snapshot.Save(window, path);
        window.Close();
    }

    /// <summary>Where the popover opens when nothing was clicked: the meter, or the tray corner.</summary>
    private Rect DefaultAnchor()
    {
        if (_overlay is { IsAnchored: true })
        {
            return Screens.WindowAnchor(new WindowInteropHelper(_overlay).Handle);
        }

        // Bottom-right of the primary monitor, where the notification area normally is.
        var monitor = Screens.MonitorFromPoint(0, 0);
        return monitor is { } info
            ? new Rect(info.Work.Right - 40, info.Work.Bottom - 8, 16, 8)
            : Screens.CursorAnchor();
    }

    private void PushTotals()
    {
        if (_store is null || _monitor is null)
        {
            return;
        }

        _overlay?.Update(_monitor.Totals, _store.Settings.UseBits);
        _widget?.Update(_monitor.Totals, _store.Settings.UseBits);
    }

    private TrayMeterOptions BuildMeterOptions()
    {
        var settings = _store!.Settings;
        var (systemDownload, systemUpload) = SystemTheme.DefaultInk();
        var (downloadInk, uploadInk) = settings.ResolveRateColors(systemDownload, systemUpload);

        return new TrayMeterOptions
        {
            Size = Dpi.TrayIconSize(),
            Layout = PreferencesWindow.ToLayout(settings.MeterStyle),
            DownloadColor = downloadInk,
            UploadColor = uploadInk,
            UseBits = settings.UseBits,
            ShowArrows = settings.ShowArrows,
            TaskbarBackground = SystemTheme.TaskbarBackground(),
            MinimumContrastRatio = settings.EnforceContrast ? Contrast.MinimumReadableRatio : 0,
            IconGlyph = settings.TrayIconGlyph,
        };
    }

    private void ApplySettings()
    {
        if (_store is null || _monitor is null || _tray is null)
        {
            return;
        }

        var settings = _store.Settings;
        NetFluss.Core.Localization.Use(settings.Language);

        _monitor.Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
        _monitor.ExcludeTunnelAdapters = settings.ExcludeTunnelAdapters;
        _monitor.TotalsFromVisibleAdaptersOnly = settings.TotalsFromVisibleAdaptersOnly;
        _monitor.AdapterGraceSeconds = settings.AdapterGraceEnabled ? settings.AdapterGraceSeconds : null;
        _monitor.PreferIPv6 = settings.ExternalIPv6;

        // Switching IPv4/IPv6 must show the other address now, not after the five-minute
        // cache runs out — on macOS the setting change triggers the same refetch.
        if (_lastIPv6 != settings.ExternalIPv6)
        {
            _lastIPv6 = settings.ExternalIPv6;
            _monitor.InvalidatePublicAddress();
        }

        // Adapter visibility was the same shape of bug as the theme: HiddenAdapters was
        // stored and round-trip tested, and nothing ever handed it to the monitor.
        _monitor.Visibility = settings.VisibilityOptions();
        _monitor.AdapterNames = settings.AdapterCustomNames;
        _monitor.AdapterOrder = settings.AdapterOrder;
        _monitor.Refresh();

        var (systemDownload, systemUpload) = SystemTheme.DefaultInk();
        var (download, upload) = settings.ResolveRateColors(systemDownload, systemUpload);

        // One resolved palette for every themed window, so a theme cannot reach some
        // surfaces and not others — which is how it came to be wired to none of them.
        var surface = settings.Theme.Surface(SystemTheme.IsAppLight());

        ApplyOverlay(settings, download, upload);
        ApplyWidget(settings, download, upload, surface);
        ApplyPopoverTheme();
        _popover?.ApplyLayout();

        var overlayCarriesTheMeter = _overlay is { IsAnchored: true };

        // While another surface shows the numbers, the tray icon drops to a static glyph so
        // the rates are not displayed twice — but it stays *present*, because it is where a
        // Windows user looks for a background app's menu. Hiding it is opt-in.
        _tray.Options = overlayCarriesTheMeter
            ? BuildMeterOptions() with { Layout = TrayMeterLayout.Icon }
            : BuildMeterOptions();

        _tray.Redraw();
        _tray.IsVisible = !(settings.HideTrayIcon && overlayCarriesTheMeter);

        PushTotals();
    }

    private void ApplyOverlay(AppSettings settings, ThemeColor download, ThemeColor upload)
    {
        if (settings.MeterSurface != MeterSurface.TaskbarOverlay)
        {
            _overlay?.Stop();
            _overlay?.Close();
            _overlay = null;
            OverlayFellBackToTray = false;
            return;
        }

        if (_overlay is null)
        {
            _overlay = new TaskbarOverlayWindow(_monitor!)
            {
                ContextMenu = SurfaceMenu.Build(_commands!),
            };

            _overlay.Clicked += (_, _) => TogglePopover(Screens.WindowAnchor(new WindowInteropHelper(_overlay).Handle));

            // The overlay reports rather than decides. Losing the anchor brings the tray
            // meter back immediately, so there is never a moment with no meter at all.
            _overlay.AnchorLost += (_, _) =>
            {
                OverlayFellBackToTray = true;
                if (_tray is not null)
                {
                    _tray.IsVisible = true;
                }
            };

            _overlay.Start();
        }

        _overlay.ApplySettings(settings, download, upload);
        OverlayFellBackToTray = !_overlay.IsAnchored;
    }

    /// <summary>Pushes the current theme into the popover, whenever one exists to push into.</summary>
    private void ApplyPopoverTheme()
    {
        if (_store is null || _popover is null)
        {
            return;
        }

        var settings = _store.Settings;
        var (systemDownload, systemUpload) = SystemTheme.DefaultInk();
        var (download, upload) = settings.ResolveRateColors(systemDownload, systemUpload);

        _popover.ApplyTheme(settings.Theme.Surface(SystemTheme.IsAppLight()), download, upload);
    }

    private void ApplyWidget(AppSettings settings, ThemeColor download, ThemeColor upload, SurfacePalette surface)
    {
        if (!settings.ShowFloatingWidget)
        {
            _widget?.Close();
            _widget = null;
            return;
        }

        if (_widget is null)
        {
            _widget = new FloatingWidgetWindow(settings)
            {
                ContextMenu = SurfaceMenu.Build(_commands!),
            };

            _widget.Clicked += (_, _) => TogglePopover(Screens.WindowAnchor(new WindowInteropHelper(_widget).Handle));
            _widget.Show();
            _widget.Place();
        }

        _widget.ApplySettings(settings, download, upload, surface);
    }

    /// <summary>Opens the speed test, or brings the open one forward.</summary>
    private void ShowSpeedTest()
    {
        if (_store is null)
        {
            return;
        }

        if (_speedTest is { IsLoaded: true })
        {
            _speedTest.Activate();
            return;
        }

        _speedTest = new SpeedTestWindow(_store.Settings.Theme.Surface(SystemTheme.IsAppLight()));
        _speedTest.Closed += (_, _) => _speedTest = null;
        _speedTest.Show();
        _speedTest.Activate();
    }

    private void ShowStatistics()
    {
        // Lands with the statistics store.
    }

    private void ShowNetworkSlice()
    {
        // Lands with the Network Slice window.
    }

    private void ShowAbout()
    {
        // Lands with the About window and update checker.
    }

    private void CopyDiagnostics()
    {
        // Lands with the diagnostics report.
    }

    private void ShowPreferences(string? tab = null)
    {
        if (_store is null)
        {
            return;
        }

        if (_preferences is { IsLoaded: true })
        {
            _preferences.Activate();
            return;
        }

        _preferences = new PreferencesWindow(_store, _monitor!);
        _preferences.Closed += (_, _) => _preferences = null;
        _preferences.Show();
        _preferences.Activate();
        _ = tab;
    }

    private void TogglePopover(Rect anchor)
    {
        if (_popover is { IsVisible: true })
        {
            // A pinned popover is a window, not a popover: clicking the meter brings it
            // forward rather than closing it.
            if (_popover.IsPinned)
            {
                _popover.Activate();
            }
            else
            {
                _popover.HideAndNotify();
            }

            return;
        }

        if (DateTime.UtcNow - _popoverHiddenAt < ReopenSuppressionWindow)
        {
            return;
        }

        ShowPopover(anchor);
    }

    private void ShowPopover(Rect anchor, bool offScreen = false)
    {
        if (_monitor is null || _store is null)
        {
            return;
        }

        if (_popover is null)
        {
            _popover = new PopoverWindow(new PopoverContext
            {
                Monitor = _monitor,
                Store = _store,
                Wifi = _wifi!,
                Dns = _dns!,
                Traffic = _traffic!,
                Helper = _helper!,
                Privileged = _privileged!,
                Commands = _commands!,
            });

            _popover.Hidden += (_, _) => _popoverHiddenAt = DateTime.UtcNow;

            // Themed on creation as well as on every settings change: the window is built
            // lazily on first open, so waiting for a change would show it unthemed once.
            ApplyPopoverTheme();
        }

        if (_popover.IsVisible)
        {
            if (!offScreen)
            {
                _popover.Activate();
            }

            return;
        }

        if (offScreen)
        {
            _popover.ShowOffScreen();
            return;
        }

        _popover.ShowAt(anchor);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _overlay?.Stop();
        _overlay?.Close();
        _widget?.Close();
        _tray?.Dispose();
        _traffic?.Dispose();
        _helper?.Dispose();
        _monitor?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
