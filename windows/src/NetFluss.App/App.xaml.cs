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
    private StatisticsService? _statistics;
    private StatisticsWindow? _statisticsWindow;
    private AboutWindow? _aboutWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private UpdateNotifier? _updates;
    private readonly TrafficTimer _timer = new();
    private AppCommands? _commands;
    private TrayIconHost? _tray;
    private PopoverWindow? _popover;
    private PreferencesWindow? _preferences;
    private TaskbarOverlayWindow? _overlay;
    private FloatingWidgetWindow? _widget;
    private SpeedTestWindow? _speedTest;
    private SpeedTestHistory? _speedTestHistory;
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
        _statistics = new StatisticsService(_store, _monitor);
        _updates = new UpdateNotifier(_store);
        _updates.UpdateFound += OnUpdateFound;

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
        _tray.NotificationClicked += (_, _) => ShowAbout();

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
            else if (args.PropertyName is nameof(NetworkMonitorService.Addresses) or nameof(NetworkMonitorService.PublicAddress))
            {
                PushAccessories();
            }
        };

        // The Traffic Timer counts whether or not the popover is open, and comes back paused
        // after a restart: traffic while NetFluss was not running cannot be counted.
        _timer.Restore(_store.Settings.TrafficTimerSession);
        _monitor.Ticked += (_, _) => _timer.Ingest(_monitor.LastSample, _store.Settings.ExcludeTunnelAdapters);
        var lastTimerState = _timer.State;
        _timer.Changed += (_, _) =>
        {
            if (_timer.State != lastTimerState)
            {
                lastTimerState = _timer.State;
                SaveTimer();
            }
        };

        ApplySettings();
        _monitor.Start();

        _instance.Listen(args => Dispatcher.BeginInvoke(() => HandleCommand(args)));

        // The helper is optional; connecting quietly in the background means a popover
        // opened later already knows whether per-app traffic is available.
        _helper.EnsureConnecting();
        _updates.Start();

        // Sign-out, shutdown and sleep must not lose the last few minutes of history or a
        // running timer: OnExit is not guaranteed to run when Windows ends the session, and
        // a machine that never wakes from sleep never exits at all.
        SessionEnding += (_, _) => PersistState();
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, args) =>
        {
            // Raised on a SystemEvents thread; the settings write fans out to WPF objects.
            if (args.Mode == Microsoft.Win32.PowerModes.Suspend)
            {
                Dispatcher.Invoke(PersistState);
            }
        };

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
            case "--timer" when args.Length > 1:
                switch (args[1].ToLowerInvariant())
                {
                    case "start":
                        _timer.Start();
                        break;
                    case "pause":
                        _timer.Pause();
                        break;
                    case "reset":
                        _timer.Reset();
                        break;
                }

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
            var preferences = new PreferencesWindow(PreferencesContext());
            var colon = target.IndexOf(':');
            if (colon > 0)
            {
                preferences.SelectTab(target[(colon + 1)..]);
            }

            window = preferences;
        }
        else if (target == "about" && _store is not null && _updates is not null)
        {
            var (surface, download, upload) = Palette();
            window = new AboutWindow(_updates, surface, download, upload);
        }
        else if (target.StartsWith("speedtest", StringComparison.Ordinal) && _store is not null)
        {
            // "speedtest:result|running|consent|error|history|note" previews a state with
            // sample numbers and a throwaway history, so snapshots never touch the real one.
            var (surface, download, upload) = Palette();
            var speedTest = new SpeedTestWindow(_store, new SpeedTestHistory(null), surface, download, upload);
            if (target.Split(':') is [_, var state])
            {
                speedTest.Preview(state);
            }

            window = speedTest;
        }
        else if (target.StartsWith("statistics", StringComparison.Ordinal) && _store is not null && _statistics is not null)
        {
            // "statistics:demo:30d" previews the generated year at a given range.
            var parts = target.Split(':');
            _statistics.SetDemo(parts.Contains("demo"));
            var settings = _store.Settings;
            var (systemDownload, systemUpload) = SystemTheme.DefaultInk();
            var (download, upload) = settings.ResolveRateColors(systemDownload, systemUpload);
            var statisticsWindow = new StatisticsWindow(_statistics, _store, settings.Theme.Surface(SystemTheme.IsAppLight()), download, upload, () => { });
            foreach (var range in Enum.GetValues<StatisticsRange>())
            {
                if (parts.Contains(range.Code().ToLowerInvariant()))
                {
                    statisticsWindow.InitialRange = range;
                }
            }

            window = statisticsWindow;
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
        _statistics?.SetDemo(false);
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

    private void SaveTimer() => _store?.Batch(settings => settings.TrafficTimerSession = _timer.Save());

    /// <summary>
    /// The VPN mark and exit country after the rates on the taskbar meter and the widget.
    /// Each draws its dimmed state in its own secondary ink: the taskbar's, or the theme's.
    /// </summary>
    private void PushAccessories()
    {
        if (_store is null || _monitor is null)
        {
            return;
        }

        var settings = _store.Settings;
        var vpn = _monitor.Addresses.IsVpnActive;
        var country = _monitor.PublicAddress?.CountryCode;

        if (_overlay is not null)
        {
            var taskbarIdle = SystemTheme.IsShellLight() ? ThemeColor.FromHex("5D5D5D") : ThemeColor.FromHex("C5C5C5");
            _overlay.SetAccessories(MeterAccessories.From(settings, vpn, country, taskbarIdle), settings.ReadoutFontSize);
        }

        if (_widget is not null)
        {
            var surface = settings.Theme.Surface(SystemTheme.IsAppLight());
            _widget.SetAccessories(MeterAccessories.From(settings, vpn, country, surface.TextSecondary));
        }

        _tray?.SetVpnStatus(settings.VpnIndicator != "off" ? (vpn, country) : null);
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
        _monitor.DetectVpn = settings.NeedsVpnDetection;
        _monitor.MeterShowsCountry = settings.ShowCountryFlag;

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
        PushAccessories();
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

    /// <summary>The themed palette for a window opened now: surface, then the two rate inks.</summary>
    private (SurfacePalette Surface, ThemeColor Download, ThemeColor Upload) Palette()
    {
        var settings = _store!.Settings;
        var (systemDownload, systemUpload) = SystemTheme.DefaultInk();
        var (download, upload) = settings.ResolveRateColors(systemDownload, systemUpload);
        return (settings.Theme.Surface(SystemTheme.IsAppLight()), download, upload);
    }

    /// <summary>
    /// Opens one of the app's windows, or brings the open one forward — every window is a
    /// singleton, as on macOS, where choosing a menu item twice never stacks two copies.
    /// </summary>
    private void Open<T>(Func<T?> current, Action<T?> remember, Func<T> create)
        where T : Window
    {
        if (current() is { IsLoaded: true } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var window = create();
        AppIcon.Apply(window);
        window.Closed += (_, _) => remember(null);
        remember(window);
        window.Show();
        window.Activate();
    }

    private void ShowSpeedTest()
    {
        if (_store is not null)
        {
            Open(() => _speedTest, w => _speedTest = w, () =>
            {
                var (surface, download, upload) = Palette();
                _speedTestHistory ??= new SpeedTestHistory(SpeedTestHistory.DefaultPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
                return new SpeedTestWindow(_store, _speedTestHistory, surface, download, upload);
            });
        }
    }

    private void ShowStatistics()
    {
        if (_store is null || _statistics is null)
        {
            return;
        }

        Open(() => _statisticsWindow, w => _statisticsWindow = w, () =>
        {
            var (surface, download, upload) = Palette();
            return new StatisticsWindow(_statistics, _store, surface, download, upload, () => ShowPreferences("statistics"));
        });
    }

    private void ShowNetworkSlice()
    {
        // Lands with the Network Slice window.
    }

    private void ShowAbout()
    {
        if (_store is null || _updates is null)
        {
            return;
        }

        Open(() => _aboutWindow, w => _aboutWindow = w, () =>
        {
            var (surface, download, upload) = Palette();
            return new AboutWindow(_updates, surface, download, upload);
        });
    }

    private void CopyDiagnostics()
    {
        if (_store is null || _monitor is null || _helper is null)
        {
            return;
        }

        Open(() => _diagnosticsWindow, w => _diagnosticsWindow = w, () =>
        {
            var (surface, download, upload) = Palette();
            return new DiagnosticsWindow(_monitor, _helper, _store, surface, download, upload);
        });
    }

    private PreferencesContext PreferencesContext() => new()
    {
        Store = _store!,
        Monitor = _monitor!,
        Helper = _helper!,
        Privileged = _privileged!,
        Statistics = _statistics!,
        Traffic = _traffic!,
        Commands = _commands!,
    };

    private void ShowPreferences(string? tab = null)
    {
        if (_store is null)
        {
            return;
        }

        Open(() => _preferences, w => _preferences = w, () => new PreferencesWindow(PreferencesContext()));
        if (tab is not null)
        {
            _preferences?.SelectTab(tab);
        }
    }

    /// <summary>
    /// A newer release was found: one notification from the tray, and a quiet line in the
    /// popover footer for as long as it stays relevant.
    /// </summary>
    private void OnUpdateFound(object? sender, AvailableUpdate update)
    {
        var message = Localization.L("NetFluss {0} is available!", update.Version);
        _tray?.Notify("NetFluss", message);
        _popover?.SetFooterStatus(message);
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
                Statistics = _statistics!,
                Timer = _timer,
                Commands = _commands!,
            });

            _popover.Hidden += (_, _) => _popoverHiddenAt = DateTime.UtcNow;
            if (_updates?.Available is { } update)
            {
                _popover.SetFooterStatus(Localization.L("NetFluss {0} is available!", update.Version));
            }

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

    /// <summary>Writes everything that lives in memory between periodic saves.</summary>
    private void PersistState()
    {
        SaveTimer();
        _statistics?.Flush();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _overlay?.Stop();
        _overlay?.Close();
        _widget?.Close();
        _tray?.Dispose();
        _traffic?.Dispose();
        SaveTimer();
        _statistics?.Dispose();
        _helper?.Dispose();
        _monitor?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
