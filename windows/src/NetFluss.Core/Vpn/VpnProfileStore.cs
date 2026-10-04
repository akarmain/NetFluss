// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json;

namespace NetFluss.Core.Vpn;

/// <summary>
/// The saved profiles and their config files — the macOS <c>VPNProfileStore</c>:
/// <c>%LOCALAPPDATA%\NetFluss\VPN\profiles.json</c> plus one folder per profile.
/// </summary>
public sealed class VpnProfileStore(string root)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string DefaultRoot(string localAppData) => Path.Combine(localAppData, "NetFluss", "VPN");

    public string Root { get; } = root;

    private string IndexPath => Path.Combine(Root, "profiles.json");

    public string ProfileDirectory(Guid id) => Path.Combine(Root, id.ToString("N"));

    public IReadOnlyList<VpnProfile> Load()
    {
        try
        {
            return File.Exists(IndexPath) ? JsonSerializer.Deserialize<List<VpnProfile>>(File.ReadAllText(IndexPath), Json) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<VpnProfile> profiles)
    {
        Directory.CreateDirectory(Root);
        var temporary = IndexPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profiles, Json));
        File.Move(temporary, IndexPath, overwrite: true);
    }

    public void WriteFiles(Guid id, IEnumerable<VpnConfigFile> files)
    {
        var directory = ProfileDirectory(id);
        foreach (var file in files)
        {
            if (!VpnConfigImporter.IsSafeRelative(file.Name))
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(directory, file.Name));
            if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Data);
        }
    }

    /// <summary>The config a server connects with: its own file, else the profile's primary one.</summary>
    public string ConfigPath(VpnProfile profile, VpnServer? server)
        => Path.Combine(ProfileDirectory(profile.Id), (server?.ConfigFileName ?? profile.ConfigFileName).Replace('/', Path.DirectorySeparatorChar));

    public void RemoveFiles(Guid id)
    {
        try
        {
            var directory = ProfileDirectory(id);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A locked file stays behind; the profile itself is gone from the list.
        }
    }
}
