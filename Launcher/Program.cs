using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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

        HideParentCommandWindow(Log);

        if (args.Any(arg => arg.Equals("--background", StringComparison.OrdinalIgnoreCase)))
        {
            Log("Permanent background mode is no longer supported; launch DMC5 normally through Steam.");
            return 2;
        }

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

        try
        {
            if (Process.GetProcessesByName("DevilMayCry5").Any(process => !process.HasExited))
            {
                Log("DMC5 is already running; no second mod session was started.");
                return 0;
            }

            var existing = ReadLiveBridgeReady(readyPath);
            if (existing is not null)
            {
                Log($"Stopping orphaned bridge PID {existing.Pid} before the new game session.");
                StopExistingBridge();
                try
                {
                    using var oldBridge = Process.GetProcessById(existing.Pid);
                    if (!oldBridge.WaitForExit(1500)) oldBridge.Kill(entireProcessTree: true);
                }
                catch
                {
                    // It may have exited after the shutdown packet.
                }
            }

            TryDelete(readyPath);
            TryDelete(readyPath + ".tmp");
            bridge = StartBridge(baseDirectory, Log);
            ownsBridge = bridge is not null;
            activeBridgePid = bridge?.Id ?? 0;
            var ready = bridge is null
                ? null
                : WaitForBridgeReady(readyPath, bridge.Id, TimeSpan.FromSeconds(3),
                    requireAdvancedHaptics);

            if (activeBridgePid == 0)
                Log("Bridge could not be started; DMC5 will still be launched.");
            else
            {
                if (ready is null)
                    Log("Bridge readiness timed out; DMC5 will still be launched.");
                else
                    Log($"Bridge ready: controller={ready.ControllerReady}, " +
                        $"advancedHaptics={ready.AdvancedHapticsReady}, " +
                        $"output={ready.OutputBackend}, {ready.Description}");
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

    private static void HideParentCommandWindow(Action<string> log)
    {
        try
        {
            var information = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(
                Process.GetCurrentProcess().Handle,
                processInformationClass: 0,
                ref information,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);
            if (status != 0 || information.InheritedFromUniqueProcessId == IntPtr.Zero)
                return;

            var parentId = information.InheritedFromUniqueProcessId.ToInt32();
            using var parent = Process.GetProcessById(parentId);
            log($"Launcher parent is {parent.ProcessName}.exe PID {parentId}.");
            if (!parent.ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase) &&
                !parent.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase) &&
                !parent.ProcessName.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
                return;

            // Steam expands a launch option containing %command% through cmd.exe.
            // That console remains visible while this launcher deliberately waits
            // for DMC5 so it can tear the session bridge down cleanly. Attach only
            // long enough to obtain the console HWND, hide it, then detach again.
            var attachedHere = AttachConsole((uint)parentId);
            try
            {
                var window = GetConsoleWindow();
                if (window == IntPtr.Zero)
                {
                    parent.Refresh();
                    window = parent.MainWindowHandle;
                }

                if (window == IntPtr.Zero)
                {
                    log($"Parent {parent.ProcessName}.exe PID {parentId} has no visible console window.");
                    return;
                }

                var hideRequested = ShowWindowAsync(window, ShowWindowHide);
                log($"Parent command window hide requested for {parent.ProcessName}.exe PID {parentId}: " +
                    $"hwnd=0x{window.ToInt64():X}, accepted={hideRequested}.");
            }
            finally
            {
                if (attachedHere) FreeConsole();
            }
        }
        catch (Exception ex)
        {
            // Window cleanup is cosmetic and must never block game startup.
            log("Could not hide the parent command window: " + ex.Message);
        }
    }

    private const int ShowWindowHide = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

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
        bool requireAdvancedHaptics)
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
                        if (!requireAdvancedHaptics || status.AdvancedHapticsReady)
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
        public bool ControllerReady { get; set; }
        public bool AdvancedHapticsReady { get; set; }
        public string OutputBackend { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime Utc { get; set; }
    }

    private sealed class LauncherSettings
    {
        public bool EnableAdvancedHaptics { get; set; } = true;
    }
}
