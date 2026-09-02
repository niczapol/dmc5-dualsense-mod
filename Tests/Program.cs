using DMC5DualSense.Bridge;

var failures = new List<string>();

Run("localized CFI-ZCT1/CFI-ZCT2 audio endpoint is accepted", () =>
{
    Equal(true, HapticEngine.MatchesAudioEndpointName(
        "Динамики (DualSense Wireless Controller)",
        "DualSense Wireless Controller"));
});

Run("DualSense Edge audio endpoint is accepted by family name", () =>
{
    Equal(true, HapticEngine.MatchesAudioEndpointName(
        "Speakers (DualSense Edge Wireless Controller)",
        "DualSense Wireless Controller"));
});

Run("unrelated audio endpoint is rejected", () =>
{
    Equal(false, HapticEngine.MatchesAudioEndpointName(
        "Speakers (Realtek(R) Audio)",
        "DualSense Wireless Controller"));
});

Run("renamed DualSense endpoint is accepted by USB hardware identity", () =>
{
    var match = HapticEngine.ClassifyAudioEndpoint(
        "My custom controller audio",
        "DualSense Wireless Controller",
        @"{1}.USB\VID_054C&PID_0CE6&MI_00\6&ABC&0&0000",
        @"{2}.\\?\usb#vid_054c&pid_0ce6&mi_00#...",
        4);
    Equal(1200, match.Score);
});

Run("future Sony four-channel controller does not require a known product id", () =>
{
    var match = HapticEngine.ClassifyAudioEndpoint(
        "Renamed gamepad",
        "DualSense Wireless Controller",
        @"USB\VID_054C&PID_FFFF&MI_00\...",
        "",
        4);
    Equal(900, match.Score);
});

Run("unrelated four-channel endpoint is rejected without Sony hardware identity", () =>
{
    var match = HapticEngine.ClassifyAudioEndpoint(
        "Surround speakers",
        "DualSense Wireless Controller",
        @"USB\VID_1234&PID_5678\...",
        "",
        8);
    Equal(0, match.Score);
});

Run("Dante face-button gun mapping keeps both triggers free", () =>
{
    var runtime = new AdaptiveTriggerRuntime();
    runtime.UpdateBindings("dante", attackLargeButton: 0x20, special2Button: 0);
    var output = runtime.Build(State("dante", 0), Config(), new XInputSnapshot(true, 0f, 1f));
    Equal(TriggerMode.Off, output.Right.Mode);
    Equal("None", runtime.DanteAttackLargeMapping);
});

Run("repeated shots while R2 is held cannot alter the DMC5 binding", () =>
{
    var runtime = new AdaptiveTriggerRuntime();
    runtime.UpdateBindings("dante", attackLargeButton: 0x20, special2Button: 0);
    for (var i = 0; i < 20; i++)
        runtime.Build(State("dante", 0), Config(), new XInputSnapshot(true, 0f, 1f));
    Equal("None", runtime.DanteAttackLargeMapping);
    Equal(TriggerMode.Off, runtime.Build(
        State("dante", 0), Config(), new XInputSnapshot(true, 0f, 1f)).Right.Mode);
});

Run("Dante gun resistance follows an explicit R2 remap", () =>
{
    var runtime = new AdaptiveTriggerRuntime();
    runtime.UpdateBindings("dante", attackLargeButton: 0x0800, special2Button: 0);
    var output = runtime.Build(State("dante", 0), Config(), new XInputSnapshot(true, 0f, 0f));
    Equal(TriggerMode.Weapon, output.Right.Mode);
    Equal((byte)4, output.Right.Position);
    Equal((byte)5, output.Right.EndPosition);
    Equal("Right", runtime.DanteAttackLargeMapping);
});

Run("Dante remap changes take effect without restarting", () =>
{
    var runtime = new AdaptiveTriggerRuntime();
    runtime.UpdateBindings("dante", attackLargeButton: 0x0800, special2Button: 0);
    Equal(TriggerMode.Weapon, runtime.Build(
        State("dante", 1), Config(), new XInputSnapshot(true, 0f, 0f)).Right.Mode);

    runtime.UpdateBindings("dante", attackLargeButton: 0x0200, special2Button: 0);
    var output = runtime.Build(State("dante", 1), Config(), new XInputSnapshot(true, 0f, 0f));
    Equal(TriggerMode.Weapon, output.Left.Mode);
    Equal(TriggerMode.Off, output.Right.Mode);
    Equal("Left", runtime.DanteAttackLargeMapping);
});

Run("Nero bindings are read independently from the active layout", () =>
{
    var runtime = new AdaptiveTriggerRuntime();
    runtime.UpdateBindings("nero", attackLargeButton: 0x0800, special2Button: 0x0200);
    var output = runtime.Build(State("nero"), Config(), new XInputSnapshot(true, 0f, 0.9f));
    Equal(TriggerMode.Vibration, output.Left.Mode);
    Equal(TriggerMode.Weapon, output.Right.Mode);
    Equal("Left", runtime.ExceedMapping);
    Equal("Right", runtime.NeroAttackLargeMapping);
});

