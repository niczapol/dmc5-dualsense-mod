namespace DMC5DualSense.Bridge;

internal enum TriggerMode : byte
{
    Off = 0x05,
    Feedback = 0x21,
    Weapon = 0x25,
    Vibration = 0x26
}

internal readonly record struct TriggerEffect(
    TriggerMode Mode,
    byte Position,
    byte Strength,
    byte EndPosition = 0,
    byte Frequency = 0)
{
    public static TriggerEffect Off => new(TriggerMode.Off, 0, 0);

    public static TriggerEffect Feedback(byte position, byte strength) =>
        new(TriggerMode.Feedback, position, strength);

    public static TriggerEffect Weapon(byte startPosition, byte endPosition, byte strength) =>
        new(TriggerMode.Weapon, startPosition, strength, endPosition);

    public static TriggerEffect Vibration(byte position, byte amplitude, byte frequency) =>
        new(TriggerMode.Vibration, position, amplitude, Frequency: frequency);
}

internal readonly record struct ControllerOutput(
    TriggerEffect LeftTrigger,
    TriggerEffect RightTrigger,
    byte Red,
    byte Green,
    byte Blue,
    byte PlayerLeds = 0x04,
    byte LeftRumble = 0,
    byte RightRumble = 0);

internal readonly record struct ControllerWriteDiagnostic(
    long Attempts,
    long Successes,
    long TriggerEffectWrites,
    long RumbleWrites);

internal interface IControllerOutputDevice : IDisposable
{
    bool Connected { get; }
    string Description { get; }
    bool EnsureConnected();
    bool Write(ControllerOutput output);
    ControllerWriteDiagnostic GetAndResetWriteDiagnostic();
    void Reset();
}
