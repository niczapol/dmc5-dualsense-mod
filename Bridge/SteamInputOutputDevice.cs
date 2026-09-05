using System.Runtime.InteropServices;

namespace DMC5DualSense.Bridge;

internal static class SteamDualSenseTriggerPayload
{
    internal const int Size = 120;
    internal const int LeftCommandOffset = 8;
    internal const int RightCommandOffset = 64;
    private const int CommandDataOffset = 8;

    public static byte[] Build(TriggerEffect left, TriggerEffect right)
    {
        var payload = new byte[Size];
        payload[0] = 0x03; // SCE_PAD_TRIGGER_MASK_L2 | SCE_PAD_TRIGGER_MASK_R2
        WriteCommand(payload, LeftCommandOffset, left);
        WriteCommand(payload, RightCommandOffset, right);
        return payload;
    }

    private static void WriteCommand(byte[] payload, int commandOffset, TriggerEffect effect)
    {
        var dataOffset = commandOffset + CommandDataOffset;
        switch (effect.Mode)
        {
            case TriggerMode.Feedback when effect.Strength > 0:
                WriteInt32(payload, commandOffset, 1);
                payload[dataOffset] = Math.Clamp(effect.Position, (byte)0, (byte)9);
                payload[dataOffset + 1] = Math.Clamp(effect.Strength, (byte)0, (byte)8);
                break;

            case TriggerMode.Weapon when effect.Strength > 0:
            {
                var start = Math.Clamp(effect.Position, (byte)2, (byte)7);
                var end = Math.Clamp(effect.EndPosition, (byte)(start + 1), (byte)8);
                WriteInt32(payload, commandOffset, 2);
                payload[dataOffset] = start;
                payload[dataOffset + 1] = end;
                payload[dataOffset + 2] = Math.Clamp(effect.Strength, (byte)0, (byte)8);
                break;
            }

            case TriggerMode.Vibration when effect.Strength > 0 && effect.Frequency > 0:
                WriteInt32(payload, commandOffset, 3);
                payload[dataOffset] = Math.Clamp(effect.Position, (byte)0, (byte)9);
                payload[dataOffset + 1] = Math.Clamp(effect.Strength, (byte)0, (byte)8);
                payload[dataOffset + 2] = effect.Frequency;
                break;

            default:
                WriteInt32(payload, commandOffset, 0);
                break;
        }
    }

    private static void WriteInt32(byte[] payload, int offset, int value) =>
        BitConverter.TryWriteBytes(payload.AsSpan(offset, sizeof(int)), value);
}

internal sealed class SteamInputOutputDevice : IControllerOutputDevice
{
    private const string SteamClientVersion = "SteamClient020";
    private const string SteamInputVersion = "SteamInput006";
    private const int SteamClientGetInputIndex = 38;
    private const int SteamInputInitIndex = 0;
    private const int SteamInputShutdownIndex = 1;
    private const int SteamInputRunFrameIndex = 3;
    private const int SteamInputGetConnectedControllersIndex = 6;
    private const int SteamInputTriggerVibrationIndex = 30;
    private const int SteamInputSetLedColorIndex = 33;
    private const int SteamInputGetInputTypeForHandleIndex = 37;
    private const int SteamInputSetDualSenseTriggerEffectIndex = 47;
    private const int Ps5ControllerType = 13;
    private const uint LedFlagRestoreUserDefault = 1;

    private readonly object _gate = new();
    private readonly string _steamApiPath;
    private IntPtr _module;
    private IntPtr _steamInput;
    private ulong _controllerHandle;
    private bool _steamApiInitialized;
    private bool _steamInputInitialized;
    private DateTime _nextReconnectUtc = DateTime.MinValue;
    private string _lastError = "not initialized";
    private long _writeAttempts;
    private long _writeSuccesses;
    private long _triggerEffectWrites;
    private long _rumbleWrites;

    private SteamApiShutdownDelegate? _steamApiShutdown;
    private SteamInputShutdownDelegate? _inputShutdown;
    private SteamInputRunFrameDelegate? _runFrame;
    private SteamInputGetConnectedControllersDelegate? _getConnectedControllers;
    private SteamInputTriggerVibrationDelegate? _triggerVibration;
    private SteamInputSetLedColorDelegate? _setLedColor;
    private SteamInputGetInputTypeForHandleDelegate? _getInputTypeForHandle;
    private SteamInputSetDualSenseTriggerEffectDelegate? _setDualSenseTriggerEffect;

    public SteamInputOutputDevice(string baseDirectory)
    {
        _steamApiPath = Path.GetFullPath(Path.Combine(baseDirectory, "..", "steam_api64.dll"));
    }

    public bool Connected
    {
        get { lock (_gate) return _controllerHandle != 0; }
    }

