// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.Windows.Threading;
using NetFluss.Core;

namespace NetFluss.App;

/// <summary>
/// The optional once-a-day check for a newer Windows release — port of the macOS
/// <c>UpdateNotifier</c>. A user is told about a given version once, in a notification;
/// after that it is only mentioned quietly in the popover footer until they update.
/// </summary>
internal sealed class UpdateNotifier
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// The first check waits for the network to settle after sign-in: a check fired the
    /// moment the app starts at login usually fails, and then waits a whole day to retry.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    private readonly SettingsStore _store;
    private readonly DispatcherTimer _timer;
    private bool _checking;

    internal UpdateNotifier(SettingsStore store)
    {
        _store = store;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = StartupDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(1);
            await CheckIfDueAsync();
        };
    }

    /// <summary>Raised when a check found a newer release than the one running.</summary>
    internal event EventHandler<AvailableUpdate>? UpdateFound;

    internal AvailableUpdate? Available { get; private set; }

    internal static string CurrentVersion => HelperClient.AppVersion;

    /// <summary>
    /// The test channel: with NETFLUSS_UPDATE_PRERELEASES=1, pre-releases are offered too.
    /// They still need a valid signature, so this widens what is offered, never what is trusted.
    /// </summary>
    private static bool IncludePrereleases => Environment.GetEnvironmentVariable("NETFLUSS_UPDATE_PRERELEASES") == "1";

    internal void Start() => _timer.Start();

    /// <summary>The About window's button: checks now, regardless of the schedule.</summary>
    internal async Task<AvailableUpdate?> CheckNowAsync()
    {
        var update = await UpdateLookup.FetchAsync(CurrentVersion, IncludePrereleases);
        _store.Batch(settings => settings.LastUpdateCheck = DateTimeOffset.Now);
        Available = update;
        return update;
    }

    private async Task CheckIfDueAsync()
    {
        var settings = _store.Settings;
        if (_checking || !settings.AutomaticUpdateChecks)
        {
            return;
        }

        if (settings.LastUpdateCheck is { } last && DateTimeOffset.Now - last < CheckInterval)
        {
            return;
        }

        _checking = true;
        try
        {
            var update = await CheckNowAsync();
            if (update is null)
            {
                return;
            }

            // Once per version: a reminder every day would make an optional check feel
            // like nagging, which is the fastest way to get it switched off.
            if (update.Version != _store.Settings.LastNotifiedVersion)
            {
                _store.Batch(s => s.LastNotifiedVersion = update.Version);
                UpdateFound?.Invoke(this, update);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Offline, rate-limited or GitHub down: try again in an hour, say nothing.
        }
        finally
        {
            _checking = false;
        }
    }

    internal static void Open(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