Run("Steam Input trigger payload turns both DualSense triggers off", () =>
{
    var payload = SteamDualSenseTriggerPayload.Build(TriggerEffect.Off, TriggerEffect.Off);
    Equal(SteamDualSenseTriggerPayload.Size, payload.Length);
    Equal((byte)0x03, payload[0]);
    Equal(0, BitConverter.ToInt32(payload, SteamDualSenseTriggerPayload.LeftCommandOffset));
    Equal(0, BitConverter.ToInt32(payload, SteamDualSenseTriggerPayload.RightCommandOffset));
});

Run("Steam Input trigger payload preserves the PS5 vibration command layout", () =>
{
    var payload = SteamDualSenseTriggerPayload.Build(
        TriggerEffect.Vibration(1, 4, 76), TriggerEffect.Off);
    var data = SteamDualSenseTriggerPayload.LeftCommandOffset + 8;
    Equal(3, BitConverter.ToInt32(payload, SteamDualSenseTriggerPayload.LeftCommandOffset));
    Equal((byte)1, payload[data]);
    Equal((byte)4, payload[data + 1]);
    Equal((byte)76, payload[data + 2]);
});

Run("Steam Input trigger payload preserves independent PS5 weapon sides", () =>
{
    var payload = SteamDualSenseTriggerPayload.Build(
        TriggerEffect.Off, TriggerEffect.Weapon(4, 8, 5));
    var data = SteamDualSenseTriggerPayload.RightCommandOffset + 8;
    Equal(0, BitConverter.ToInt32(payload, SteamDualSenseTriggerPayload.LeftCommandOffset));
    Equal(2, BitConverter.ToInt32(payload, SteamDualSenseTriggerPayload.RightCommandOffset));
    Equal((byte)4, payload[data]);
    Equal((byte)8, payload[data + 1]);
    Equal((byte)5, payload[data + 2]);
});

Run("independent rumble watchdogs cannot extend a stale opposite motor", () =>
{
    var rumble = new RumbleRuntime();
    var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    rumble.SetGameMotor(0, 1f, start);
    rumble.SetGameMotor(1, 0.5f, start);
    var active = rumble.GetOutput(start.AddMilliseconds(100));
    rumble.SetGameMotor(0, 0f, start.AddMilliseconds(150));
    var expired = rumble.GetOutput(start.AddMilliseconds(200));
    Equal((byte)255, active.Low);
    Equal(true, active.High is >= 127 and <= 128);
    Equal(new RumbleOutput(0, 0), expired);
});

Run("trigger-motor aliases are isolated and attenuated", () =>
{
    var rumble = new RumbleRuntime();
    var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    rumble.SetGameMotor(130, 1f, start);
    var output = rumble.GetOutput(start.AddMilliseconds(10));
    Equal(true, output.Low is >= 139 and <= 141);
    Equal((byte)0, output.High);
});

Run("advanced-haptics limiter is transparent and bounded", () =>
{
    Equal(0.5, HapticOutputSafety.SoftLimit(0.5));
    var hot = HapticOutputSafety.SoftLimit(2.51);
    var negative = HapticOutputSafety.SoftLimit(-2.51);
    Equal(true, hot is > 0.99 and < 1.0);
    Equal(true, Math.Abs(hot + negative) < 0.000001);
});

Run("advanced haptics take exclusive actuator priority", () =>
{
    var ordinary = new RumbleOutput(180, 90);
    Equal(ordinary, HapticOutputSafety.Arbitrate(ordinary, false));
    Equal(new RumbleOutput(0, 0), HapticOutputSafety.Arbitrate(ordinary, true));
});

Run("ordinary DMC5 rumble never enters the advanced audio bus", () =>
{
    using var haptics = new HapticEngine(1f, loadOriginalSamples: false);
    haptics.SetGameMotor(0, 1f);
    var buffer = new byte[48_000 / 10 * 4 * sizeof(short)];
    haptics.Read(buffer, 0, buffer.Length);
    Equal(true, buffer.All(value => value == 0));
    Equal(true, haptics.GetRumbleOutput().Low > 0);
});

Run("overlapping advanced haptics are smoothly limited without int16 clipping", () =>
{
    using var haptics = new HapticEngine(1f, loadOriginalSamples: false);
    haptics.Pulse(1f, 1f, 0.5f);
    haptics.Pulse(1f, 1f, 0.5f);
    var buffer = new byte[48_000 / 2 * 4 * sizeof(short)];
    haptics.Read(buffer, 0, buffer.Length);
    var actuatorSamples = Enumerable.Range(0, buffer.Length / 2)
        .Where(index => index % 4 is 2 or 3)
        .Select(index => BitConverter.ToInt16(buffer, index * 2))
        .ToArray();
    Equal(true, actuatorSamples.Any(value => value != 0));
    Equal(true, actuatorSamples.All(value => value is > short.MinValue and < short.MaxValue));
    Equal(true, haptics.GetAndResetRenderDiagnostic().LimitedFrames > 0);
});

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("All deterministic DualSense logic tests passed.");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        failures.Add("FAIL " + name + ": " + ex.Message);
    }
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected {expected}, got {actual}");
}

static BridgeConfig Config() => new()
{
    AdaptiveProfile = "Authentic",
    EnableAdaptiveTriggers = true,
    TriggerStrength = 1f
};

static GameState State(string character, int danteWeaponId = -1) => new(
    character, true, 100, 100, 0, 0, 0,
    0, 0, 0, false, 0, 0, 0, danteWeaponId, -1, -1, 0, 0, DateTime.UtcNow);
