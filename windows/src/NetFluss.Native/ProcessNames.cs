// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NetFluss.Native;

/// <summary>
/// Process id → the name a person would recognise: "Google Chrome" rather than
/// "chrome.exe", from the executable's own version resource.
///
/// <para>Cached briefly by pid. Process ids are reused, but not within seconds, and the
/// lookup — open the process, read the image path, parse the version block — is far too
/// expensive to repeat for every event of a busy download.</para>
/// </summary>
public sealed class ProcessNames
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly Dictionary<int, (string Name, string? Path, DateTime Expires)> _cache = [];
    private readonly Dictionary<string, string> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public string Resolve(int processId) => Lookup(processId).Name;

    /// <summary>The full image path, when the process could be opened.</summary>
    public string? PathOf(int processId) => Lookup(processId).Path;

    private (string Name, string? Path) Lookup(int processId)
    {
        // The kernel attributes stack work it does itself to the idle and System processes.
        if (processId is 0 or 4)
        {
            return ("System", null);
        }

        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_cache.TryGetValue(processId, out var cached) && cached.Expires > now)
            {
                return (cached.Name, cached.Path);
            }
        }

        var path = ImagePath(processId);
        var name = path is null ? FallbackName(processId) : FriendlyName(path);

        lock (_gate)
        {
            _cache[processId] = (name, path, now + Lifetime);

            if (_cache.Count > 4096)
            {
                foreach (var expired in _cache.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList())
                {
                    _cache.Remove(expired);
                }
            }
        }

        return (name, path);
    }

    private string FriendlyName(string path)
    {
        lock (_gate)
        {
            if (_descriptions.TryGetValue(path, out var known))
            {
                return known;
            }
        }

        var fallback = Path.GetFileNameWithoutExtension(path);
        string name;

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);

            // FileDescription is the field Task Manager shows. Some vendors leave it empty or
            // fill it with something generic, so fall back to the product name and then to
            // the file name — and a host such as svchost keeps its file name, since its
            // description ("Host Process for Windows Services") describes every instance.
            var description = info.FileDescription?.Trim();
            name = !string.IsNullOrEmpty(description) && description.Length <= 48 && !IsGenericHost(fallback)
                ? description
                : !string.IsNullOrWhiteSpace(info.ProductName) && info.ProductName.Length <= 48 && !IsGenericHost(fallback)
                    ? info.ProductName.Trim()
                    : fallback;
        }
        catch (FileNotFoundException)
        {
            name = fallback;
        }

        lock (_gate)
        {
            _descriptions[path] = name;
        }

        return name;
    }

    private static bool IsGenericHost(string fileName)
        => fileName.Equals("svchost", StringComparison.OrdinalIgnoreCase) ||
           fileName.Equals("dllhost", StringComparison.OrdinalIgnoreCase) ||
           fileName.Equals("rundll32", StringComparison.OrdinalIgnoreCase) ||
           fileName.Equals("conhost", StringComparison.OrdinalIgnoreCase);

    private static string FallbackName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Exited between the event and the lookup, which a short-lived process does.
            return $"PID {processId}";
        }
    }

    private static string? ImagePath(int processId)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
