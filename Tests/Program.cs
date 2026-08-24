using DMC5DualSense.Bridge;

var failures = new List<string>();

Run("per-frame Dante gunCheck cannot learn a trigger", () =>
{
    var runtime = new AdaptiveTriggerRuntime();
    runtime.OnEvent("dante_gun_input", new XInputSnapshot(true, 0f, 1f));
    var output = runtime.Build(State("dante", 0), Config(), new XInputSnapshot(true, 0f, 1f));
    Equal(TriggerMode.Off, output.Right.Mode);
    Equal("None", runtime.DanteAttackLargeMapping);
});

Run("a real Dante shot learns the physical trigger", () =>
{
    var runtime = new AdaptiveTriggerRuntime();
    runtime.OnEvent("dante_ivory_shot", new XInputSnapshot(true, 0f, 1f));
    var output = runtime.Build(State("dante", 0), Config(), new XInputSnapshot(true, 0f, 1f));
    Equal(TriggerMode.Weapon, output.Right.Mode);
    Equal((byte)4, output.Right.Position);
    Equal((byte)5, output.Right.EndPosition);
    Equal("Right", runtime.DanteAttackLargeMapping);
});

Run("Nero and Dante AttackL mappings are isolated", () =>
{
    var runtime = new AdaptiveTriggerRuntime();
    runtime.OnEvent("dante_coyote_shot", new XInputSnapshot(true, 0f, 1f));
    var nero = runtime.Build(State("nero"), Config(), new XInputSnapshot(true, 0f, 1f));
    Equal(TriggerMode.Off, nero.Right.Mode);
    Equal("None", runtime.NeroAttackLargeMapping);
    Equal("Right", runtime.DanteAttackLargeMapping);
});

Run("EX-Act can recover a remapped Exceed side", () =>
{
    var runtime = new AdaptiveTriggerRuntime();
    runtime.OnEvent("ex_act", new XInputSnapshot(true, 0f, 0.9f));
    var output = runtime.Build(State("nero"), Config(), new XInputSnapshot(true, 0f, 0.9f));
    Equal(TriggerMode.Off, output.Left.Mode);
    Equal(TriggerMode.Vibration, output.Right.Mode);
    Equal("Right", runtime.ExceedMapping);
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
    0, 0, 0, false, 0, 0, 0, danteWeaponId, 0, 0, DateTime.UtcNow);
