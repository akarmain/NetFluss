// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using NetFluss.Core;

namespace NetFluss.Service;

/// <summary>
/// Entry point for every way the helper runs:
///
/// <list type="bullet">
/// <item><b>no arguments</b> — under the Service Control Manager, as LocalSystem;</item>
/// <item><b>install</b> / <b>uninstall</b> — launched elevated by the app's "Install helper"
/// button, never by anything else;</item>
/// <item><b>console</b> — the same server in the foreground, for development. Unelevated it
/// serves the pipe but cannot start the trace, which is itself a useful thing to test.</item>
/// </list>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var verb = args.FirstOrDefault()?.ToLowerInvariant();

        switch (verb)
        {
            case "install":
                return Installer.Install();

            case "uninstall":
                return Installer.Uninstall();

            case "console":
                return RunConsole(args.Skip(1).ToArray());

            case null:
                return ServiceHost.Run(() => new HelperServer(HelperProtocol.PipeName));

            default:
                Console.Error.WriteLine("usage: NetFluss.Service [install | uninstall | console [--pipe name]]");
                return 2;
        }
    }

    private static int RunConsole(string[] args)
    {
        var pipe = HelperProtocol.PipeName;
        var index = Array.IndexOf(args, "--pipe");
        if (index >= 0 && index + 1 < args.Length)
        {
            pipe = args[index + 1];
        }

        using var server = new HelperServer(pipe);
        server.Log = Console.WriteLine;
        server.Start();

        Console.WriteLine($"NetFluss helper serving \\\\.\\pipe\\{pipe}. Press Ctrl+C to stop.");
        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };

        stop.Wait();
        return 0;
    }
}
