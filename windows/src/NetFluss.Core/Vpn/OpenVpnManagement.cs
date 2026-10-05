// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace NetFluss.Core.Vpn;

/// <summary>One thing the OpenVPN management interface reported.</summary>
public abstract record OpenVpnEvent
{
    /// <summary>"CONNECTING", "CONNECTED", "RECONNECTING", "EXITING"…; the tunnel address on CONNECTED.</summary>
    public sealed record State(string Name, string? AssignedIp) : OpenVpnEvent;

    public sealed record ByteCount(ulong In, ulong Out) : OpenVpnEvent;

    /// <summary>OpenVPN asks for credentials of a kind ("Auth", "Private Key").</summary>
    public sealed record NeedCredentials(string Kind, bool UsernameToo) : OpenVpnEvent;

    public sealed record AuthFailed(string Message) : OpenVpnEvent;

    public sealed record Log(string Message) : OpenVpnEvent;

    public sealed record Hold : OpenVpnEvent;
}

/// <summary>
/// The OpenVPN management protocol — the macOS <c>OpenVPNManagementClient</c>, over TCP on
/// the loopback interface (Windows OpenVPN has no Unix sockets), behind the password the
/// helper generated: release the hold, answer credential prompts, follow state and byte
/// counts, and ask for a clean exit.
/// </summary>
public sealed class OpenVpnManagementClient : IAsyncDisposable
{
    private readonly int _port;
    private readonly string _password;
    private readonly Func<int, bool>? _verifyListener;
    private readonly SemaphoreSlim _write = new(1, 1);
    private TcpClient? _client;
    private StreamWriter? _writer;
    private CancellationTokenSource? _reading;
    private bool _ready;

    /// <param name="verifyListener">
    /// Asked, before anything is sent, whether the process listening on the port is the
    /// OpenVPN the helper started — so the password and the user's credentials never go
    /// to a program that grabbed the port first.
    /// </param>
    public OpenVpnManagementClient(int port, string password, Func<int, bool>? verifyListener = null)
    {
        _port = port;
        _password = password;
        _verifyListener = verifyListener;
    }

    /// <summary>Raised on a background thread for every event.</summary>
    public event Action<OpenVpnEvent>? EventReceived;

    /// <summary>Raised when the connection ends — OpenVPN exited.</summary>
    public event Action? Closed;

    /// <summary>Connects, retrying while OpenVPN is still starting (about five seconds).</summary>
    public async Task ConnectAsync(CancellationToken cancellation)
    {
        for (var attempt = 0; ; attempt++)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(System.Net.IPAddress.Loopback, _port, cancellation).ConfigureAwait(false);
                _client = client;
                break;
            }
            catch (SocketException) when (attempt < 25)
            {
                client.Dispose();
                await Task.Delay(200, cancellation).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        if (_verifyListener is not null && !_verifyListener(_port))
        {
            _client.Dispose();
            _client = null;
            throw new IOException("Another program is listening on the OpenVPN management port.");
        }

        var stream = _client.GetStream();
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        _reading = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var reader = new StreamReader(stream, Encoding.UTF8);

        // OpenVPN prompts "ENTER PASSWORD:" without a line break, so a line reader would wait
        // on it forever. The answer can go first: OpenVPN reads it once it has prompted.
        await SendAsync(_password).ConfigureAwait(false);
        _ = Task.Run(() => ReadLoop(reader, _reading.Token), CancellationToken.None);
    }

    private async Task ReadLoop(StreamReader reader, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested && await reader.ReadLineAsync(cancellation).ConfigureAwait(false) is { } raw)
            {
                // The unterminated prompt arrives glued to the reply that follows it.
                var line = raw.StartsWith("ENTER PASSWORD:", StringComparison.Ordinal) ? raw["ENTER PASSWORD:".Length..] : raw;

                if (line.StartsWith("ERROR: bad password", StringComparison.Ordinal))
                {
                    EventReceived?.Invoke(new OpenVpnEvent.AuthFailed("The OpenVPN management password was refused."));
                    break;
                }

                if (!_ready && (line.StartsWith("SUCCESS: password is correct", StringComparison.Ordinal) || line.StartsWith(">INFO:", StringComparison.Ordinal)))
                {
                    _ready = true;
                    await SendAsync("state on").ConfigureAwait(false);
                    await SendAsync("bytecount 2").ConfigureAwait(false);
                    await SendAsync("hold release").ConfigureAwait(false);
                    continue;
                }

                if (Parse(line) is { } parsed)
                {
                    if (parsed is OpenVpnEvent.Hold)
                    {
                        await SendAsync("hold release").ConfigureAwait(false);
                        continue;
                    }

                    EventReceived?.Invoke(parsed);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
        }

        Closed?.Invoke();
    }

