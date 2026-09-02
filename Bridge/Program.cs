using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DMC5DualSense.Bridge;

internal static class Program
{
    private static readonly object StateGate = new();
    private static GameState _state = GameState.Empty;
    private static readonly CancellationTokenSource Shutdown = new();
    private static readonly AdaptiveTriggerRuntime AdaptiveTriggers = new();

    private static async Task<int> Main(string[] args)
    {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: "Local\\DMC5DualSense.Bridge",
            createdNew: out var isFirstInstance);
        if (!isFirstInstance) return 0;

        var baseDirectory = AppContext.BaseDirectory;
        var configPath = Path.Combine(baseDirectory, "config.json");
        var logPath = Path.Combine(baseDirectory, "bridge.log");
        var readyPath = Path.Combine(baseDirectory, "bridge.ready.json");
        var config = BridgeConfig.Load(configPath);

        void Log(string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            Console.WriteLine(line);
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
        }

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Shutdown.Cancel();
        };

        StartParentMonitor(args, Log);
        Log("Session bridge started for the current DMC5 process.");

        using IControllerOutputDevice controller = new SteamInputOutputDevice(baseDirectory);
        using var haptics = new HapticEngine(config.HapticsStrength);

        var foundController = controller.EnsureConnected();
        Log(foundController
            ? $"DualSense output connected through Steam Input: {controller.Description}"
            : $"DualSense Steam Input output is waiting: {controller.Description}");

        var xinput = XInputReader.ReadFirstConnected();
        Log(xinput.Connected
            ? $"Steam/XInput pad detected: LT={xinput.LeftTrigger:0.00}, RT={xinput.RightTrigger:0.00}."
            : "Steam/XInput pad is not currently exposed; action-aware trigger mapping will wait for it.");

        var audioStarted = false;
        if (config.EnableAdvancedHaptics)
        {
            audioStarted = haptics.Start(
                config.AudioDeviceContains,
                config.EnsureHapticsEndpointAudible,
                config.HapticsEndpointVolume);
            Log(audioStarted
                ? $"Advanced haptics audio: {haptics.Status}"
                : $"Advanced haptics unavailable: {haptics.Status}");
        }

        if (args.Any(arg => arg.Equals("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            controller.Reset();
            return foundController ? 0 : 2;
        }

        if (args.Any(arg => arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            if (!foundController) return 2;
            Log("Self-test: exact PS5 Exceed, Blue Rose trigger profile and Blue Rose HD haptic.");
            controller.Write(new ControllerOutput(
                TriggerEffect.Vibration(0, 4, 76),
                TriggerEffect.Off,
                0, 0, 220));
            await Task.Delay(700);
            controller.Write(new ControllerOutput(
                TriggerEffect.Weapon(4, 8, 4),
                TriggerEffect.Weapon(4, 8, 4),
                0, 0, 220));
            haptics.PlayOriginal("bluerose_shot_shell");
            await Task.Delay(2300);
            controller.Reset();
            Log("Self-test completed; all effects stopped and both triggers were released.");
            return 0;
        }

        if (args.Any(arg => arg.Equals("--self-test-all", StringComparison.OrdinalIgnoreCase)))
        {
            if (!foundController || !audioStarted) return 2;
            await RunFullPs5SelfTest(controller, haptics, Log);
            return 0;
        }

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, config.Port));
        Log($"Listening for DMC5 telemetry on 127.0.0.1:{config.Port}.");
        WriteReadyStatus(readyPath, foundController, audioStarted, controller.Description);

        var receiver = ReceiveLoop(udp, haptics, config, Log, true, Shutdown.Token);
        var output = OutputLoop(controller, haptics,
            config, readyPath, Log, Shutdown.Token);

        try
        {
            await Task.WhenAll(receiver, output);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            controller.Reset();
            TryDelete(readyPath);
            TryDelete(readyPath + ".tmp");
        }

        return 0;
    }

    private static void WriteReadyStatus(
        string path,
        bool controllerReady,
        bool advancedHapticsReady,
        string description)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                controllerReady,
                advancedHapticsReady,
                outputBackend = "SteamInput006",
                description,
                utc = DateTime.UtcNow
            });
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // The launcher also has a timeout; telemetry must work even if status cannot be written.
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static async Task RunFullPs5SelfTest(
        IControllerOutputDevice controller,
        HapticEngine haptics,
        Action<string> log)
    {
        var diagnostics = haptics.GetOriginalSampleDiagnostics();
        if (diagnostics.Count != 12)
            throw new InvalidOperationException(
                $"Expected 12 original PS5 haptic samples, found {diagnostics.Count}.");

        log("Full PS5 self-test: 4 lightbar colors, every exact adaptive-trigger profile, and all 12 HD haptic events.");
        foreach (var sample in diagnostics)
        {
            log($"PS5 haptic #{sample.Index}: {sample.Key}, {sample.FileName}, " +
                $"{sample.Channels}ch, {sample.DurationSeconds:0.000}s, gain {sample.GainDb:+0.##;-0.##;0} dB, " +
                $"pitch {sample.PitchCents:+0;-0;0} cents, delay {sample.DelaySeconds:0.000}s, " +
                $"loop={sample.Loop}, peak={sample.SourcePeak:0.0000}.");
        }

        var triggerTests = new[]
        {
            ("Nero Exceed", TriggerEffect.Vibration(0, 4, 76), TriggerEffect.Off, (byte)0, (byte)0, (byte)220),
            ("Nero Blue Rose", TriggerEffect.Off, TriggerEffect.Weapon(4, 8, 4), (byte)0, (byte)0, (byte)220),
            ("Dante weapon 0", TriggerEffect.Off, TriggerEffect.Weapon(4, 5, 4), (byte)195, (byte)0, (byte)0),
            ("Dante weapon 1", TriggerEffect.Off, TriggerEffect.Weapon(4, 8, 4), (byte)195, (byte)0, (byte)0),
            ("Dante weapons 2/3/4", TriggerEffect.Off, TriggerEffect.Weapon(2, 8, 5), (byte)195, (byte)0, (byte)0),
            ("Dante weapon 5", TriggerEffect.Off, TriggerEffect.Vibration(0, 4, 76), (byte)195, (byte)0, (byte)0),
            ("V (triggers off)", TriggerEffect.Off, TriggerEffect.Off, (byte)120, (byte)0, (byte)255),
            ("Vergil (triggers off)", TriggerEffect.Off, TriggerEffect.Off, (byte)0, (byte)255, (byte)160)
        };

        foreach (var test in triggerTests)
        {
            log($"Trigger/light test: {test.Item1}.");
            controller.Write(new ControllerOutput(
                test.Item2, test.Item3, test.Item4, test.Item5, test.Item6));
            await Task.Delay(550);
        }

        controller.Write(new ControllerOutput(
            TriggerEffect.Off, TriggerEffect.Off, 200, 200, 200));

        foreach (var sample in diagnostics)
        {
            log($"Playing original PS5 haptic #{sample.Index}: {sample.Key}.");
            haptics.StopOriginalHaptics();
            haptics.PlayOriginal(sample.Key);

            var playbackMilliseconds = sample.Loop
                ? 900
                : (int)Math.Clamp(
                    (sample.DurationSeconds + sample.DelaySeconds + 0.15) * 1000.0,
                    350.0,
                    2400.0);
            await Task.Delay(playbackMilliseconds);

            if (sample.Key == "mirage_sp_loop")
            {
                log("Ending Mirage special loop with its original PS5 end event.");
                haptics.PlayOriginal("mirage_sp_end");
                await Task.Delay(650);
            }
        }

        haptics.StopOriginalHaptics();
        controller.Reset();
        log("Full PS5 self-test completed; 12/12 samples were decoded and scheduled, all effects stopped, both triggers released.");
    }

    private static async Task ReceiveLoop(
        UdpClient udp,
        HapticEngine haptics,
        BridgeConfig config,
        Action<string> log,
        bool allowRemoteShutdown,
        CancellationToken cancellationToken)
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var lastCharacter = "";
        var windowStartedUtc = DateTime.UtcNow;
        var motorPackets = 0;
        var padShakePackets = 0;
        var events = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var originalHaptics = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        static void Increment(Dictionary<string, int> counters, string key)
        {
            counters[key] = counters.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        void FlushDiagnosticsIfDue()
        {
            var now = DateTime.UtcNow;
            if (now - windowStartedUtc < TimeSpan.FromSeconds(5)) return;
            var audio = haptics.GetAndResetRenderDiagnostic();
            if (config.EnableCalibrationLog &&
                (motorPackets > 0 || padShakePackets > 0 || events.Count > 0 ||
                 originalHaptics.Count > 0 || audio.NonZeroFrames > 0))
            {
                var eventSummary = string.Join(",", events.OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key}:{pair.Value}"));
                var hapticSummary = string.Join(",", originalHaptics.OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key}:{pair.Value}"));
                log($"Telemetry 5s: motor={motorPackets}, padshake={padShakePackets}, " +
                    $"events=[{eventSummary}], original=[{hapticSummary}], " +
                    $"audio={audio.NonZeroFrames}/{audio.Frames} frames, " +
                    $"limited={audio.LimitedFrames}, peak={audio.Peak:0.000}, " +
                    $"state={audio.PlaybackState}.");
            }

            motorPackets = 0;
            padShakePackets = 0;
            events.Clear();
            originalHaptics.Clear();
            windowStartedUtc = now;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var packet = await udp.ReceiveAsync(cancellationToken);
            BridgeMessage? message;

            try
            {
                message = JsonSerializer.Deserialize<BridgeMessage>(packet.Buffer, jsonOptions);
            }
            catch (JsonException ex)
            {
                log($"Ignored invalid telemetry packet: {ex.Message}");
                continue;
            }

            if (message is null || message.Version != 1) continue;

            switch (message.Type)
            {
                case "state":
                    lock (StateGate)
                    {
                        _state = new GameState(
                            message.Character,
                            message.InGameplay,
                            message.Health,
                            message.MaxHealth,
                            message.MotionBank,
                            message.MotionId,
                            message.MotionFrame,
                            message.ExceedGauge,
                            message.ExceedGaugeMax,
                            message.ExceedStock,
                            message.ExceedRequest,
                            message.ExceedRequestValue,
                            message.BlueRoseChargeLevel,
                            message.BlueRoseTimer,
                            message.DanteWeaponId,
                            message.AttackLargeButton,
                            message.Special2Button,
                            message.Left,
                            message.Right,
                            DateTime.UtcNow);
                    }

                    var mappingsBefore = (
                        AdaptiveTriggers.ExceedMapping,
                        AdaptiveTriggers.NeroAttackLargeMapping,
                        AdaptiveTriggers.DanteAttackLargeMapping);
                    AdaptiveTriggers.UpdateBindings(
                        message.Character,
                        message.AttackLargeButton,
                        message.Special2Button);
                    var mappingsAfter = (
                        AdaptiveTriggers.ExceedMapping,
                        AdaptiveTriggers.NeroAttackLargeMapping,
                        AdaptiveTriggers.DanteAttackLargeMapping);

                    if (mappingsAfter != mappingsBefore)
                    {
                        log($"Adaptive mapping read from DMC5 controls: " +
                            $"Exceed={mappingsAfter.Item1}, " +
                            $"NeroAttackL={mappingsAfter.Item2}, " +
                            $"DanteAttackL={mappingsAfter.Item3}, " +
                            $"AttackLButton=0x{message.AttackLargeButton:X}, " +
                            $"Special2Button=0x{message.Special2Button:X}.");
                    }

                    if (!message.Character.Equals(lastCharacter, StringComparison.OrdinalIgnoreCase))
                    {
                        lastCharacter = message.Character;
                        log($"Character detected: {lastCharacter}; HP {message.Health:0}/{message.MaxHealth:0}.");
                    }
                    break;

                case "rumble":
                    haptics.RumblePulse(message.Left, message.Right, message.Duration);
                    break;

                case "padshake":
                    padShakePackets++;
                    haptics.FromGamePadShake(
                        message.Motor,
                        Math.Clamp(message.Value, 0f, 1f),
                        message.Duration);
                    break;

                case "motor":
                    motorPackets++;
                    haptics.SetGameMotor(message.Motor, message.Value);
                    break;

                case "event" when message.Name.Equals("damage", StringComparison.OrdinalIgnoreCase):
                    if (config.AdaptiveProfile.Equals("Enhanced", StringComparison.OrdinalIgnoreCase))
                        haptics.Impact(Math.Clamp(message.Value, 0.15f, 1f));
                    break;

                case "event":
                    Increment(events, message.Name);
                    GameState latestState;
                    lock (StateGate) latestState = _state;

                    if (message.Name.Equals("weapon_hit", StringComparison.OrdinalIgnoreCase) &&
                        config.AdaptiveProfile.Equals("Enhanced", StringComparison.OrdinalIgnoreCase))
                        haptics.WeaponHit(latestState.Character,
                            message.Value > 0 ? Math.Clamp(message.Value, 0.2f, 1f) : 1f);
                    else if (!message.Name.Equals("exceed_input", StringComparison.OrdinalIgnoreCase) &&
                             !message.Name.Equals("ex_act", StringComparison.OrdinalIgnoreCase) &&
                             !message.Name.Equals("max_act", StringComparison.OrdinalIgnoreCase) &&
                             !message.Name.StartsWith("gun_charge_", StringComparison.OrdinalIgnoreCase) &&
                             haptics.PlayOriginal(message.Name))
                        Increment(originalHaptics, message.Name);

                    break;

                case "shutdown":
                    if (allowRemoteShutdown)
                    {
                        log("Session bridge received shutdown request.");
                        Shutdown.Cancel();
                    }
                    break;
            }

            FlushDiagnosticsIfDue();
        }
    }

    private static void StartParentMonitor(string[] args, Action<string> log)
    {
        var parentIndex = Array.FindIndex(args, arg =>
            arg.Equals("--parent", StringComparison.OrdinalIgnoreCase));
        if (parentIndex < 0 || parentIndex + 1 >= args.Length ||
            !int.TryParse(args[parentIndex + 1], out var parentId)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(parentId);
                await parent.WaitForExitAsync(Shutdown.Token);
                log("DMC5 exited; shutting down bridge.");
                Shutdown.Cancel();
            }
            catch (OperationCanceledException)
            {
            }
            catch (ArgumentException)
            {
                Shutdown.Cancel();
            }
            catch (Exception ex)
            {
                log($"Parent monitor unavailable: {ex.Message}");
            }
        });
    }

    private static async Task OutputLoop(
        IControllerOutputDevice controller,
        HapticEngine haptics,
        BridgeConfig config,
        string readyPath,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        var wasConnected = controller.Connected;
        var wasAudioReady = haptics.Started;
        var nextAudioRetryUtc = DateTime.MinValue;
        var nextStatusWriteUtc = DateTime.MinValue;
        var nextOutputDiagnosticUtc = DateTime.UtcNow.AddSeconds(5);

        while (!cancellationToken.IsCancellationRequested)
        {
            GameState snapshot;
            lock (StateGate) snapshot = _state;

            var stateForOutput = snapshot.IsFresh ? snapshot : GameState.Empty;
            var input = stateForOutput.IsFresh
                ? new XInputSnapshot(true, stateForOutput.TriggerLeft, stateForOutput.TriggerRight)
                : XInputReader.ReadFirstConnected();
            var triggerEffects = AdaptiveTriggers.Build(stateForOutput, config, input);
            var command = ProfileEngine.Build(
                stateForOutput,
                config,
                (DateTime.UtcNow - start).TotalSeconds,
                triggerEffects.Left,
                triggerEffects.Right);
            var rumble = haptics.GetRumbleOutput();
            command = command with
            {
                LeftRumble = rumble.Low,
                RightRumble = rumble.High
            };

            var connected = controller.Write(command);
            var audioReady = haptics.Started;
            if (config.EnableAdvancedHaptics && !audioReady &&
                DateTime.UtcNow >= nextAudioRetryUtc)
            {
                nextAudioRetryUtc = DateTime.UtcNow.AddSeconds(1);
                audioReady = haptics.Start(
                    config.AudioDeviceContains,
                    config.EnsureHapticsEndpointAudible,
                    config.HapticsEndpointVolume);
            }

            var statusChanged = connected != wasConnected || audioReady != wasAudioReady;

            if (connected != wasConnected)
            {
                log(connected
                    ? $"DualSense Steam Input output connected: {controller.Description}"
                    : $"DualSense Steam Input output disconnected: {controller.Description}");
                wasConnected = connected;
            }

            if (audioReady != wasAudioReady)
            {
                log(audioReady
                    ? $"Advanced haptics audio reconnected: {haptics.Status}"
                    : $"Advanced haptics audio disconnected: {haptics.Status}");
                wasAudioReady = audioReady;
            }

            // Refresh periodically as well as on transitions so launchers can
            // reject a stale status file left by a crashed process.
            if (statusChanged || DateTime.UtcNow >= nextStatusWriteUtc)
            {
                nextStatusWriteUtc = DateTime.UtcNow.AddSeconds(2);
                WriteReadyStatus(readyPath, connected, audioReady, controller.Description);
            }

            if (config.EnableCalibrationLog && DateTime.UtcNow >= nextOutputDiagnosticUtc)
            {
                nextOutputDiagnosticUtc = DateTime.UtcNow.AddSeconds(5);
                var outputDiagnostic = controller.GetAndResetWriteDiagnostic();
                log($"Output 5s: SteamInput={outputDiagnostic.Successes}/{outputDiagnostic.Attempts}, " +
                    $"triggerWrites={outputDiagnostic.TriggerEffectWrites}, " +
                    $"rumbleWrites={outputDiagnostic.RumbleWrites}.");
            }

            await Task.Delay(33, cancellationToken);
        }
    }
}
