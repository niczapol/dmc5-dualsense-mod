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
    private TriggerSide _neroAttackLargeSide;
    private TriggerSide _danteAttackLargeSide;
    private TriggerSide _neroAttackLargeCandidate;
    private TriggerSide _danteAttackLargeCandidate;
    private int _neroAttackLargeCandidateVotes;
    private int _danteAttackLargeCandidateVotes;

    public string ExceedMapping
    {
        get { lock (_gate) return _exceedSide.ToString(); }
    }

    public string NeroAttackLargeMapping
    {
        get { lock (_gate) return _neroAttackLargeSide.ToString(); }
    }

    public string DanteAttackLargeMapping
    {
        get { lock (_gate) return _danteAttackLargeSide.ToString(); }
    }

    public void OnEvent(string eventName, XInputSnapshot input)
    {
        lock (_gate)
        {
            switch (eventName.ToLowerInvariant())
            {
                case "exceed_input":
                case "ex_act":
                case "max_act":
                    LearnSide(ref _exceedSide, input, 0.08f);
                    break;

                case "gun_charge_start":
                case "gun_charge_level":
                case "blue_rose_shot":
                    LearnStableSide(
                        ref _neroAttackLargeSide,
                        ref _neroAttackLargeCandidate,
                        ref _neroAttackLargeCandidateVotes,
                        input,
                        0.55f);
                    break;

                case "dante_ebony_shot":
                case "dante_ivory_shot":
                case "dante_coyote_shot":
                    LearnStableSide(
                        ref _danteAttackLargeSide,
                        ref _danteAttackLargeCandidate,
                        ref _danteAttackLargeCandidateVotes,
                        input,
                        0.55f);
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
                    Apply(ref left, ref right, _neroAttackLargeSide,
                        TriggerEffect.Weapon(4, 8, ScaleLevel(4, strength)));
                    break;
                }

                case "dante":
                {
                    var effect = DanteAttackLargeEffect(state.DanteWeaponId, strength);
                    Apply(ref left, ref right, _danteAttackLargeSide, effect);
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
        var detected = DetectSide(input, threshold);
        if (detected != TriggerSide.None) side = detected;
    }

    private static void LearnStableSide(
        ref TriggerSide side,
        ref TriggerSide candidate,
        ref int candidateVotes,
        XInputSnapshot input,
        float threshold)
    {
        // AttackL is a face button in the stock layout. One coincidental L2/R2
        // press during a shot therefore cannot prove that the action was remapped.
        // Require three consecutive matching shot/charge observations, then lock
        // the side for the bridge session so ordinary weapon-switch presses cannot
        // move an already established adaptive effect between the triggers.
        if (side != TriggerSide.None) return;

        var detected = DetectSide(input, threshold);
        if (detected == TriggerSide.None)
        {
            candidate = TriggerSide.None;
            candidateVotes = 0;
            return;
        }

        if (candidate != detected)
        {
            candidate = detected;
            candidateVotes = 1;
            return;
        }

        candidateVotes++;
        if (candidateVotes < 3) return;

        side = candidate;
        candidate = TriggerSide.None;
        candidateVotes = 0;
    }

    private static TriggerSide DetectSide(XInputSnapshot input, float threshold)
    {
        if (!input.Connected) return TriggerSide.None;
        if (input.LeftTrigger < threshold && input.RightTrigger < threshold)
            return TriggerSide.None;
        if (Math.Abs(input.LeftTrigger - input.RightTrigger) < 0.12f)
            return TriggerSide.None;

        return input.LeftTrigger > input.RightTrigger
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