    /// <summary>One management line, or null for replies and chatter.</summary>
    public static OpenVpnEvent? Parse(string line)
    {
        line = line.TrimEnd('\r');
        if (line.StartsWith(">STATE:", StringComparison.Ordinal))
        {
            // >STATE:<time>,<state>,<description>,<local ip>,<remote ip>,...
            var fields = line[">STATE:".Length..].Split(',');
            return fields.Length >= 2
                ? new OpenVpnEvent.State(fields[1], fields.Length >= 4 && fields[3].Length > 0 ? fields[3] : null)
                : null;
        }

        if (line.StartsWith(">BYTECOUNT:", StringComparison.Ordinal))
        {
            var parts = line[">BYTECOUNT:".Length..].Split(',');
            return parts.Length == 2 &&
                   ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var received) &&
                   ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sent)
                ? new OpenVpnEvent.ByteCount(received, sent)
                : null;
        }

        if (line.StartsWith(">PASSWORD:", StringComparison.Ordinal))
        {
            var body = line[">PASSWORD:".Length..];
            if (body.StartsWith("Verification Failed", StringComparison.Ordinal))
            {
                return new OpenVpnEvent.AuthFailed(body);
            }

            // "Need 'Auth' username/password" or "Need 'Private Key' password"
            var first = body.IndexOf('\'');
            var second = first >= 0 ? body.IndexOf('\'', first + 1) : -1;
            return second > first
                ? new OpenVpnEvent.NeedCredentials(body[(first + 1)..second], body.Contains("username", StringComparison.Ordinal))
                : null;
        }

        if (line.StartsWith(">LOG:", StringComparison.Ordinal))
        {
            var parts = line[">LOG:".Length..].Split(',', 3);
            return new OpenVpnEvent.Log(parts.Length == 3 ? parts[2] : line);
        }

        return line.StartsWith(">HOLD:", StringComparison.Ordinal) ? new OpenVpnEvent.Hold() : null;
    }

    /// <summary>Answers a credential prompt — one command after the other, never interleaved.</summary>
    public async Task SendCredentialsAsync(string kind, string? username, string? password)
    {
        if (username is not null)
        {
            await SendAsync($"username \"{Escape(kind)}\" \"{Escape(username)}\"").ConfigureAwait(false);
        }

        if (password is not null)
        {
            await SendAsync($"password \"{Escape(kind)}\" \"{Escape(password)}\"").ConfigureAwait(false);
        }
    }

    /// <summary>Asks OpenVPN to exit cleanly.</summary>
    public Task SignalExitAsync() => SendAsync("signal SIGTERM");

    /// <summary>One command at a time: StreamWriter refuses overlapping writes, and losing one silently loses a password.</summary>
    private async Task SendAsync(string command)
    {
        if (_writer is not { } writer)
        {
            return;
        }

        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(command).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Backslashes and quotes escaped for the management command grammar.</summary>
    public static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// The real reason from an OpenVPN log: its last error-looking lines without timestamps,
    /// rather than "the connection stopped".
    /// </summary>
    public static string? SummarizeLog(string log)
    {
        var lines = log.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        string[] markers = ["error", "fatal", "cannot", "failed", "must define", "auth_"];
        var notable = lines.Where(l => markers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase))).ToList();
        var chosen = notable.Count == 0 ? lines.TakeLast(1) : notable.TakeLast(2);

        // "2026-10-04 20:15:01 message" → "message"
        var cleaned = chosen.Select(l =>
        {
            var parts = l.Split(' ', 3);
            return parts.Length == 3 && parts[0].All(c => char.IsDigit(c) || c == '-') && parts[1].Contains(':', StringComparison.Ordinal) ? parts[2] : l;
        });
        var joined = string.Join(" — ", cleaned);
        return joined.Length == 0 ? null : joined;
    }

    public async ValueTask DisposeAsync()
    {
        if (_reading is not null)
        {
            await _reading.CancelAsync().ConfigureAwait(false);
            _reading.Dispose();
        }

        _writer?.Dispose();
        _client?.Dispose();
    }
}
