namespace DMC5DualSense.Bridge;

internal readonly record struct XboxInputReport(
    ushort Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short LeftThumbX,
    short LeftThumbY,
    short RightThumbX,
    short RightThumbY);

internal static class DualSenseInputReport
{
    private const ushort DPadUp = 0x0001;
    private const ushort DPadDown = 0x0002;
    private const ushort DPadLeft = 0x0004;
    private const ushort DPadRight = 0x0008;
    private const ushort Start = 0x0010;
    private const ushort Back = 0x0020;
    private const ushort LeftThumb = 0x0040;
    private const ushort RightThumb = 0x0080;
    private const ushort LeftShoulder = 0x0100;
    private const ushort RightShoulder = 0x0200;
    private const ushort Guide = 0x0400;
    private const ushort A = 0x1000;
    private const ushort B = 0x2000;
    private const ushort X = 0x4000;
    private const ushort Y = 0x8000;

    public static bool TryParse(ReadOnlySpan<byte> report, out XboxInputReport state)
    {
        state = default;
        if (report.Length < 11 || report[0] != 0x01) return false;

        var faceAndDpad = report[8];
        var buttonsA = report[9];
        var buttonsB = report[10];
        ushort buttons = 0;

        if ((faceAndDpad & 0x10) != 0) buttons |= X; // Square
        if ((faceAndDpad & 0x20) != 0) buttons |= A; // Cross
        if ((faceAndDpad & 0x40) != 0) buttons |= B; // Circle
        if ((faceAndDpad & 0x80) != 0) buttons |= Y; // Triangle

        buttons |= (faceAndDpad & 0x0F) switch
        {
            0 => DPadUp,
            1 => DPadUp | DPadRight,
            2 => DPadRight,
            3 => DPadRight | DPadDown,
            4 => DPadDown,
            5 => DPadDown | DPadLeft,
            6 => DPadLeft,
            7 => DPadLeft | DPadUp,
            _ => 0
        };

        if ((buttonsA & 0x01) != 0) buttons |= LeftShoulder;
        if ((buttonsA & 0x02) != 0) buttons |= RightShoulder;
        if ((buttonsA & 0x10) != 0) buttons |= Back; // Create
        if ((buttonsA & 0x20) != 0) buttons |= Start; // Options
        if ((buttonsA & 0x40) != 0) buttons |= LeftThumb;
        if ((buttonsA & 0x80) != 0) buttons |= RightThumb;
        if ((buttonsB & 0x01) != 0) buttons |= Guide;
        if ((buttonsB & 0x02) != 0) buttons |= Back; // Touchpad click

        state = new XboxInputReport(
            buttons,
            report[5],
            report[6],
            MapAxis(report[1]),
            MapAxisInverted(report[2]),
            MapAxis(report[3]),
            MapAxisInverted(report[4]));
        return true;
    }

    private static short MapAxis(byte value)
    {
        var centered = value - 128;
        return centered >= 0
            ? (short)(centered * short.MaxValue / 127)
            : (short)(centered * 32768 / 128);
    }

    private static short MapAxisInverted(byte value)
    {
        var mapped = MapAxis(value);
        return mapped == short.MinValue ? short.MaxValue : (short)-mapped;
    }
}
