using DMC5DualSense.Bridge;

var failures = new List<string>();

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
