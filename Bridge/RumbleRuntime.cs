namespace DMC5DualSense.Bridge;

internal readonly record struct RumbleOutput(byte Low, byte High);

internal sealed class RumbleRuntime
{
    private readonly float _strength;
    private readonly TimedMotor[] _motors =
        Enumerable.Range(0, 4).Select(_ => new TimedMotor()).ToArray();
    private readonly TransientMotor _transientLow = new();
    private readonly TransientMotor _transientHigh = new();
    private DateTime _lastMotorSignalUtc;

    public RumbleRuntime(float strength = 1f) =>
        _strength = Math.Clamp(strength, 0f, 1f);

    public void SetGameMotor(int motor, float power, DateTime? nowUtc = null)
    {
        var index = NormalizeMotor(motor);
        if (index < 0) return;
        var now = nowUtc ?? DateTime.UtcNow;
        _motors[index].Power = Math.Clamp(power, 0f, 1f);
        _motors[index].UntilUtc = now.AddMilliseconds(180);
        _lastMotorSignalUtc = now;
    }

    public bool HasRecentGameMotor(TimeSpan age, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        return _lastMotorSignalUtc != default && now - _lastMotorSignalUtc < age;
    }

    public void Pulse(float low, float high, float durationSeconds, DateTime? nowUtc = null)
    {
        low = Math.Clamp(low, 0f, 1f);
        high = Math.Clamp(high, 0f, 1f);
        durationSeconds = Math.Clamp(durationSeconds <= 0 ? 0.08f : durationSeconds, 0.025f, 1.5f);
        var now = nowUtc ?? DateTime.UtcNow;
        var until = now.AddSeconds(durationSeconds);
        UpdateTransient(_transientLow, low, now, until);
        UpdateTransient(_transientHigh, high, now, until);
    }

    public RumbleOutput GetOutput(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var low = Math.Max(Active(0, now), Active(2, now) * 0.55f);
        var high = Math.Max(Active(1, now), Active(3, now) * 0.55f);
        low = Math.Max(low, TransientValue(_transientLow, now)) * _strength;
        high = Math.Max(high, TransientValue(_transientHigh, now)) * _strength;
        return new RumbleOutput(
            (byte)Math.Clamp((int)Math.Round(low * 255f), 0, 255),
            (byte)Math.Clamp((int)Math.Round(high * 255f), 0, 255));
    }

    private float Active(int index, DateTime nowUtc) =>
        nowUtc < _motors[index].UntilUtc ? _motors[index].Power : 0f;

    private static int NormalizeMotor(int motor)
    {
        if (motor is >= 128 and <= 131) motor -= 128;
        return motor is >= 0 and < 4 ? motor : -1;
    }

    private static void UpdateTransient(
        TransientMotor motor,
        float power,
        DateTime nowUtc,
        DateTime untilUtc)
    {
        if (power <= 0f) return;
        var current = TransientValue(motor, nowUtc);
        motor.Power = Math.Max(current, power);
        motor.StartUtc = nowUtc;
        if (untilUtc > motor.UntilUtc) motor.UntilUtc = untilUtc;
    }

    private static float TransientValue(TransientMotor motor, DateTime nowUtc)
    {
        if (motor.UntilUtc == default || nowUtc >= motor.UntilUtc)
        {
            motor.Power = 0f;
            return 0f;
        }

        var total = Math.Max(0.001, (motor.UntilUtc - motor.StartUtc).TotalSeconds);
        var remaining = Math.Clamp((motor.UntilUtc - nowUtc).TotalSeconds / total, 0.0, 1.0);
        return motor.Power * (float)Math.Sqrt(remaining);
    }

    private sealed class TimedMotor
    {
        public float Power;
        public DateTime UntilUtc;
    }

    private sealed class TransientMotor
    {
        public float Power;
        public DateTime StartUtc;
        public DateTime UntilUtc;
    }
}

internal static class HapticOutputSafety
{
    private const double Knee = 0.90;

    public static double SoftLimit(double value)
    {
        if (!double.IsFinite(value)) return 0.0;
        var magnitude = Math.Abs(value);
        if (magnitude <= Knee) return value;
        var limited = Knee + (1.0 - Knee) *
            (1.0 - Math.Exp(-(magnitude - Knee) / (1.0 - Knee)));
        return Math.CopySign(Math.Min(limited, 0.999969), value);
    }

    public static RumbleOutput Arbitrate(RumbleOutput ordinary, bool advancedHapticsActive) =>
        advancedHapticsActive ? default : ordinary;
}
