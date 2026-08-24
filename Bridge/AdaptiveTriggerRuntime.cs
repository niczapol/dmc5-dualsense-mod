namespace DMC5DualSense.Bridge;

internal sealed class AdaptiveTriggerRuntime
{
    private enum TriggerSide
    {
        None,
        Left,
        Right
    }

    private readonly object _gate = new();

    // DMC5's stock mapping puts Exceed on L2. AttackL is normally a face button,
    // so its adaptive effect intentionally remains disabled until an actual
    // trigger press proves that the player remapped the action to L2 or R2.
    private TriggerSide _exceedSide = TriggerSide.Left;
    private TriggerSide _attackLargeSide;

    public string ExceedMapping
    {
        get { lock (_gate) return _exceedSide.ToString(); }
    }

    public string AttackLargeMapping
    {
        get { lock (_gate) return _attackLargeSide.ToString(); }
    }

    public void OnEvent(string eventName, XInputSnapshot input)
    {
        lock (_gate)
        {
            switch (eventName.ToLowerInvariant())
            {
                case "exceed_input":
                    LearnSide(ref _exceedSide, input, 0.08f);
                    break;

                case "gun_charge_start":
                case "gun_charge_level":
                case "blue_rose_shot":
                case "dante_gun_input":
                case "dante_gun_shot":
                case "dante_ebony_shot":
                case "dante_ivory_shot":
                case "dante_coyote_shot":
                    LearnSide(ref _attackLargeSide, input, 0.55f);
                    break;
            }
        }
    }

    public (TriggerEffect Left, TriggerEffect Right) Build(
        GameState state,
        BridgeConfig config,
        XInputSnapshot input)
    {
        if (!config.EnableAdaptiveTriggers || !state.IsFresh || !state.InGameplay)
            return (TriggerEffect.Off, TriggerEffect.Off);

        lock (_gate)
        {
            var left = TriggerEffect.Off;
            var right = TriggerEffect.Off;
            var strength = Math.Clamp(config.TriggerStrength, 0f, 1f);

            switch (state.Character.ToLowerInvariant())
            {
                case "nero":
                {
                    // PS5 app.PlayerNero::setNeroAdaptiveTriger, action Special2:
                    // power=max(analog*0.5,0.2), frequency=0.3, start=0, end=1.
                    // VendorNativeDualSenseDevice turns that into effect 0x26,
                    // position 0, amplitude 1..4 and frequency 76.
                    var analog = Read(_exceedSide, input);
                    var amplitude = (byte)Math.Clamp(
                        (int)(Math.Max(analog * 0.5f, 0.2f) * 8f), 1, 4);
                    Apply(ref left, ref right, _exceedSide,
                        TriggerEffect.Vibration(0, ScaleLevel(amplitude, strength), 76));

                    // PS5 action AttackL / Blue Rose: power=.5, frequency=0,
                    // start=.5, end=.9 -> exact Weapon(4,8,4).
                    Apply(ref left, ref right, _attackLargeSide,
                        TriggerEffect.Weapon(4, 8, ScaleLevel(4, strength)));
                    break;
                }

                case "dante":
                {
                    var effect = DanteAttackLargeEffect(state.DanteWeaponId, strength);
                    Apply(ref left, ref right, _attackLargeSide, effect);
                    break;
                }

                // The PS5 implementation has no adaptive-trigger branch for V
                // or playable Vergil. Their triggers remain in the Off state.
                case "v":
                case "vergil":
                    break;
            }

            return (left, right);
        }
    }

    private static TriggerEffect DanteAttackLargeEffect(int weaponId, float strength) =>
        weaponId switch
        {
            // PS5 setDanteAdaptiveTriger jump-table results after conversion by
            // VendorNativeDualSenseDevice::updateDerived.
            0 => TriggerEffect.Weapon(4, 5, ScaleLevel(4, strength)),
            1 => TriggerEffect.Weapon(4, 8, ScaleLevel(4, strength)),
            2 or 3 or 4 => TriggerEffect.Weapon(2, 8, ScaleLevel(5, strength)),
            5 => TriggerEffect.Vibration(0, ScaleLevel(4, strength), 76),
            _ => TriggerEffect.Off
        };

    private static void LearnSide(
        ref TriggerSide side,
        XInputSnapshot input,
        float threshold)
    {
        if (!input.Connected) return;
        if (input.LeftTrigger < threshold && input.RightTrigger < threshold) return;
        if (Math.Abs(input.LeftTrigger - input.RightTrigger) < 0.12f) return;

        side = input.LeftTrigger > input.RightTrigger
            ? TriggerSide.Left
            : TriggerSide.Right;
    }

    private static float Read(TriggerSide side, XInputSnapshot input) => side switch
    {
        TriggerSide.Left => Math.Clamp(input.LeftTrigger, 0f, 1f),
        TriggerSide.Right => Math.Clamp(input.RightTrigger, 0f, 1f),
        _ => 0f
    };

    private static void Apply(
        ref TriggerEffect left,
        ref TriggerEffect right,
        TriggerSide side,
        TriggerEffect effect)
    {
        if (side == TriggerSide.Left) left = effect;
        if (side == TriggerSide.Right) right = effect;
    }

    private static byte ScaleLevel(byte level, float amount) =>
        (byte)Math.Clamp(
            (int)Math.Round(level * Math.Clamp(amount, 0f, 1f)),
            level == 0 ? 0 : 1,
            8);
}
