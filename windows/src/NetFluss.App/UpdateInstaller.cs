// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32;
using NetFluss.Core;

namespace NetFluss.App;

/// <summary>
/// Installs an update in place — the Windows counterpart of the macOS Sparkle 2 updates:
/// download this architecture's setup from the GitHub release, check it against the
/// release's SHA256SUMS.txt, run it silently, and let it relaunch NetFluss.
///
/// <para>The checksum is not optional. Without a listed hash that matches the bytes, the
/// update is refused and the user is pointed at the release page instead — an installer
/// that runs unattended must be exactly the one that was published.</para>
/// </summary>
internal static class UpdateInstaller
{
    /// <summary>Matches AppId in windows/Packaging/NetFluss.iss.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F3C9A41-2E7B-4D58-9C1A-5B0E8D2F7A13}_is1";

    /// <summary>
    /// Whether this copy came from the installer, so a setup can replace it. A portable copy
    /// (the zip) or a development build is left alone and offered the download instead.
    /// </summary>
    internal static bool IsInstalledCopy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") is string location &&
                   string.Equals(Path.GetFullPath(location).TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool CanInstall(AvailableUpdate update) => update.Installer is not null && update.Checksums is not null && IsInstalledCopy();

    /// <summary>
    /// Downloads, verifies and starts the setup. Null once the setup is running (the caller
    /// then quits); otherwise the reason it was not.
    /// </summary>
    internal static async Task<string?> InstallAsync(AvailableUpdate update, IProgress<double> progress, CancellationToken cancellation)
    {
        if (update.Installer is not { } installer || update.Checksums is not { } checksums)
        {
            return Localization.L("This release has no installer for this PC.");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFluss-Windows/" + UpdateNotifier.CurrentVersion);

        try
        {
            var fileName = Path.GetFileName(installer.AbsolutePath);
            var sums = await http.GetStringAsync(checksums, cancellation);
            if (UpdateLookup.ExpectedHash(sums, fileName) is not { } expected)
            {
                return Localization.L("The update could not be verified, so it was not installed.");
            }

            var folder = Path.Combine(Path.GetTempPath(), "NetFluss-Update-" + update.Version);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, fileName);

            using (var response = await http.GetAsync(installer, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(cancellation);
                await using var target = File.Create(path);
                var buffer = new byte[81920];
                long read = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    read += count;
                    if (total > 0)
                    {
                        progress.Report((double)read / total);
                    }
                }
            }

            string actual;
            await using (var stream = File.OpenRead(path))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation));
            }

            if (actual != expected)
            {
                File.Delete(path);
                return Localization.L("The update could not be verified, so it was not installed.");
            }

            // Silent, no reboot, and /LAUNCH asks the setup to start NetFluss again.
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LAUNCH",
                UseShellExecute = false,
            })?.Dispose();
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TaskCanceledException)
        {
            return Localization.L("The update could not be installed: {0}", e.Message);
        }
    }
}
