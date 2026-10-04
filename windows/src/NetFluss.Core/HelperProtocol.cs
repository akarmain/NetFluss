// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetFluss.Core;

/// <summary>
/// The contract between NetFluss and its helper service, the Windows counterpart of the
/// macOS <c>NetflussHelperProtocol</c>. Newline-delimited JSON over a duplex named pipe.
///
/// <para>JSON rather than a binary format because the volume is small — one message a second
/// while a consumer is open — and because a protocol a person can read in a pipe dump is a
/// protocol that gets debugged.</para>
/// </summary>
public static class HelperProtocol
{
    /// <summary>The pipe the service listens on. Machine-wide: the service runs as LocalSystem.</summary>
    public const string PipeName = "NetFluss.Helper";

    /// <summary>The Windows service name.</summary>
    public const string ServiceName = "NetFlussHelper";

    /// <summary>Bumped whenever a message changes shape.</summary>
    public const int Version = 1;

    /// <summary>A line longer than this is not a NetFluss message, and is dropped unread.</summary>
    public const int MaximumLineLength = 1 << 20;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Json);

    public static T? Deserialize<T>(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

/// <summary>Client → helper.</summary>
public sealed record HelperRequest
{
    /// <summary>"hello", "subscribe", "unsubscribe", "setDns", "restartAdapter".</summary>
    public required string Op { get; init; }

    /// <summary>Echoed in the matching result, so a client can await one specific answer.</summary>
    public int Id { get; init; }

    public int Version { get; init; } = HelperProtocol.Version;

    /// <summary>Interface GUID for setDns and restartAdapter.</summary>
    public string? Adapter { get; init; }

    /// <summary>Resolvers for setDns; empty restores DHCP.</summary>
    public IReadOnlyList<string>? Servers { get; init; }

    /// <summary>Whether a subscription wants per-flow detail (the Network Slice) as well.</summary>
    public bool Flows { get; init; }
}

/// <summary>Helper → client.</summary>
public sealed record HelperMessage
{
    /// <summary>"hello", "traffic", "result".</summary>
    public required string Type { get; init; }

    public int Id { get; init; }

    public int Version { get; init; }

    /// <summary>The helper's build version, compared with the app's to offer an update.</summary>
    public string? HelperVersion { get; init; }

    /// <summary>Why traffic is unavailable inside the helper, when it is.</summary>
    public string? TraceStatus { get; init; }

    public bool Ok { get; init; }

    public string? Message { get; init; }

    public long ElapsedMs { get; init; }

    public IReadOnlyList<HelperProcess>? Processes { get; init; }

    public IReadOnlyList<HelperFlow>? Flows { get; init; }
}

public sealed record HelperProcess(int Pid, string Name, long Rx, long Tx);

public sealed record HelperFlow(int Pid, string Proto, string Local, int LocalPort, string Remote, int RemotePort, long Rx, long Tx);