    public string Description
    {
        get
        {
            lock (_gate)
            {
                return _controllerHandle != 0
                    ? $"Steam Input PS5 controller 0x{_controllerHandle:X}"
                    : _lastError;
            }
        }
    }

    public bool EnsureConnected()
    {
        lock (_gate) return EnsureConnectedNoLock();
    }

    private bool EnsureConnectedNoLock()
    {
        if (_controllerHandle == 0 && DateTime.UtcNow < _nextReconnectUtc) return false;
        _nextReconnectUtc = DateTime.UtcNow.AddSeconds(1);

        try
        {
            if (!_steamApiInitialized && !InitializeSteamNoLock()) return false;
            _runFrame!(_steamInput, true);

            var handles = new ulong[16];
            var pinned = GCHandle.Alloc(handles, GCHandleType.Pinned);
            int count;
            try
            {
                count = Math.Clamp(
                    _getConnectedControllers!(_steamInput, pinned.AddrOfPinnedObject()),
                    0,
                    handles.Length);
            }
            finally
            {
                pinned.Free();
            }

            if (_controllerHandle != 0 && handles.Take(count).Contains(_controllerHandle)) return true;
            _controllerHandle = handles.Take(count)
                .FirstOrDefault(handle => handle != 0 &&
                    _getInputTypeForHandle!(_steamInput, handle) == Ps5ControllerType);
            if (_controllerHandle == 0)
            {
                _lastError = count == 0
                    ? "Steam Input has no connected controller"
                    : $"Steam Input found {count} controller(s), but no PS5 DualSense";
                return false;
            }

            _lastError = "";
            return true;
        }
        catch (Exception ex)
        {
            _controllerHandle = 0;
            _lastError = "Steam Input output failed: " + ex.Message;
            return false;
        }
    }

    private bool InitializeSteamNoLock()
    {
        try
        {
            if (!File.Exists(_steamApiPath))
            {
                _lastError = "steam_api64.dll not found beside DevilMayCry5.exe";
                return false;
            }

            Environment.SetEnvironmentVariable("SteamAppId",
                Environment.GetEnvironmentVariable("SteamAppId") ?? "601150");
            Environment.SetEnvironmentVariable("SteamGameId",
                Environment.GetEnvironmentVariable("SteamGameId") ?? "601150");

            _module = NativeLibrary.Load(_steamApiPath);
            var steamApiInit = Export<SteamApiInitDelegate>("SteamAPI_Init");
            _steamApiShutdown = Export<SteamApiShutdownDelegate>("SteamAPI_Shutdown");
            var getUser = Export<SteamApiGetHandleDelegate>("SteamAPI_GetHSteamUser");
            var getPipe = Export<SteamApiGetHandleDelegate>("SteamAPI_GetHSteamPipe");
            var createInterface = Export<SteamInternalCreateInterfaceDelegate>("SteamInternal_CreateInterface");

            if (!steamApiInit())
                throw new InvalidOperationException("SteamAPI_Init returned false");
            _steamApiInitialized = true;

            var steamClient = createInterface(SteamClientVersion);
            if (steamClient == IntPtr.Zero)
                throw new InvalidOperationException($"{SteamClientVersion} is unavailable");

            var getSteamInput = Method<SteamClientGetInputDelegate>(steamClient, SteamClientGetInputIndex);
            _steamInput = getSteamInput(steamClient, getUser(), getPipe(), SteamInputVersion);
            if (_steamInput == IntPtr.Zero)
                throw new InvalidOperationException($"{SteamInputVersion} is unavailable");

            var inputInit = Method<SteamInputInitDelegate>(_steamInput, SteamInputInitIndex);
            _inputShutdown = Method<SteamInputShutdownDelegate>(_steamInput, SteamInputShutdownIndex);
            _runFrame = Method<SteamInputRunFrameDelegate>(_steamInput, SteamInputRunFrameIndex);
            _getConnectedControllers = Method<SteamInputGetConnectedControllersDelegate>(
                _steamInput, SteamInputGetConnectedControllersIndex);
            _triggerVibration = Method<SteamInputTriggerVibrationDelegate>(
                _steamInput, SteamInputTriggerVibrationIndex);
            _setLedColor = Method<SteamInputSetLedColorDelegate>(
                _steamInput, SteamInputSetLedColorIndex);
            _getInputTypeForHandle = Method<SteamInputGetInputTypeForHandleDelegate>(
                _steamInput, SteamInputGetInputTypeForHandleIndex);
            _setDualSenseTriggerEffect = Method<SteamInputSetDualSenseTriggerEffectDelegate>(
                _steamInput, SteamInputSetDualSenseTriggerEffectIndex);

            if (!inputInit(_steamInput, true))
                throw new InvalidOperationException("ISteamInput::Init returned false");
            _steamInputInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            _lastError = "Steam Input initialization failed: " + ex.Message;
            ShutdownSteamNoLock();
            return false;
        }
    }

