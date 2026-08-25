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

Run("neutral USB DualSense input maps to neutral XInput", () =>
{
    var bytes = NeutralDualSenseReport();
    Equal(true, DualSenseInputReport.TryParse(bytes, out var input));
    Equal((ushort)0, input.Buttons);
    Equal((byte)0, input.LeftTrigger);
    Equal((byte)0, input.RightTrigger);
    Equal((short)0, input.LeftThumbX);
    Equal((short)0, input.LeftThumbY);
    Equal((short)0, input.RightThumbX);
    Equal((short)0, input.RightThumbY);
});

Run("DualSense controls map atomically to the expected Xbox report", () =>
{
    var bytes = NeutralDualSenseReport();
    bytes[1] = 0;
    bytes[2] = 0;
    bytes[3] = 255;
    bytes[4] = 255;
    bytes[5] = 73;
    bytes[6] = 201;
    bytes[8] = 0x21; // Cross + up/right
    bytes[9] = 0xA3; // L1, R1, Options, R3
    bytes[10] = 0x03; // PS and touchpad click

    Equal(true, DualSenseInputReport.TryParse(bytes, out var input));
    Equal((ushort)0x17B9, input.Buttons);
    Equal((byte)73, input.LeftTrigger);
    Equal((byte)201, input.RightTrigger);
    Equal(short.MinValue, input.LeftThumbX);
    Equal(short.MaxValue, input.LeftThumbY);
    Equal(short.MaxValue, input.RightThumbX);
    Equal((short)-32767, input.RightThumbY);
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

static byte[] NeutralDualSenseReport()
{
    var bytes = new byte[64];
    bytes[0] = 0x01;
    bytes[1] = 128;
    bytes[2] = 128;
    bytes[3] = 128;
    bytes[4] = 128;
    bytes[8] = 0x08;
    return bytes;
}

static GameState State(string character, int danteWeaponId = -1) => new(
    character, true, 100, 100, 0, 0, 0,
    0, 0, 0, false, 0, 0, 0, danteWeaponId, -1, -1, 0, 0, DateTime.UtcNow);
