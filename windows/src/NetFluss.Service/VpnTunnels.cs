// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using NetFluss.Core;
using NetFluss.Core.Vpn;

namespace NetFluss.Service;

/// <summary>
/// Runs OpenVPN and WireGuard tunnels for the app — the VPN half of the macOS privileged
/// helper. The tools are the user's installed OpenVPN Community and WireGuard for Windows;
/// both need administrator rights to create a tunnel, which is why they run here.
///
/// <para><b>Running someone else's config as SYSTEM.</b> The app sends the files' contents,
/// never paths, so the helper cannot be talked into reading a file the caller could not.
/// They are written to a folder only SYSTEM and Administrators can touch, checked there
/// (so nothing can change them between the check and the launch), and OpenVPN runs with
/// <c>--script-security 1</c>; see <see cref="VpnConfigPolicy"/> for what is refused.</para>
/// </summary>
internal sealed class VpnTunnels : IDisposable
{
    private const int MaxTotalBytes = 8 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Tunnel> _tunnels = new();

    internal Action<string>? Log { get; set; }

    private static string StagingRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetFluss", "VPN");

    internal async Task<HelperMessage> StartAsync(HelperRequest request)
    {
        var files = request.Files ?? [];
        if (files.Count == 0 || request.Config is null || files.Sum(f => (long)f.Data.Length) > MaxTotalBytes ||
            files.Any(f => !VpnConfigImporter.IsSafeRelative(f.Name)) || files.All(f => f.Name != request.Config))
        {
            return Failure("The VPN configuration could not be read.");
        }

        return request.Kind switch
        {
            "openVpn" => StartOpenVpn(files, request.Config),
            "wireGuard" => await StartWireGuardAsync(files, request.Config, request.Tunnel),
            _ => Failure("Unknown VPN kind."),
        };
    }

    // ===================================== OpenVPN =====================================

