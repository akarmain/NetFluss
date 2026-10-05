// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Reflection;

namespace NetFluss.Core;

/// <summary>
/// The version a build reports: "2.6.0", or "2.6.0-beta.1" for a beta. Read from the
/// informational version, which keeps the pre-release suffix the numeric assembly and file
/// versions drop (both are 2.6.0.0 for a beta), minus the "+commit" the SDK appends.
/// </summary>
public static class BuildVersion
{
    public static string Of(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var metadata = informational.IndexOf('+', StringComparison.Ordinal);
            return metadata >= 0 ? informational[..metadata] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>"2.6.0-beta.1" is; "2.6.0" is not. A pre-release build is offered pre-release updates.</summary>
    public static bool IsPrerelease(string version)
    {
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return (metadata >= 0 ? version[..metadata] : version).Contains('-', StringComparison.Ordinal);
    }
}
