using System.Runtime.InteropServices;

namespace DMC5DualSense.Bridge;

internal readonly record struct XInputSnapshot(bool Connected, float LeftTrigger, float RightTrigger)
{
    public static XInputSnapshot None => new(false, 0, 0);
}

internal static class XInputReader
{
    public static XInputSnapshot ReadFirstConnected()
    {
        for (uint index = 0; index < 4; index++)
        {
            try
            {
                if (XInputGetState(index, out var state) != 0) continue;
                return new XInputSnapshot(
                    true,
                    state.Gamepad.LeftTrigger / 255f,
                    state.Gamepad.RightTrigger / 255f);
            }
            catch (DllNotFoundException)
            {
                return XInputSnapshot.None;
            }
            catch (EntryPointNotFoundException)
            {
                return XInputSnapshot.None;
            }
        }

        return XInputSnapshot.None;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }
}