    private HelperMessage StartOpenVpn(IReadOnlyList<HelperFile> files, string config)
    {
        if (OpenVpnPath() is not { } openvpn)
        {
            return Failure("OpenVPN is not installed. Install OpenVPN Community from openvpn.net, then connect again.");
        }

        var id = Guid.NewGuid().ToString("N");
        var directory = Stage(id, files);
        var configPath = Path.Combine(directory, config.Replace('/', '\\'));

        if (VpnConfigPolicy.UnsafeOpenVpnDirective(File.ReadAllText(configPath)) is { } bad)
        {
            Cleanup(directory);
            return Failure($"This OpenVPN config was refused because it contains a '{bad}' directive, which could run code or touch files as SYSTEM. Remove it and re-import.");
        }

        var port = FreeLoopbackPort();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var secretPath = Path.Combine(directory, "management.pw");
        File.WriteAllText(secretPath, secret + "\n");
        var logPath = Path.Combine(directory, "openvpn.log");

        var info = new ProcessStartInfo
        {
            FileName = openvpn,
            WorkingDirectory = Path.GetDirectoryName(configPath)!,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Ours come after --config, so they override anything the config says.
        foreach (var argument in new[]
                 {
                     "--config", configPath,
                     "--management", "127.0.0.1", port.ToString(System.Globalization.CultureInfo.InvariantCulture), secretPath,
                     "--management-hold",
                     "--management-query-passwords",
                     "--auth-retry", "interact",
                     "--log", logPath,
                     "--verb", "3",
                     "--script-security", "1",
                 })
        {
            info.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("Could not start OpenVPN.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Cleanup(directory);
            return Failure(e.Message);
        }

        // Early option errors go to stdout before --log is open; keep them for the log.
        var captured = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(captured, e.Data);
        process.ErrorDataReceived += (_, e) => Append(captured, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.EnableRaisingEvents = true;

        var handle = "ovpn:" + id;
        var tunnel = new Tunnel(handle, directory, process, captured, logPath, null);
        _tunnels[handle] = tunnel;
        process.Exited += (_, _) =>
        {
            Log?.Invoke($"openvpn {id} exited {SafeExitCode(process)}");
            tunnel.Exited = true;
        };

        Log?.Invoke($"openvpn {id} started (port {port})");
        return new HelperMessage { Type = "result", Ok = true, Handle = handle, Port = port, Secret = secret };
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (builder)
        {
            if (builder.Length < 64 * 1024)
            {
                builder.AppendLine(line);
            }
        }
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    /// <summary>OpenVPN Community's install, from its registry key, else the default folder.</summary>
    internal static string? OpenVpnPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OpenVPN");
        if (key?.GetValue("exe_path") is string exe && File.Exists(exe))
        {
            return exe;
        }

        if (key?.GetValue(null) is string folder && File.Exists(Path.Combine(folder, "bin", "openvpn.exe")))
        {
            return Path.Combine(folder, "bin", "openvpn.exe");
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin", "openvpn.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ===================================== WireGuard =====================================

    private async Task<HelperMessage> StartWireGuardAsync(IReadOnlyList<HelperFile> files, string config, string? requestedName)
    {
        if (WireGuardPath() is not { } wireguard)
        {
            return Failure("WireGuard is not installed. Install WireGuard for Windows from wireguard.com, then connect again.");
        }

        var name = VpnConfigPolicy.WireGuardTunnelName(requestedName ?? Path.GetFileNameWithoutExtension(config));
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(StagingRoot, id);
        CreateProtectedDirectory(directory);

        // The tunnel is named after the file, so the file is named after the tunnel.
        var text = Encoding.UTF8.GetString(files.First(f => f.Name == config).Data).TrimStart('﻿');
        var configPath = Path.Combine(directory, name + ".conf");
        File.WriteAllText(configPath, VpnConfigPolicy.StripWireGuardScripts(text), new UTF8Encoding(false));

        // A tunnel of the same name left over from a crash would make the install fail.
        if (_tunnels.Values.Any(t => t.WireGuardName == name) || ServiceExists("WireGuardTunnel$" + name))
        {
            await RunAsync(wireguard, "/uninstalltunnelservice", name);
            await Task.Delay(500);
        }

        var (exit, output) = await RunAsync(wireguard, "/installtunnelservice", configPath);
        if (exit != 0)
        {
            Cleanup(directory);
            return Failure(output.Trim().Length > 0 ? output.Trim() : $"WireGuard could not start the tunnel (exit {exit}).");
        }

        var handle = "wg:" + name;
        _tunnels[handle] = new Tunnel(handle, directory, null, new StringBuilder(output), null, name);
        Log?.Invoke($"wireguard {name} installed");
        return new HelperMessage { Type = "result", Ok = true, Handle = handle };
    }

    internal static string? WireGuardPath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe");
        return File.Exists(path) ? path : null;
    }

    private static bool ServiceExists(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return key is not null;
    }

    private static async Task<(int Exit, string Output)> RunAsync(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = file,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);
        if (process is null)
        {
            return (-1, string.Empty);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (-1, "Timed out.");
        }

        return (process.ExitCode, (await stdout) + (await stderr));
    }

    // ===================================== Stop and logs =====================================

    internal async Task<HelperMessage> StopAsync(string? handle)
    {
        if (handle is null)
        {
            return Failure("No such tunnel.");
        }

        if (handle.StartsWith("wg:", StringComparison.Ordinal))
        {
            // Also stops a tunnel from a previous run of the helper, which this one never saw.
            var name = VpnConfigPolicy.WireGuardTunnelName(handle[3..]);
            if (WireGuardPath() is { } wireguard)
            {
                await RunAsync(wireguard, "/uninstalltunnelservice", name);
            }

            if (_tunnels.TryRemove(handle, out var wg))
            {
                Cleanup(wg.Directory);
            }

            return new HelperMessage { Type = "result", Ok = true };
        }

        if (!_tunnels.TryRemove(handle, out var tunnel))
        {
            return new HelperMessage { Type = "result", Ok = true };
        }

        if (tunnel.Process is { HasExited: false } process)
        {
            // The app asks OpenVPN to exit over its management interface first; this is
            // the backstop for one that did not listen.
            if (!process.WaitForExit(1500))
            {
                process.Kill(entireProcessTree: true);
            }
        }

        // Keep the log a little while for a vpnLog that follows a failed connection.
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => Cleanup(tunnel.Directory), TaskScheduler.Default);
        _tunnels[handle + ":ended"] = tunnel;
        return new HelperMessage { Type = "result", Ok = true };
    }

    internal async Task<HelperMessage> ReadLogAsync(string? handle)
    {
        if (handle is null)
        {
            return Failure("No such tunnel.");
        }

        if (handle.StartsWith("wg:", StringComparison.Ordinal))
        {
            // WireGuard keeps one ring log for every tunnel; the tail is what matters.
            if (WireGuardPath() is not { } wireguard)
            {
                return Failure("WireGuard is not installed.");
            }

            var (_, output) = await RunAsync(wireguard, "/dumplog");
            var name = handle[3..];
            var lines = output.Split('\n').Where(l => l.Contains(name, StringComparison.OrdinalIgnoreCase) || l.Contains("[MGR]", StringComparison.Ordinal)).TakeLast(60);
            return new HelperMessage { Type = "result", Ok = true, Message = string.Join("\n", lines) };
        }

        if (!_tunnels.TryGetValue(handle, out var tunnel) && !_tunnels.TryGetValue(handle + ":ended", out tunnel))
        {
            return Failure("No such tunnel.");
        }

        var text = new StringBuilder();
        try
        {
            if (tunnel.LogPath is { } path && File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(Math.Max(0, stream.Length - (64 * 1024)), SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                text.Append(await reader.ReadToEndAsync());
            }
        }
        catch (IOException)
        {
        }

        lock (tunnel.Captured)
        {
            if (tunnel.Captured.Length > 0)
            {
                text.AppendLine().Append(tunnel.Captured);
            }
        }

        return new HelperMessage { Type = "result", Ok = true, Message = text.ToString() };
    }

    // ===================================== Staging =====================================

    private static string Stage(string id, IReadOnlyList<HelperFile> files)
    {
        var directory = Path.Combine(StagingRoot, id);
        CreateProtectedDirectory(directory);
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            var path = Path.GetFullPath(Path.Combine(directory, file.Name.Replace('/', '\\')));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Data);
        }

        return directory;
    }

    /// <summary>SYSTEM and Administrators only, inheritance cut: nobody else can swap a file in.</summary>
    private static void CreateProtectedDirectory(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        if (!Directory.Exists(StagingRoot))
        {
            new DirectoryInfo(StagingRoot).Create(security);
        }

        new DirectoryInfo(StagingRoot).SetAccessControl(security);
        new DirectoryInfo(directory).Create(security);
    }

    private static void Cleanup(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static HelperMessage Failure(string message) => new() { Type = "result", Ok = false, Message = message };

    /// <summary>Stops every tunnel: the helper is going away and must not leave a tunnel unowned.</summary>
    public void Dispose()
    {
        foreach (var tunnel in _tunnels.Values)
        {
            if (tunnel.WireGuardName is { } name && WireGuardPath() is { } wireguard)
            {
                RunAsync(wireguard, "/uninstalltunnelservice", name).GetAwaiter().GetResult();
            }

            if (tunnel.Process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
            }

            Cleanup(tunnel.Directory);
        }

        _tunnels.Clear();
    }

    private sealed class Tunnel(string handle, string directory, Process? process, StringBuilder captured, string? logPath, string? wireGuardName)
    {
        internal string Handle { get; } = handle;

        internal string Directory { get; } = directory;

        internal Process? Process { get; } = process;

        internal StringBuilder Captured { get; } = captured;

        internal string? LogPath { get; } = logPath;

        internal string? WireGuardName { get; } = wireGuardName;

        internal volatile bool Exited;
    }
}
