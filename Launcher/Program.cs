using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DMC5DualSense.Launcher;

internal static class Program
{
    private const int BridgePort = 27105;

    private static int Main(string[] args)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var logPath = Path.Combine(baseDirectory, "launcher.log");
        var readyPath = Path.Combine(baseDirectory, "bridge.ready.json");

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

        if (args.Any(arg => arg.Equals("--background", StringComparison.OrdinalIgnoreCase)))
            return EnsureResidentBridge(baseDirectory, readyPath, Log);

        var gameExecutable = ResolveGameExecutable(args, baseDirectory);
        if (gameExecutable is null)
        {
            Log("DevilMayCry5.exe was not supplied by Steam and could not be found.");
            return 2;
        }

        var gameArguments = ResolveGameArguments(args, gameExecutable);
        Process? bridge = null;
        var ownsBridge = false;
        var activeBridgePid = 0;
        var settings = LoadSettings(baseDirectory);
        var requireAdvancedHaptics = settings.EnableAdvancedHaptics;
        var requireVirtualXInput = settings.EnableVirtualXInput;

        try
        {
            var ready = ReadLiveBridgeReady(readyPath);
            if (ready is not null)
            {
                activeBridgePid = ready.Pid;
                Log($"Reusing live bridge PID {ready.Pid} (resident={ready.Resident}).");
                ready = WaitForBridgeReady(readyPath, ready.Pid, TimeSpan.FromSeconds(6),
                    requireAdvancedHaptics, requireVirtualXInput);
            }
            else
            {
                TryDelete(readyPath);
                TryDelete(readyPath + ".tmp");
                bridge = StartBridge(baseDirectory, Log, resident: false);
                ownsBridge = bridge is not null;
                activeBridgePid = bridge?.Id ?? 0;
                if (bridge is not null)
                    ready = WaitForBridgeReady(readyPath, bridge.Id, TimeSpan.FromSeconds(6),
                        requireAdvancedHaptics, requireVirtualXInput);
            }

            if (activeBridgePid == 0)
                Log("Bridge could not be started; DMC5 will still be launched.");
            else
            {
                if (ready is null)
                    Log("Bridge readiness timed out; DMC5 will still be launched.");
                else
                    Log($"Bridge ready: controller={ready.ControllerReady}, " +
                        $"advancedHaptics={ready.AdvancedHapticsReady}, " +
                        $"virtualXInput={ready.VirtualXInputReady}, " +
                        $"directInput={ready.DirectInputReady}, {ready.Description}");
            }

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

            Log($"DMC5 started as PID {game.Id}; bridge PID " +
                (activeBridgePid == 0 ? "none" : activeBridgePid.ToString(CultureInfo.InvariantCulture)) + ".");
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
            if (ownsBridge)
            {
                StopExistingBridge();
                try
                {
                    if (bridge is not null && !bridge.WaitForExit(2000))
                        bridge.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The bridge may already have exited through its parent monitor.
                }
                bridge?.Dispose();
                TryDelete(readyPath);
            }
        }
    }

    private static int EnsureResidentBridge(
        string baseDirectory,
        string readyPath,
        Action<string> log)
    {
        var existing = ReadLiveBridgeReady(readyPath);
        if (existing?.Resident == true)
        {
            log($"Resident bridge already running as PID {existing.Pid}: " +
                $"controller={existing.ControllerReady}, advancedHaptics={existing.AdvancedHapticsReady}, " +
                $"virtualXInput={existing.VirtualXInputReady}, directInput={existing.DirectInputReady}.");
            return 0;
        }

        if (existing is not null)
        {
            log($"A session bridge is already running as PID {existing.Pid}; " +
                "it cannot be replaced while DMC5 is active.");
            return 5;
        }

        TryDelete(readyPath);
        TryDelete(readyPath + ".tmp");
        using var bridge = StartBridge(baseDirectory, log, resident: true);
        if (bridge is null) return 2;

        var settings = LoadSettings(baseDirectory);
        var ready = WaitForBridgeReady(readyPath, bridge.Id, TimeSpan.FromSeconds(8),
            settings.EnableAdvancedHaptics, settings.EnableVirtualXInput);
        if (ready is null)
        {
            log("Resident bridge readiness timed out.");
            return 3;
        }

        log($"Resident bridge ready as PID {ready.Pid}: controller={ready.ControllerReady}, " +
            $"advancedHaptics={ready.AdvancedHapticsReady}, virtualXInput={ready.VirtualXInputReady}, " +
            $"directInput={ready.DirectInputReady}, {ready.Description}");
        return ready.ControllerReady &&
               (!settings.EnableAdvancedHaptics || ready.AdvancedHapticsReady) &&
               (!settings.EnableVirtualXInput ||
                (ready.VirtualXInputReady && ready.DirectInputReady))
            ? 0
            : 4;
    }

    private static Process? StartBridge(string baseDirectory, Action<string> log, bool resident)
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
            Arguments = resident
                ? "--resident"
                : "--parent " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
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

    private static BridgeReady? ReadLiveBridgeReady(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var status = JsonSerializer.Deserialize<BridgeReady>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (status is null || status.Pid <= 0 || status.Utc == default ||
                DateTime.UtcNow - status.Utc > TimeSpan.FromSeconds(5)) return null;
            using var process = Process.GetProcessById(status.Pid);
            return process.HasExited ||
                   !process.ProcessName.Equals("DMC5DualSense.Bridge", StringComparison.OrdinalIgnoreCase)
                ? null
                : status;
        }
        catch
        {
            return null;
        }
    }

    private static BridgeReady? WaitForBridgeReady(
        string path,
        int processId,
        TimeSpan timeout,
        bool requireAdvancedHaptics,
        bool requireVirtualXInput)
    {
        var deadline = DateTime.UtcNow + timeout;
        BridgeReady? latest = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var status = JsonSerializer.Deserialize<BridgeReady>(File.ReadAllText(path),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (status?.Pid == processId)
                    {
                        latest = status;
                        if (status.ControllerReady &&
                            (!requireAdvancedHaptics || status.AdvancedHapticsReady) &&
                            (!requireVirtualXInput ||
                             (status.VirtualXInputReady && status.DirectInputReady)))
                            return status;
                    }
                }
            }
            catch
            {
                // The bridge writes the small file atomically enough for the next poll to succeed.
            }
            Thread.Sleep(50);
        }
        return latest;
    }

    private static LauncherSettings LoadSettings(string baseDirectory)
    {
        try
        {
            var path = Path.Combine(baseDirectory, "config.json");
            if (!File.Exists(path)) return new LauncherSettings();
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new LauncherSettings();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private sealed class BridgeReady
    {
        public int Pid { get; set; }
        public bool Resident { get; set; }
        public bool ControllerReady { get; set; }
        public bool AdvancedHapticsReady { get; set; }
        public bool VirtualXInputReady { get; set; }
        public bool DirectInputReady { get; set; }
        public string Description { get; set; } = "";
        public DateTime Utc { get; set; }
    }

    private sealed class LauncherSettings
    {
        public bool EnableAdvancedHaptics { get; set; } = true;
        public bool EnableVirtualXInput { get; set; } = true;
    }
}
