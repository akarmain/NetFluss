// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.IO;
using System.Text;
using NetFluss.Native;

namespace NetFluss.App.Vpn;

/// <summary>
/// The VPN diagnostics log — the macOS <c>VPNDiagnosticsLog</c>: which tools are installed,
/// what was asked of them, and their full output, so a failed connection can be reported
/// in one paste. Kept in <c>%LOCALAPPDATA%\NetFluss\logs\vpn.log</c>, capped at 256 KB.
/// </summary>
internal static class VpnDiagnosticsLog
{
    private const long MaximumBytes = 256 * 1024;
    private static readonly object Gate = new();

    internal static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetFluss", "logs", "vpn.log");

    internal static void Log(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaximumBytes)
                {
                    // Keep the newer half, so the failure that prompted the report survives.
                    var text = File.ReadAllText(FilePath);
                    File.WriteAllText(FilePath, text[(text.Length / 2)..]);
                }

                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                File.AppendAllText(FilePath, $"{stamp}  {line}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static void LogBlock(string title, string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd().Split('\n');
        Log($"--- {title} ({lines.Length} lines) ---{Environment.NewLine}{string.Join(Environment.NewLine, lines.Select(l => "    " + l))}");
    }

    /// <summary>The environment a VPN report needs: Windows build, tools, helper.</summary>
    internal static void LogEnvironment(bool helperConnected, string? helperVersion)
    {
        Log($"Windows {Environment.OSVersion.Version} {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}, " +
            $"NetFluss {UpdateNotifier.CurrentVersion}, helper {(helperConnected ? helperVersion ?? "?" : "not connected")}");
        Log($"OpenVPN: {VpnTools.OpenVpnPath() ?? "not installed"}; WireGuard: {VpnTools.WireGuardPath() ?? "not installed"}; " +
            $"Windows VPN connections: {RasVpn.Entries().Count}");
    }

    internal static string Read()
    {
        lock (Gate)
        {
            try
            {
                return File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            try
            {
                File.Delete(FilePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Where the installed VPN tools are, read the same way the helper finds them.</summary>
internal static class VpnTools
{
    internal static string? OpenVpnPath()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OpenVPN");
        if (key?.GetValue("exe_path") is string exe && File.Exists(exe))
        {
            return exe;
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin", "openvpn.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    internal static string? WireGuardPath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe");
        return File.Exists(path) ? path : null;
    }
}
