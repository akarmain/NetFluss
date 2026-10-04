// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace NetFluss.App;

/// <summary>
/// Last-resort error handling. A notification-area app has no window to report a failure
/// in, so an unhandled exception would otherwise end it silently — the meter just vanishes
/// from the taskbar and the user is left guessing. Errors are written to
/// <c>%LOCALAPPDATA%\NetFluss\logs\errors.log</c>, which "Copy Network Diagnostics"
/// includes, and UI-thread errors are survived rather than fatal wherever WPF allows it.
/// </summary>
internal static class CrashLog
{
    private const long MaximumSize = 512 * 1024;

    internal static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetFluss",
        "logs",
        "errors.log");

    internal static void Install(Application application)
    {
        application.DispatcherUnhandledException += (_, e) =>
        {
            Write("UI", e.Exception);

            // Keep running: a broken section of one window is far better than no meter.
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                Write(e.IsTerminating ? "fatal" : "background", exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("task", e.Exception);
            e.SetObserved();
        };
    }

    internal static void Write(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

            // Rotate rather than grow without bound: a fault that repeats every tick would
            // otherwise fill the disk with copies of itself.
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaximumSize)
            {
                File.Move(LogPath, LogPath + ".1", overwrite: true);
            }

            var entry = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("u"))
                .Append(" [").Append(context).Append("] ")
                .AppendLine(exception.ToString())
                .AppendLine();

            File.AppendAllText(LogPath, entry.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to report to.
        }
    }

    /// <summary>The most recent entries, for the diagnostics report.</summary>
    internal static string Tail(int maxChars = 8000)
    {
        try
        {
            if (!File.Exists(LogPath))
            {
                return string.Empty;
            }

            var text = File.ReadAllText(LogPath);
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
