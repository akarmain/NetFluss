// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetFluss.App;

/// <summary>
/// Notices when the previous session ended without NetFluss closing itself, and keeps what
/// Windows recorded about it. <see cref="CrashLog"/> sees managed exceptions, but a fault in
/// native code — or an exception on a thread nothing catches on — ends the process before
/// anything of NetFluss's can write a line; Windows still logs the crash ("Application
/// Error", ".NET Runtime") in the Application event log. So a marker is written at start and
/// removed on a clean exit; finding it at the next start means the last run did not end
/// cleanly, and the matching records go into errors.log, which Copy Diagnostics includes.
/// </summary>
internal static class SessionGuard
{
    private static readonly string MarkerPath = Path.Combine(Path.GetDirectoryName(CrashLog.LogPath)!, "session.marker");

    /// <summary>
    /// Starts this session. Returns when the previous one began if it did not end cleanly,
    /// otherwise null.
    /// </summary>
    internal static DateTimeOffset? Begin()
    {
        DateTimeOffset? unclean = null;
        try
        {
            if (File.Exists(MarkerPath) &&
                DateTimeOffset.TryParse(File.ReadAllText(MarkerPath).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started))
            {
                unclean = started;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return unclean;
    }

    /// <summary>A clean end: NetFluss quit, updated itself, or Windows ended the session.</summary>
    internal static void End()
    {
        try
        {
            File.Delete(MarkerPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Writes what Windows recorded about NetFluss crashing since <paramref name="since"/> to
    /// errors.log. True when there was a crash record — not merely an unclean end, which a
    /// power cut or Task Manager also leaves.
    /// </summary>
    internal static bool RecordPreviousCrash(DateTimeOffset since)
    {
        var records = CrashRecords(since, out var unreadable);
        var report = new StringBuilder()
            .Append("The previous session, started ")
            .Append(since.ToString("u", CultureInfo.InvariantCulture))
            .AppendLine(records.Count > 0
                ? ", ended in a crash. Windows recorded:"
                : ", ended without NetFluss closing it, and Windows recorded no crash (ended from outside, power loss or a hang).");
        foreach (var record in records)
        {
            report.AppendLine().AppendLine(record);
        }

        if (unreadable is not null)
        {
            report.AppendLine().AppendLine("(The Application event log could not be read: " + unreadable + ")");
        }

        CrashLog.Write("previous session", new PreviousSessionEnded(report.ToString()));
        return records.Count > 0;
    }

    private static List<string> CrashRecords(DateTimeOffset since, out string? unreadable)
    {
        unreadable = null;
        var records = new List<string>();
        try
        {
            // Event times are UTC in the XPath query; a minute's slack for clock rounding.
            var from = since.UtcDateTime.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var query = new EventLogQuery(
                "Application",
                PathType.LogName,
                "*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime'])" +
                $" and TimeCreated[@SystemTime>='{from}']]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            for (var entry = reader.ReadEvent(); entry is not null && records.Count < 4; entry = reader.ReadEvent())
            {
                using (entry)
                {
                    string? text;
                    try
                    {
                        text = entry.FormatDescription();
                    }
                    catch (EventLogException)
                    {
                        continue;
                    }

                    // Every crash on the machine is in this log; only NetFluss's own are ours to keep.
                    if (text is not null && text.Contains("NetFluss.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        records.Add($"{entry.TimeCreated?.ToUniversalTime():u} {entry.ProviderName} {entry.Id}{Environment.NewLine}{text.Trim()}");
                    }
                }
            }
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            unreadable = e.Message;
        }

        return records;
    }

    /// <summary>Carries the report into <see cref="CrashLog"/>, which logs exceptions.</summary>
    private sealed class PreviousSessionEnded(string message) : Exception(message)
    {
        public override string ToString() => Message;
    }
}