    public bool Write(ControllerOutput output)
    {
        lock (_gate)
        {
            _writeAttempts++;
            if (!EnsureConnectedNoLock()) return false;

            try
            {
                _runFrame!(_steamInput, true);
                var triggerPayload = SteamDualSenseTriggerPayload.Build(
                    output.LeftTrigger, output.RightTrigger);
                var pinned = GCHandle.Alloc(triggerPayload, GCHandleType.Pinned);
                try
                {
                    _setDualSenseTriggerEffect!(
                        _steamInput, _controllerHandle, pinned.AddrOfPinnedObject());
                }
                finally
                {
                    pinned.Free();
                }

                _setLedColor!(_steamInput, _controllerHandle,
                    output.Red, output.Green, output.Blue, 0);
                _triggerVibration!(_steamInput, _controllerHandle,
                    ScaleRumble(output.LeftRumble), ScaleRumble(output.RightRumble));

                _writeSuccesses++;
                if (output.LeftTrigger.Mode != TriggerMode.Off ||
                    output.RightTrigger.Mode != TriggerMode.Off)
                    _triggerEffectWrites++;
                if (output.LeftRumble != 0 || output.RightRumble != 0)
                    _rumbleWrites++;
                return true;
            }
            catch (Exception ex)
            {
                _controllerHandle = 0;
                _lastError = "Steam Input output failed: " + ex.Message;
                return false;
            }
        }
    }

    private static ushort ScaleRumble(byte value) => (ushort)(value * 257);

    public ControllerWriteDiagnostic GetAndResetWriteDiagnostic()
    {
        lock (_gate)
        {
            var diagnostic = new ControllerWriteDiagnostic(
                _writeAttempts,
                _writeSuccesses,
                _triggerEffectWrites,
                _rumbleWrites);
            _writeAttempts = 0;
            _writeSuccesses = 0;
            _triggerEffectWrites = 0;
            _rumbleWrites = 0;
            return diagnostic;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_steamInput == IntPtr.Zero || _controllerHandle == 0) return;
            try
            {
                var payload = SteamDualSenseTriggerPayload.Build(
                    TriggerEffect.Off, TriggerEffect.Off);
                var pinned = GCHandle.Alloc(payload, GCHandleType.Pinned);
                try
                {
                    _setDualSenseTriggerEffect!(
                        _steamInput, _controllerHandle, pinned.AddrOfPinnedObject());
                }
                finally
                {
                    pinned.Free();
                }
                _triggerVibration!(_steamInput, _controllerHandle, 0, 0);
                _setLedColor!(_steamInput, _controllerHandle, 0, 0, 0,
                    LedFlagRestoreUserDefault);
            }
            catch
            {
                // Steam or the controller may already be shutting down.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Reset();
            ShutdownSteamNoLock();
        }
    }

    private void ShutdownSteamNoLock()
    {
        if (_steamInputInitialized && _steamInput != IntPtr.Zero && _inputShutdown is not null)
        {
            try { _inputShutdown(_steamInput); } catch { }
        }
        _steamInputInitialized = false;
        _steamInput = IntPtr.Zero;
        _controllerHandle = 0;

        if (_steamApiInitialized && _steamApiShutdown is not null)
        {
            try { _steamApiShutdown(); } catch { }
        }
        _steamApiInitialized = false;

        if (_module != IntPtr.Zero)
        {
            try { NativeLibrary.Free(_module); } catch { }
            _module = IntPtr.Zero;
        }
    }

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));

    private static T Method<T>(IntPtr instance, int index) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(instance);
        var address = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SteamApiInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SteamApiShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SteamApiGetHandleDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr SteamInternalCreateInterfaceDelegate(string version);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
    private delegate IntPtr SteamClientGetInputDelegate(
        IntPtr self, int user, int pipe, string version);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SteamInputInitDelegate(
        IntPtr self, [MarshalAs(UnmanagedType.I1)] bool explicitlyCallRunFrame);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SteamInputShutdownDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SteamInputRunFrameDelegate(
        IntPtr self, [MarshalAs(UnmanagedType.I1)] bool reserved);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int SteamInputGetConnectedControllersDelegate(IntPtr self, IntPtr handles);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SteamInputTriggerVibrationDelegate(
        IntPtr self, ulong controllerHandle, ushort leftSpeed, ushort rightSpeed);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SteamInputSetLedColorDelegate(
        IntPtr self, ulong controllerHandle, byte red, byte green, byte blue, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int SteamInputGetInputTypeForHandleDelegate(IntPtr self, ulong controllerHandle);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SteamInputSetDualSenseTriggerEffectDelegate(
        IntPtr self, ulong controllerHandle, IntPtr triggerEffect);
}
