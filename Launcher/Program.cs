using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DMC5DualSense.Launcher;

internal static class Program
{
    private const int BridgePort = 27105;

    private static int Main(string[] args)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var logPath = Path.Combine(baseDirectory, "launcher.log");

        void Log(string message)
        {
            try
            {
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch
            {
                // A logging failure must never prevent the game from starting.
            }
        }

        var gameExecutable = ResolveGameExecutable(args, baseDirectory);
        if (gameExecutable is null)
        {
            Log("DevilMayCry5.exe was not supplied by Steam and could not be found.");
            return 2;
        }

        var gameArguments = ResolveGameArguments(args, gameExecutable);
        Process? bridge = null;

        try
        {
            StopExistingBridge();
            Thread.Sleep(250);

            bridge = StartBridge(baseDirectory, Log);
            if (bridge is null)
                Log("Bridge could not be started; DMC5 will still be launched.");
            else
                Thread.Sleep(450);

            var gameStart = new ProcessStartInfo
            {
                FileName = gameExecutable,
                WorkingDirectory = Path.GetDirectoryName(gameExecutable) ?? baseDirectory,
                UseShellExecute = false
            };
            foreach (var argument in gameArguments)
                gameStart.ArgumentList.Add(argument);

            using var game = Process.Start(gameStart);
            if (game is null)
            {
                Log("Steam command did not create a DMC5 process.");
                return 3;
            }

            Log($"DMC5 started as PID {game.Id}; bridge PID {bridge?.Id.ToString(CultureInfo.InvariantCulture) ?? "none"}.");
            game.WaitForExit();
            Log($"DMC5 exited with code {game.ExitCode}.");
            return game.ExitCode;
        }
        catch (Exception ex)
        {
            Log("Launcher failure: " + ex);
            return 4;
        }
        finally
        {
            StopExistingBridge();
            if (bridge is not null)
            {
                try
                {
                    if (!bridge.WaitForExit(2000)) bridge.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The bridge may already have exited through its parent monitor.
                }
                bridge.Dispose();
            }
        }
    }

    private static Process? StartBridge(string baseDirectory, Action<string> log)
    {
        var path = Path.Combine(baseDirectory, "DMC5DualSense.Bridge.exe");
        if (!File.Exists(path))
        {
            log("Bridge executable not found: " + path);
            return null;
        }

        return Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = "--parent " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            WorkingDirectory = baseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static string? ResolveGameExecutable(string[] args, string baseDirectory)
    {
        if (args.Length > 0 && File.Exists(args[0]) &&
            Path.GetFileName(args[0]).Equals("DevilMayCry5.exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(args[0]);

        var sibling = Path.GetFullPath(Path.Combine(baseDirectory, "..", "DevilMayCry5.exe"));
        return File.Exists(sibling) ? sibling : null;
    }

    private static IReadOnlyList<string> ResolveGameArguments(string[] args, string gameExecutable)
    {
        if (args.Length > 0 &&
            Path.GetFullPath(args[0]).Equals(gameExecutable, StringComparison.OrdinalIgnoreCase))
            return args.Skip(1).ToArray();

        return args;
    }

    private static void StopExistingBridge()
    {
        try
        {
            using var udp = new UdpClient();
            var payload = Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"shutdown\"}");
            udp.Send(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, BridgePort));
        }
        catch
        {
            // No bridge listening is the normal first-launch case.
        }
    }
}
