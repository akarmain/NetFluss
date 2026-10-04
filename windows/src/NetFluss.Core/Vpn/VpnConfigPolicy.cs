// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text;
using System.Text.RegularExpressions;

namespace NetFluss.Core.Vpn;

/// <summary>
/// What the helper refuses before it runs a user-imported config as LocalSystem.
///
/// <para>The macOS helper bans <c>plugin</c> and <c>config</c> and forces
/// <c>--script-security 1</c>, which stops every up/down script. Windows needs more: a
/// config is a list of file paths too, and a SYSTEM process will happily write a status
/// file over <c>C:\Windows\System32\…</c> or send the first lines of any file it can read
/// as a "password". So every directive that names a file must name one inside the
/// profile's own folder, and the ones that only ever write files are refused outright.</para>
/// </summary>
public static partial class VpnConfigPolicy
{
    /// <summary>Load code or nest configs our scan cannot see.</summary>
    private static readonly HashSet<string> Banned =
    [
        "plugin", "config",

        // Write files (or change where files go) with SYSTEM's rights.
        "status", "writepid", "log", "log-append", "replay-persist", "ifconfig-pool-persist",
        "cd", "chroot", "tmp-dir", "daemon", "service",

        // Our own management interface is set on the command line; a config may not open another.
        "management", "management-client-user", "management-client-group",
    ];

    /// <summary>Directives whose first argument is a file OpenVPN reads.</summary>
    private static readonly HashSet<string> Reads =
    [
        "ca", "cert", "key", "tls-auth", "tls-crypt", "tls-crypt-v2", "pkcs12", "crl-verify",
        "extra-certs", "dh", "secret", "auth-user-pass", "askpass", "http-proxy-user-pass",
    ];

    /// <summary>
    /// Null when the config may run, else the directive that stops it. File references are
    /// checked against the profile folder; inline blocks (<c>&lt;ca&gt;…&lt;/ca&gt;</c>) are fine.
    /// </summary>
    public static string? UnsafeOpenVpnDirective(string text)
    {
        foreach (var tokens in VpnConfigImporter.Directives(text))
        {
            var name = tokens[0];
            if (Banned.Contains(name))
            {
                return name;
            }

            if (Reads.Contains(name) && tokens.Length >= 2)
            {
                var reference = tokens[1].Trim('"', '\'');
                if (reference.Length > 0 && reference != "[inline]" && !reference.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                    !VpnConfigImporter.IsSafeRelative(reference))
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A WireGuard config with its script hooks removed. WireGuard for Windows ignores them
    /// unless an administrator has turned on DangerousScriptExecution — and a machine where
    /// that is on is exactly where a profile must not be allowed to bring its own.
    /// </summary>
    public static string StripWireGuardScripts(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => !ScriptLine().IsMatch(line));
        return string.Join("\r\n", lines);
    }

    [GeneratedRegex(@"^\s*(PreUp|PostUp|PreDown|PostDown)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptLine();

    /// <summary>
    /// A tunnel name WireGuard for Windows accepts — it names the service and the adapter
    /// after the file: letters, digits and <c>_=+.-</c>, at most 32 characters.
    /// </summary>
    public static string WireGuardTunnelName(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '=' or '+' or '.' or '-' ? c : '-');
        }

        var cleaned = builder.ToString().Trim('-', '.');
        if (cleaned.Length == 0)
        {
            cleaned = "NetFluss";
        }

        return cleaned.Length > 32 ? cleaned[..32] : cleaned;
    }
}
