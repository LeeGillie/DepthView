using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DepthView.Integrations.Common;
using DepthView.Integrations.LightBurn.Control;

namespace DepthView;

/// <summary>
/// Command-line entry points for the integration work.
///
/// Both exist so the pieces can be exercised without a GUI, which matters more here than
/// usual: one talks to a live copy of LightBurn over a socket, and the other reads somebody's
/// project file. Neither is something to first find out about from inside a dialog.
/// </summary>
internal static partial class Program
{
    /// <summary>DepthView --project &lt;file&gt; - read a laser project and report it.</summary>
    private static int RunProject(string[] args)
    {
        AttachParentConsole();

        var path = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (path is null)
        {
            Console.Error.WriteLine("Usage: DepthView --project <file.lbrn2|file.wws>");
            Console.Error.WriteLine($"       Known extensions: {string.Join(", ", ProjectReaders.AllExtensions)}");
            return 2;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No such file: {path}");
            return 2;
        }

        var result = ProjectReaders.Read(path);
        Console.Write(ProjectReport.Write(result));

        // Non-zero for "could not read at all", zero for a partial read. A partial read of an
        // undocumented format is the expected outcome rather than a failure, and a script that
        // treats it as one would be unusable against .wws.
        return result.Ok ? 0 : 1;
    }

    /// <summary>
    /// DepthView --lb &lt;command&gt; [args] - drive a running copy of LightBurn.
    ///
    /// START is deliberately not reachable by an abbreviation or a default. Every other command
    /// here is recoverable; that one fires a laser.
    /// </summary>
    private static int RunLightBurnControl(string[] args)
    {
        AttachParentConsole();

        if (args.Length == 0)
        {
            Console.Error.WriteLine("""
                Usage: DepthView --lb <command> [value]

                  ping                  is LightBurn listening
                  status                ask what state it is in
                  load <file>           open a project
                  forceload <file>      open one, discarding unsaved changes
                  start                 begin the loaded job - THIS FIRES THE LASER
                  close                 ask LightBurn to close
                  forceclose            close without prompting to save
                  raw <text>            send anything, for commands not listed here
                  listen [seconds]      just watch for datagrams

                Options:
                  --host <ip>           default 127.0.0.1
                  --send-port <n>       default 19840
                  --listen-port <n>     default 19841
                  --timeout <ms>        default 2000

                The command set is community knowledge rather than published documentation, so
                'raw' is here for the ones nobody has written down. No reply is not proof of
                failure: UDP drops datagrams silently, and several commands may not answer.
                """);
            return 2;
        }

        string host = FlagStr(args, "--host") ?? "127.0.0.1";
        int sendPort = (int)(Flag(args, "--send-port") ?? LightBurnControl.DefaultSendPort);
        int listenPort = (int)(Flag(args, "--listen-port") ?? LightBurnControl.DefaultListenPort);
        var timeout = TimeSpan.FromMilliseconds(Flag(args, "--timeout") ?? 2000);

        string verb = args[0].ToLowerInvariant();
        string? value = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));

        try
        {
            return RunLightBurnControlAsync(verb, value, host, sendPort, listenPort, timeout)
                   .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("LightBurn control failed: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunLightBurnControlAsync(
        string verb, string? value, string host, int sendPort, int listenPort, TimeSpan timeout)
    {
        using var lb = new LightBurnControl(host, sendPort, listenPort);

        try
        {
            lb.StartListening();
        }
        catch (Exception ex)
        {
            // Worth continuing without a listener: sending still works, and something else
            // already bound to the reply port is a normal thing to find on a laser machine.
            Console.Error.WriteLine(
                $"Could not listen on {listenPort} ({ex.Message}). Sending anyway; replies will "
                + "not be seen.");
        }

        if (verb == "listen")
        {
            double seconds = double.TryParse(value, out double s) ? s : 30;
            Console.WriteLine($"Listening on {listenPort} for {seconds:0.#}s. Ctrl-C to stop.");
            lb.MessageReceived += (_, m) => Console.WriteLine(m.ToString());
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            return 0;
        }

        LightBurnMessage? reply;

        switch (verb)
        {
            case "ping": reply = await lb.PingAsync(timeout); break;
            case "status": reply = await lb.StatusAsync(timeout); break;

            case "load":
            case "forceload":
                if (value is null)
                {
                    Console.Error.WriteLine($"'{verb}' needs a file path.");
                    return 2;
                }
                // Sent as an absolute path where one can be formed: LightBurn resolves it in
                // its own working directory, which is not this process's.
                string full = File.Exists(value) ? Path.GetFullPath(value) : value;
                reply = verb == "load"
                    ? await lb.LoadFileAsync(full, timeout)
                    : await lb.ForceLoadFileAsync(full, timeout);
                break;

            case "start":
                Console.WriteLine("Sending START - this begins the loaded job.");
                reply = await lb.StartJobAsync(timeout);
                break;

            case "close": reply = await lb.CloseAsync(timeout); break;
            case "forceclose": reply = await lb.ForceCloseAsync(timeout); break;

            case "raw":
                if (value is null)
                {
                    Console.Error.WriteLine("'raw' needs something to send.");
                    return 2;
                }
                reply = await lb.SendRawAsync(value, timeout);
                break;

            default:
                Console.Error.WriteLine($"Unknown command '{verb}'. Run --lb with no arguments for the list.");
                return 2;
        }

        if (reply is null)
        {
            Console.WriteLine("No reply within the timeout.");
            Console.WriteLine(
                "That is not proof of anything: this command may not answer, LightBurn may not "
                + "be running, or the datagram may have been dropped.");
            return 1;
        }

        Console.WriteLine(reply.Text);
        return 0;
    }

    private static string? FlagStr(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
