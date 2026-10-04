// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetFluss.Core;

/// <summary>Where a speed test measures. Raw values are the macOS ones.</summary>
public enum SpeedTestProvider
{
    MLab,
    Cloudflare,
}

public static class SpeedTestProviders
{
    public static string Id(this SpeedTestProvider provider) => provider == SpeedTestProvider.MLab ? "mlab" : "cloudflare";

    public static SpeedTestProvider FromId(string? id) => id == "mlab" ? SpeedTestProvider.MLab : SpeedTestProvider.Cloudflare;

    public static string DisplayName(this SpeedTestProvider provider) => provider == SpeedTestProvider.MLab ? "M-Lab" : "Cloudflare";

    /// <summary>Localization keys, worded exactly as on macOS.</summary>
    public static string ShortDescriptionKey(this SpeedTestProvider provider) => provider == SpeedTestProvider.MLab
        ? "Public measurement servers with a more Internet-path-oriented result."
        : "Nearby Cloudflare edge locations for a fast CDN-oriented result.";

    public static string RuntimeDescriptionKey(this SpeedTestProvider provider) => provider == SpeedTestProvider.MLab
        ? "M-Lab chooses a nearby public measurement server and runs a full ndt7 download and upload test."
        : "Cloudflare measures against its nearest edge, which can be faster than broader Internet-path tests.";
}

/// <summary>One finished test. Port of the macOS <c>SpeedTestResult</c>, note included.</summary>
public sealed record SpeedTestResult
{
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonConverter(typeof(ProviderConverter))]
    public SpeedTestProvider Provider { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset FinishedAt { get; init; }

    public double? DownloadMbps { get; init; }

    public double? UploadMbps { get; init; }

    public double? LatencyMs { get; init; }

    public double? JitterMs { get; init; }

    public string? ServerName { get; init; }

    public string? ServerLocation { get; init; }

    public string? Note { get; init; }

    /// <summary>"mlab"/"cloudflare" on disk, as on macOS.</summary>
    private sealed class ProviderConverter : JsonConverter<SpeedTestProvider>
    {
        public override SpeedTestProvider Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => SpeedTestProviders.FromId(reader.GetString());

        public override void Write(Utf8JsonWriter writer, SpeedTestProvider value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Id());
    }
}

/// <summary>
/// The last thirty results, newest first, kept on this PC — the macOS speed test history,
/// in a JSON file under %LOCALAPPDATA% rather than UserDefaults.
/// </summary>
public sealed class SpeedTestHistory
{
    public const int MaximumCount = 30;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string? _path;
    private List<SpeedTestResult> _results;

    public SpeedTestHistory(string? path)
    {
        _path = path;
        _results = Load(path);
    }

    public static string DefaultPath(string localAppData) => Path.Combine(localAppData, "NetFluss", "speedtest-history.json");

    public IReadOnlyList<SpeedTestResult> Results => _results;

    public void Add(SpeedTestResult result)
    {
        _results.Insert(0, result);
        if (_results.Count > MaximumCount)
        {
            _results.RemoveRange(MaximumCount, _results.Count - MaximumCount);
        }

        Save();
    }

    /// <summary>Sets a result's note; blank removes it. Returns false for an unknown id.</summary>
    public bool SetNote(Guid id, string? note)
    {
        var index = _results.FindIndex(r => r.Id == id);
        if (index < 0)
        {
            return false;
        }

        var normalized = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (_results[index].Note == normalized)
        {
            return true;
        }

        _results[index] = _results[index] with { Note = normalized };
        Save();
        return true;
    }

    public void Clear()
    {
        _results = [];
        Save();
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_results, Json));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; a locked profile loses it for this session only.
        }
    }

    private static List<SpeedTestResult> Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<SpeedTestResult>>(File.ReadAllText(path), Json) ?? [];
            return [.. loaded.OrderByDescending(r => r.FinishedAt).Take(MaximumCount)];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
