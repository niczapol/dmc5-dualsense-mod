using HidSharp;

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

internal sealed class DualSenseDevice : IDisposable
{
    private const int SonyVendorId = 0x054C;
    private static readonly int[] SupportedProductIds = [0x0CE6, 0x0DF2, 0x0E5F];

    private readonly object _gate = new();
    private HidStream? _stream;
    private HidDevice? _device;
    private DateTime _nextReconnectUtc = DateTime.MinValue;
    private string _lastError = "";

    public bool Connected
    {
        get { lock (_gate) return _stream is not null; }
    }

    public string Description
    {
        get
        {
            lock (_gate)
            {
                if (_device is null) return _lastError.Length == 0 ? "not connected" : _lastError;
                return $"VID_{_device.VendorID:X4}/PID_{_device.ProductID:X4}, output={_device.GetMaxOutputReportLength()} bytes";
            }
        }
    }

    public bool EnsureConnected()
    {
        lock (_gate)
        {
            if (_stream is not null) return true;
            if (DateTime.UtcNow < _nextReconnectUtc) return false;

            _nextReconnectUtc = DateTime.UtcNow.AddSeconds(1);
            _lastError = "DualSense not found";
            var candidatesSeen = 0;

            foreach (var productId in SupportedProductIds)
            {
                foreach (var candidate in DeviceList.Local.GetHidDevices(SonyVendorId, productId))
                {
                    try
                    {
                        candidatesSeen++;
                        if (candidate.GetMaxOutputReportLength() < 48) continue;
                        if (!candidate.TryOpen(out var stream))
                        {
                            _lastError = $"DualSense HID is present but busy (PID_{productId:X4}); " +
                                         "start the bridge before DMC5/Steam Input";
                            continue;
                        }

                        stream.WriteTimeout = 250;
                        stream.ReadTimeout = 250;
                        _device = candidate;
                        _stream = stream;
                        _lastError = "";
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.Message;
                    }
                }
            }

            if (candidatesSeen > 0 && _lastError == "DualSense not found")
                _lastError = $"{candidatesSeen} Sony HID interface(s) found, but none exposes a writable output report";

            return false;
        }
    }

    public bool Write(ControllerOutput output)
    {
        lock (_gate)
        {
            if (!EnsureConnected() || _stream is null || _device is null) return false;

            try
            {
                var length = Math.Max(48, _device.GetMaxOutputReportLength());
                var report = new byte[length];

                // USB DualSense output report. The first byte is the HID report ID;
                // the remaining layout follows Sony's 0x02 output report as documented
                // by the MIT-licensed DualSense-Windows reference implementation.
                report[0x00] = 0x02;
                report[0x01] = 0xFF;
                report[0x02] = 0xF7;
                report[0x03] = output.RightRumble;
                report[0x04] = output.LeftRumble;

                WriteTrigger(report, 0x0B, output.RightTrigger);
                WriteTrigger(report, 0x16, output.LeftTrigger);

                report[0x27] = 0x03;
                report[0x2A] = 0x02;
                report[0x2B] = 0x01;
                report[0x2C] = (byte)(output.PlayerLeds | 0x20);
                report[0x2D] = output.Red;
                report[0x2E] = output.Green;
                report[0x2F] = output.Blue;

                _stream.Write(report);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                DisconnectNoReset();
                return false;
            }
        }
    }

    private static void WriteTrigger(byte[] report, int offset, TriggerEffect effect)
    {
        Array.Clear(report, offset, 11);

        switch (effect.Mode)
        {
            case TriggerMode.Feedback:
                WriteFeedback(report, offset, effect);
                break;

            case TriggerMode.Weapon:
                WriteWeapon(report, offset, effect);
                break;

            case TriggerMode.Vibration:
                WriteVibration(report, offset, effect);
                break;

            default:
                report[offset] = (byte)TriggerMode.Off;
                break;
        }
    }

    private static void WriteFeedback(byte[] report, int offset, TriggerEffect effect)
    {
        var position = Math.Clamp(effect.Position, (byte)0, (byte)9);
        var strength = Math.Clamp(effect.Strength, (byte)0, (byte)8);
        if (strength == 0)
        {
            report[offset] = (byte)TriggerMode.Off;
            return;
        }

        ushort zones = 0;
        uint strengths = 0;
        var packedStrength = (byte)(strength - 1);
        for (var zone = position; zone < 10; zone++)
        {
            zones |= (ushort)(1 << zone);
            strengths |= (uint)(packedStrength & 0x07) << (zone * 3);
        }

        report[offset] = (byte)TriggerMode.Feedback;
        report[offset + 1] = (byte)(zones & 0xFF);
        report[offset + 2] = (byte)((zones >> 8) & 0xFF);
        report[offset + 3] = (byte)(strengths & 0xFF);
        report[offset + 4] = (byte)((strengths >> 8) & 0xFF);
        report[offset + 5] = (byte)((strengths >> 16) & 0xFF);
        report[offset + 6] = (byte)((strengths >> 24) & 0xFF);
    }

    private static void WriteWeapon(byte[] report, int offset, TriggerEffect effect)
    {
        var start = Math.Clamp(effect.Position, (byte)2, (byte)7);
        var end = Math.Clamp(effect.EndPosition, (byte)(start + 1), (byte)8);
        var strength = Math.Clamp(effect.Strength, (byte)0, (byte)8);
        if (strength == 0)
        {
            report[offset] = (byte)TriggerMode.Off;
            return;
        }

        var zones = (ushort)((1 << start) | (1 << end));

        report[offset] = (byte)TriggerMode.Weapon;
        report[offset + 1] = (byte)(zones & 0xFF);
        report[offset + 2] = (byte)((zones >> 8) & 0xFF);
        report[offset + 3] = (byte)(strength - 1);
    }

    private static void WriteVibration(byte[] report, int offset, TriggerEffect effect)
    {
        var position = Math.Clamp(effect.Position, (byte)0, (byte)9);
        var amplitude = Math.Clamp(effect.Strength, (byte)0, (byte)8);
        if (amplitude == 0 || effect.Frequency == 0)
        {
            report[offset] = (byte)TriggerMode.Off;
            return;
        }

        ushort zones = 0;
        uint amplitudes = 0;
        var packedAmplitude = (byte)(amplitude - 1);
        for (var zone = position; zone < 10; zone++)
        {
            zones |= (ushort)(1 << zone);
            amplitudes |= (uint)(packedAmplitude & 0x07) << (zone * 3);
        }

        report[offset] = (byte)TriggerMode.Vibration;
        report[offset + 1] = (byte)(zones & 0xFF);
        report[offset + 2] = (byte)((zones >> 8) & 0xFF);
        report[offset + 3] = (byte)(amplitudes & 0xFF);
        report[offset + 4] = (byte)((amplitudes >> 8) & 0xFF);
        report[offset + 5] = (byte)((amplitudes >> 16) & 0xFF);
        report[offset + 6] = (byte)((amplitudes >> 24) & 0xFF);
        report[offset + 9] = effect.Frequency;
    }

    public void Reset()
    {
        Write(new ControllerOutput(TriggerEffect.Off, TriggerEffect.Off, 0, 0, 32, 0));
    }

    private void DisconnectNoReset()
    {
        _stream?.Dispose();
        _stream = null;
        _device = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { Reset(); } catch { }
            DisconnectNoReset();
        }
    }
}
