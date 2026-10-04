// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.IO.Pipes;
using System.Text;

namespace NetFluss.App;

/// <summary>
/// One NetFluss per user session, and a way for a second launch to talk to the first.
///
/// <para>macOS gives this for free: opening an app that is already running activates it.
/// On Windows a second double-click starts a second process, which here would mean a second
/// tray icon, a second taskbar meter stacked on the first and two writers racing on the same
/// settings file. So the second process forwards its command line to the first over a named
/// pipe and exits — with no arguments that command is "show the popover", which is what a
/// user double-clicking a running tray app is asking for.</para>
///
/// <para>The pipe is <see cref="PipeOptions.CurrentUserOnly"/>: another account on the same
/// machine must not be able to drive this one's NetFluss.</para>
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private bool _owned;

    private SingleInstance(Mutex mutex, bool owned, string pipeName)
    {
        _mutex = mutex;
        _owned = owned;
        _pipeName = pipeName;
    }

    /// <summary>True for the process that runs the app; false for one that should forward and exit.</summary>
    internal bool IsPrimary => _owned;

    internal static SingleInstance Acquire()
    {
        // Scoped to the session ("Local\"), so fast user switching gives each signed-in user
        // their own instance rather than letting the first user's block everyone else's.
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var mutex = new Mutex(initiallyOwned: true, $@"Local\NetFluss.Instance.{session}", out var created);

        return new SingleInstance(mutex, created, $"NetFluss.Commands.{session}.{Environment.UserName}");
    }

    /// <summary>Starts listening for forwarded command lines; each arrives on the thread pool.</summary>
    internal void Listen(Action<string[]> onCommand)
    {
        if (!_owned)
        {
            throw new InvalidOperationException("Only the primary instance listens.");
        }

        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var payload = await reader.ReadToEndAsync(_stop.Token).ConfigureAwait(false);

                    // One argument per line. Arguments cannot contain a newline when they
                    // arrive from a real command line, so no further escaping is needed.
                    var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(arg => arg.TrimEnd('\r'))
                        .Take(16)
                        .ToArray();

                    onCommand(args);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // A client that connected and vanished; keep serving.
                }
            }
        });
    }

    /// <summary>Hands this process's arguments to the running instance.</summary>
    internal bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);

            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(string.Join('\n', args.Length == 0 ? ["--popover"] : args));
            writer.Flush();
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();

        if (_owned)
        {
            _mutex.ReleaseMutex();
            _owned = false;
        }

        _mutex.Dispose();
        _stop.Dispose();
    }
}
