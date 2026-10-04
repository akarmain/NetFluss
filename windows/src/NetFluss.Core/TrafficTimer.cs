// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;

namespace NetFluss.Core;

public enum TrafficTimerState
{
    Idle,
    Running,
    Paused,
}

/// <summary>A timer session as saved in the settings, so it survives a restart.</summary>
public sealed record TrafficTimerSession(
    TrafficTimerState State,
    ulong DownloadBytes,
    ulong UploadBytes,
    double ElapsedSeconds,
    DateTimeOffset? StartedAt,
    DateTimeOffset? PausedAt);

/// <summary>
/// A stopwatch for traffic — port of the macOS 2.6 <c>TrafficTimer</c>. Once started it adds
/// up the download and upload of every adapter that counts toward totals (the Data Usage
/// rule), until paused or reset. It lives outside the popover so it keeps measuring while
/// the popover is closed.
/// </summary>
public sealed class TrafficTimer
{
    private readonly Dictionary<string, (ulong Rx, ulong Tx)> _previous = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    public TrafficTimer(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.Now);

    /// <summary>Raised when the state or the counted bytes change.</summary>
    public event EventHandler? Changed;

    public TrafficTimerState State { get; private set; } = TrafficTimerState.Idle;

    public ulong DownloadBytes { get; private set; }

    public ulong UploadBytes { get; private set; }

    public ulong TotalBytes => DownloadBytes + UploadBytes;

    /// <summary>When the session was first started — kept across pause and resume.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>When the session was last paused; null while running or idle.</summary>
    public DateTimeOffset? PausedAt { get; private set; }

    /// <summary>Time banked by earlier running stretches, not counting the current one.</summary>
    private TimeSpan _accumulated;

    private DateTimeOffset? _runningSince;

    public TimeSpan Elapsed(DateTimeOffset? now = null)
    {
        var at = now ?? _clock();
        return _accumulated + (_runningSince is { } since && at > since ? at - since : TimeSpan.Zero);
    }

    public void Toggle()
    {
        if (State == TrafficTimerState.Running)
        {
            Pause();
        }
        else
        {
            Start();
        }
    }

    public void Start()
    {
        if (State == TrafficTimerState.Running)
        {
            return;
        }

        // Re-baseline, so traffic that flowed while paused is not counted.
        _previous.Clear();
        var now = _clock();
        if (State == TrafficTimerState.Idle)
        {
            StartedAt = now;
        }

        PausedAt = null;
        _runningSince = now;
        State = TrafficTimerState.Running;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (State != TrafficTimerState.Running)
        {
            return;
        }

        var now = _clock();
        _accumulated = Elapsed(now);
        _runningSince = null;
        PausedAt = now;
        State = TrafficTimerState.Paused;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        State = TrafficTimerState.Idle;
        DownloadBytes = 0;
        UploadBytes = 0;
        _accumulated = TimeSpan.Zero;
        _runningSince = null;
        StartedAt = null;
        PausedAt = null;
        _previous.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds the traffic since the previous sample. A new adapter, or one whose counters went
    /// backwards, only sets a baseline — the same rule statistics use.
    /// </summary>
    public void Ingest(IEnumerable<AdapterStatus> adapters, bool excludeTunnels)
    {
        if (State != TrafficTimerState.Running)
        {
            return;
        }

        ulong download = 0;
        ulong upload = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in adapters)
        {
            if (!AdapterClassifier.CountsTowardTotals(adapter, excludeTunnels))
            {
                continue;
            }

            seen.Add(adapter.Id);
            if (_previous.TryGetValue(adapter.Id, out var previous))
            {
                if (adapter.RxBytes >= previous.Rx)
                {
                    download += adapter.RxBytes - previous.Rx;
                }

                if (adapter.TxBytes >= previous.Tx)
                {
                    upload += adapter.TxBytes - previous.Tx;
                }
            }

            _previous[adapter.Id] = (adapter.RxBytes, adapter.TxBytes);
        }

        foreach (var gone in _previous.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _previous.Remove(gone);
        }

        if (download > 0 || upload > 0)
        {
            DownloadBytes += download;
            UploadBytes += upload;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The session to save. A running one is saved as paused now: traffic while NetFluss is
    /// not running cannot be counted, so on the next launch it resumes only when asked.
    /// </summary>
    public TrafficTimerSession? Save()
    {
        if (State == TrafficTimerState.Idle)
        {
            return null;
        }

        var now = _clock();
        return new TrafficTimerSession(
            State,
            DownloadBytes,
            UploadBytes,
            Elapsed(now).TotalSeconds,
            StartedAt,
            State == TrafficTimerState.Running ? now : PausedAt);
    }

    public void Restore(TrafficTimerSession? session)
    {
        if (session is null || session.State == TrafficTimerState.Idle)
        {
            return;
        }

        State = TrafficTimerState.Paused;
        DownloadBytes = session.DownloadBytes;
        UploadBytes = session.UploadBytes;
        _accumulated = TimeSpan.FromSeconds(Math.Max(0, session.ElapsedSeconds));
        _runningSince = null;
        StartedAt = session.StartedAt;
        PausedAt = session.PausedAt;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"04:07" under an hour, "1:04:07" beyond — a phone stopwatch without hundredths.</summary>
    public static string ClockText(TimeSpan elapsed)
    {
        var total = (long)Math.Floor(elapsed.TotalSeconds + 0.02);
        var hours = total / 3600;
        var minutes = (total % 3600) / 60;
        var seconds = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{seconds:00}");
    }
}
