using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using via.hid;

public static class DMC5DualSensePlugin
{
    private const int Port = 27105;
    private static UdpClient? _udp;
    private static DateTime _lastStateUtc = DateTime.MinValue;
    private static DateTime _lastErrorUtc = DateTime.MinValue;
    private static float _lastHp = -1;
    private static uint _lastMotionBank = uint.MaxValue;
    private static uint _lastMotionId = uint.MaxValue;
    private static string _lastCharacter = "unknown";
    private static string _baseDirectory = "";
    private static bool _enableCalibrationLog = true;
    private static bool _playerTypeDumped;
    private static DateTime _lastActEventUtc = DateTime.MinValue;
    private static DateTime _lastWeaponHitUtc = DateTime.MinValue;
    private static DateTime _lastDanteShotUtc = DateTime.MinValue;
    private static DateTime _lastBlueRoseShotUtc = DateTime.MinValue;
    private static DateTime _nextMissingPlayerPollUtc = DateTime.MinValue;
    private static int _lastExceedStock = -1;
    private static bool _blueRoseCharging;
    [ThreadStatic] private static IObject? _pendingDanteShellPlayer;
    [ThreadStatic] private static int _pendingDanteWeaponId;
    private static readonly List<MethodHook> _hooks = new();
    private static readonly float[] _lastMotorPower = new float[132];
    private static readonly DateTime[] _lastMotorUtc = new DateTime[132];

    [PluginEntryPoint]
    public static void Main()
    {
        try
        {
            _baseDirectory = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
                "DMC5DualSense");
            Directory.CreateDirectory(_baseDirectory);
            WriteRuntimeLog("=== plugin session " +
                            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " ===");
            _enableCalibrationLog = ReadCalibrationSetting();

            _udp = new UdpClient();
            _udp.Connect(IPAddress.Loopback, Port);
            StartBridge();
            InstallGameplayHooks();
            LogInfo("Plugin loaded.");
        }
        catch (Exception ex)
        {
            LogError("Startup failed: " + ex);
        }
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        _udp?.Dispose();
        _udp = null;
        LogInfo("Plugin unloaded.");
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void OnUpdate()
    {
        if (_udp is null || DateTime.UtcNow - _lastStateUtc < TimeSpan.FromMilliseconds(50)) return;
        _lastStateUtc = DateTime.UtcNow;
        if (DateTime.UtcNow < _nextMissingPlayerPollUtc) return;

        try
        {
            var manager = API.GetManagedSingleton("app.PlayerManager") as IObject;
            // manualPlayer, hp and maxHp are inherited runtime fields. REFramework's
            // TypeDefinition.Fields enumeration does not include them consistently,
            // although direct GetField access does. Enumerating first caused every
            // live player to be reported as missing and disabled adaptive triggers.
            var player = manager?.GetField("manualPlayer") as IObject;

            if (player is null)
            {
                PublishMissingPlayerState();
                return;
            }

            var character = DetectCharacter(player);
            if (character == "unknown")
            {
                PublishMissingPlayerState();
                return;
            }

            _nextMissingPlayerPollUtc = DateTime.MinValue;

            if (!_playerTypeDumped)
            {
                DumpTypeHierarchy(player);
                _playerTypeDumped = true;
            }

            var hp = ToSingle(player.GetField("hp"));
            var maxHp = ToSingle(player.GetField("maxHp"));
            ReadMotion(player, out var motionBank, out var motionId, out var motionFrame);
            ReadGamePad(out var triggerLeft, out var triggerRight);

            var exceedGauge = 0f;
            var exceedGaugeMax = 0f;
            var exceedStock = 0;
            var exceedRequest = false;
            var exceedRequestValue = 0f;
            var blueRoseChargeLevel = 0;
            var blueRoseTimer = 0f;
            var danteWeaponId = -1;

            if (character == "nero")
            {
                exceedGauge = SafeCallSingle(player, "get_exceedGauge");
                exceedGaugeMax = SafeCallSingle(player, "get_MaxExceedGauge");
                exceedStock = SafeCallInt(player, "get_exceedStock");
                exceedRequest = SafeCallBool(player, "get_exceedReqTrigger");
                exceedRequestValue = SafeCallSingle(player, "get_reqExceed");
                blueRoseChargeLevel = SafeCallInt(player, "get_reserveChargeLevel");
                blueRoseTimer = SafeCallSingle(player, "get_blueRoseTimer");

                // Some DMC5 builds expose ExceedGauge.addStock with a different
                // reflected signature. The stock delta, correlated with the
                // outgoing-hit hook, remains a reliable EX/MAX-Act fallback and
                // does not react to ordinary out-of-combat L2 revving.
                if (_lastExceedStock >= 0 && exceedStock > _lastExceedStock &&
                    DateTime.UtcNow - _lastWeaponHitUtc < TimeSpan.FromMilliseconds(280))
                {
                    var gained = exceedStock - _lastExceedStock;
                    SendActEvent(gained >= 2 || (_lastExceedStock == 0 && exceedStock >= 3)
                        ? "max_act"
                        : "ex_act");
                }
                _lastExceedStock = exceedStock;

                if (_enableCalibrationLog)
                {
                    LogNeroState(exceedGauge, exceedGaugeMax, exceedStock,
                        exceedRequest, exceedRequestValue, blueRoseChargeLevel,
                        blueRoseTimer, motionBank, motionId, motionFrame);
                }
            }
            else if (character == "dante")
            {
                danteWeaponId = SafeCallInt(player, "get_weaponL_ID");
            }

            SendState(character, true, hp, maxHp, motionBank, motionId, motionFrame,
                exceedGauge, exceedGaugeMax, exceedStock, exceedRequest,
                exceedRequestValue, blueRoseChargeLevel, blueRoseTimer, danteWeaponId,
                triggerLeft, triggerRight);

            if (_lastHp >= 0 && hp < _lastHp && maxHp > 0)
            {
                var amount = Math.Clamp((_lastHp - hp) / maxHp * 4f, 0.15f, 1f);
                Send("{\"v\":1,\"type\":\"event\",\"name\":\"damage\",\"value\":" +
                     F(amount) + "}");
            }

            if (_enableCalibrationLog &&
                (motionBank != _lastMotionBank || motionId != _lastMotionId ||
                 !character.Equals(_lastCharacter, StringComparison.OrdinalIgnoreCase)))
            {
                LogMotion(character, motionBank, motionId, motionFrame, hp, maxHp);
            }

            _lastHp = hp;
            _lastMotionBank = motionBank;
            _lastMotionId = motionId;
            _lastCharacter = character;
        }
        catch (Exception ex)
        {
            LogThrottled("Telemetry error: " + ex.Message);
        }
    }

    private static void PublishMissingPlayerState()
    {
        _nextMissingPlayerPollUtc = DateTime.UtcNow.AddMilliseconds(750);
        SendState("unknown", false, 0, 0, 0, 0, 0);
        _lastHp = -1;
        _lastExceedStock = -1;
        _blueRoseCharging = false;
    }

    private static void InstallGameplayHooks()
    {
        var tdb = API.GetTDB();

        InstallPreHook(tdb, "via.hid.GamePadDevice",
            "setMotorPower(via.hid.GamePadMotor, System.Single)", OnMotorPower,
            "RE Engine motor output");

        // setMotorPower below is the final authoritative output path for PadShake
        // and all other ordinary PC rumble. Calling guessed PadShake accessors here
        // generated thousands of REFramework "method not found" messages without
        // producing a usable packet, so the redundant high-level hook is omitted.
        LogInfo("Ordinary rumble is captured at via.hid.GamePadDevice.setMotorPower.");

        InstallPreHook(tdb, "app.PlayerNero", "set_exceedReqTrigger(System.Boolean)", OnExceedInput,
            "Nero Exceed input");
        InstallPreHook(tdb, "app.PlayerNero", "setMaxAct(System.Boolean)", OnMaxAct,
            "Nero MAX-Act");
        InstallPreHook(tdb, "app.player.ExceedGauge",
            "addStock(System.Int32, System.Boolean, System.Boolean)", OnExceedStockAdded,
            "Nero EX/MAX-Act stock");
        LogMethodCandidates(tdb, "app.player.ExceedGauge", "stock");
        InstallPreHook(tdb, "app.PlayerNero", "onBlueRoseChargeStart()", OnGunChargeStart,
            "Blue Rose charge start");
        InstallPreHook(tdb, "app.PlayerNero", "onBlueRoseChargeLevelUp()", OnGunChargeLevel,
            "Blue Rose charge level");
        InstallPreHook(tdb, "app.PlayerNero", "onBlueRoseChargeCancel()", OnGunChargeEnd,
            "Blue Rose charge cancel");
        InstallPreHook(tdb, "app.PlayerNero", "onBlueRoseChargeComplete()", OnGunChargeLevel,
            "Blue Rose charge complete");
        InstallPreHook(tdb, "app.PlayerNero", "setBRShot(System.Boolean, System.Boolean)", OnBlueRoseShot,
            "Blue Rose shot HD haptic");
        InstallPrePostHook(tdb, "app.PlayerDante", "createShell(app.ShellTrack)",
            OnDanteCreateShellPre, OnDanteCreateShellPost,
            "Dante firearm HD haptics");
        LogMethodCandidates(tdb, "app.PlayerDante", "shot");
        LogMethodCandidates(tdb, "app.PlayerDante", "shell");
        InstallPreHook(tdb, "app.PlayerVergilPL", "onChargeCompleteJudgementCut()", OnJudgementCut,
            "Vergil Judgment Cut");
        InstallPreHook(tdb, "app.PlayerVergilPL", "finishJudgementCutEnd()", OnJudgementCutEnd,
            "Vergil Judgment Cut End");
        InstallPreHook(tdb, "app.PlayerVergilPL", "onCheckChargeStartBeowulf()", OnBeowulfPre,
            "Vergil Beowulf pre-impact HD haptic");
        InstallPreHook(tdb, "app.PlayerVergilPL",
            "setBeowulfJustReleaseRate(app.HitController.DamageInfo)", OnBeowulfImpact,
            "Vergil Beowulf impact HD haptic");
        InstallPreHook(tdb, "app.fsm2.player.pl0800.PL0820ForceedgeDeadlyAction",
            "start(via.behaviortree.ActionArg)", OnMirageLoop,
            "Mirage Edge special loop HD haptic");
        InstallPreHook(tdb, "app.fsm2.player.pl0800.PL0820ForceedgeDeadlyAction",
            "end(via.behaviortree.ActionArg)", OnMirageEnd,
            "Mirage Edge special end HD haptic");

        // attackHitCore is the confirmed outgoing-hit path on the playable
        // classes. Hooking both the base and overrides covers all four players;
        // the callback's short de-duplication window collapses chained calls.
        InstallPreHook(tdb, "app.Player", "attackHitCore(app.HitController.DamageInfo)",
            OnWeaponHit, "Player weapon-hit base");
        InstallPreHook(tdb, "app.PlayerNero", "attackHitCore(app.HitController.DamageInfo)",
            OnWeaponHit, "Nero weapon hit");
        InstallPreHook(tdb, "app.PlayerDante", "attackHitCore(app.HitController.DamageInfo)",
            OnWeaponHit, "Dante weapon hit");
        InstallPreHook(tdb, "app.PlayerV", "attackHitCore(app.HitController.DamageInfo)",
            OnWeaponHit, "V weapon hit");
        InstallPreHook(tdb, "app.PlayerVergilPL", "attackHitCore(app.HitController.DamageInfo)",
            OnWeaponHit, "Vergil weapon hit");
    }

    private static void InstallPreHook(
        TDB tdb,
        string typeName,
        string signature,
        MethodHook.PreHookDelegate callback,
        string label)
    {
        var method = tdb.GetType(typeName)?.GetMethod(signature);
        if (method is null)
        {
            LogError("Hook not found: " + typeName + "." + signature);
            return;
        }

        var hook = method.AddHook(false);
        hook.AddPre(callback);
        _hooks.Add(hook);
        LogInfo(label + " hook installed.");
    }

    private static void InstallPrePostHook(
        TDB tdb,
        string typeName,
        string signature,
        MethodHook.PreHookDelegate preCallback,
        MethodHook.PostHookDelegate postCallback,
        string label)
    {
        var method = tdb.GetType(typeName)?.GetMethod(signature);
        if (method is null)
        {
            LogError("Hook not found: " + typeName + "." + signature);
            return;
        }

        var hook = method.AddHook(false);
        hook.AddPre(preCallback);
        hook.AddPost(postCallback);
        _hooks.Add(hook);
        LogInfo(label + " pre/post hook installed.");
    }

    private static void LogMethodCandidates(TDB tdb, string typeName, string contains)
    {
        try
        {
            var type = tdb.GetType(typeName);
            if (type is null) return;
            foreach (var method in type.GetMethods())
            {
                var signature = method.GetMethodSignature();
                if (signature.Contains(contains, StringComparison.OrdinalIgnoreCase))
                    LogInfo("Candidate " + typeName + "." + signature);
            }
        }
        catch (Exception ex)
        {
            LogThrottled("Method candidate dump error: " + ex.Message);
        }
    }

    private static PreHookResult OnMotorPower(Span<ulong> args)
    {
        try
        {
            if (args.Length < 4) return PreHookResult.Continue;
            var motor = unchecked((int)args[2]);
            var power = Math.Clamp(BitConverter.Int32BitsToSingle(unchecked((int)args[3])), 0f, 1f);
            if (motor < 0 || motor >= _lastMotorPower.Length) return PreHookResult.Continue;

            var now = DateTime.UtcNow;
            if (Math.Abs(power - _lastMotorPower[motor]) >= 0.005f ||
                now - _lastMotorUtc[motor] >= TimeSpan.FromMilliseconds(120))
            {
                _lastMotorPower[motor] = power;
                _lastMotorUtc[motor] = now;
                if (_enableCalibrationLog)
                    LogMotor(motor, power, _lastCharacter);
                Send("{\"v\":1,\"type\":\"motor\",\"motor\":" +
                     motor.ToString(CultureInfo.InvariantCulture) +
                     ",\"value\":" + F(power) + "}");
            }
        }
        catch (Exception ex)
        {
            LogThrottled("Motor hook error: " + ex.Message);
        }

        return PreHookResult.Continue;
    }

    private static PreHookResult OnExceedInput(Span<ulong> args)
    {
        if (args.Length > 2 && (args[2] & 1) != 0) SendEvent("exceed_input");
        return PreHookResult.Continue;
    }

    private static PreHookResult OnMaxAct(Span<ulong> args)
    {
        if (args.Length > 2 && (args[2] & 1) != 0) SendActEvent("max_act");
        return PreHookResult.Continue;
    }

    private static PreHookResult OnExceedStockAdded(Span<ulong> args)
    {
        try
        {
            if (args.Length < 5) return PreHookResult.Continue;
            var amount = unchecked((int)args[2]);
            var isJustFullThrottle = (args[4] & 1) != 0;
            if (amount > 0 && isJustFullThrottle)
                SendActEvent(amount >= 3 ? "max_act" : "ex_act");
        }
        catch (Exception ex)
        {
            LogThrottled("EX/MAX-Act hook error: " + ex.Message);
        }

        return PreHookResult.Continue;
    }

    private static void SendActEvent(string name)
    {
        var now = DateTime.UtcNow;
        if (now - _lastActEventUtc < TimeSpan.FromMilliseconds(70)) return;
        _lastActEventUtc = now;
        SendEvent(name);
    }

    private static PreHookResult OnWeaponHit(Span<ulong> args)
    {
        var now = DateTime.UtcNow;
        if (now - _lastWeaponHitUtc >= TimeSpan.FromMilliseconds(32))
        {
            _lastWeaponHitUtc = now;
            SendEvent("weapon_hit", 1f);
        }
        return PreHookResult.Continue;
    }

    private static PreHookResult OnGunChargeStart(Span<ulong> args)
    {
        if (_blueRoseCharging) return PreHookResult.Continue;
        _blueRoseCharging = true;
        SendEvent("gun_charge_start");
        return PreHookResult.Continue;
    }

    private static PreHookResult OnGunChargeLevel(Span<ulong> args)
    {
        _blueRoseCharging = true;
        SendEvent("gun_charge_level");
        return PreHookResult.Continue;
    }

    private static PreHookResult OnGunChargeEnd(Span<ulong> args)
    {
        if (!_blueRoseCharging) return PreHookResult.Continue;
        _blueRoseCharging = false;
        SendEvent("gun_charge_end");
        return PreHookResult.Continue;
    }

    private static PreHookResult OnBlueRoseShot(Span<ulong> args)
    {
        var now = DateTime.UtcNow;
        if (now - _lastBlueRoseShotUtc < TimeSpan.FromMilliseconds(35))
            return PreHookResult.Continue;
        _lastBlueRoseShotUtc = now;
        _blueRoseCharging = false;
        SendEvent("blue_rose_shot");
        return PreHookResult.Continue;
    }

    private static PreHookResult OnDanteCreateShellPre(Span<ulong> args)
    {
        try
        {
            _pendingDanteShellPlayer = args.Length > 1
                ? ManagedObject.ToManagedObject(args[1]) as IObject
                : null;
            _pendingDanteWeaponId = _pendingDanteShellPlayer is null
                ? -1
                : SafeCallInt(_pendingDanteShellPlayer, "get_weaponL_ID");
        }
        catch
        {
            _pendingDanteShellPlayer = null;
            _pendingDanteWeaponId = -1;
        }
        return PreHookResult.Continue;
    }

    private static void OnDanteCreateShellPost(ref ulong returnValue)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastDanteShotUtc < TimeSpan.FromMilliseconds(20))
                return;

            var player = _pendingDanteShellPlayer;
            if (player is null) return;
            switch (_pendingDanteWeaponId)
            {
                case 0: // Ebony & Ivory
                    _lastDanteShotUtc = now;
                    SendEvent(SafeCallBool(player, "get_isEbonyShot")
                        ? "dante_ebony_shot"
                        : "dante_ivory_shot");
                    break;

                case 1: // Coyote-A
                    _lastDanteShotUtc = now;
                    SendEvent("dante_coyote_shot");
                    break;
            }
        }
        catch (Exception ex)
        {
            LogThrottled("Dante firearm haptic error: " + ex.Message);
        }
        finally
        {
            _pendingDanteShellPlayer = null;
            _pendingDanteWeaponId = -1;
        }
    }

    private static PreHookResult OnJudgementCut(Span<ulong> args)
    {
        try
        {
            var player = args.Length > 1 ? ManagedObject.ToManagedObject(args[1]) as IObject : null;
            SendEvent(player is not null && SafeCallBool(player, "get_isJudgeMentCutJR")
                ? "judgement_cut_jr"
                : "judgement_cut");
        }
        catch
        {
            SendEvent("judgement_cut");
        }
        return PreHookResult.Continue;
    }

    private static PreHookResult OnJudgementCutEnd(Span<ulong> args)
    {
        SendEvent("yamato_return");
        SendEvent("yamato_noutou");
        return PreHookResult.Continue;
    }

    private static PreHookResult OnBeowulfPre(Span<ulong> args)
    {
        SendEvent("beowulf_pre");
        return PreHookResult.Continue;
    }

    private static PreHookResult OnBeowulfImpact(Span<ulong> args)
    {
        SendEvent("beowulf_impact");
        return PreHookResult.Continue;
    }

    private static PreHookResult OnMirageLoop(Span<ulong> args)
    {
        SendEvent("mirage_loop");
        return PreHookResult.Continue;
    }

    private static PreHookResult OnMirageEnd(Span<ulong> args)
    {
        SendEvent("mirage_end");
        return PreHookResult.Continue;
    }

    private static void SendEvent(string name, float value = 0f)
    {
        ReadGamePad(out var triggerLeft, out var triggerRight);
        WriteRuntimeLog("event " + name + " value=" + F(value) +
                        " left=" + F(triggerLeft) + " right=" + F(triggerRight));
        Send("{\"v\":1,\"type\":\"event\",\"name\":\"" + Escape(name) +
             "\",\"value\":" + F(value) +
             ",\"left\":" + F(triggerLeft) + ",\"right\":" + F(triggerRight) + "}");
    }

    private static void ReadGamePad(out float left, out float right)
    {
        left = 0;
        right = 0;
        try
        {
            var device = GamePad.Device;
            if (device is null) return;
            left = Math.Clamp(device.AnalogL, 0f, 1f);
            right = Math.Clamp(device.AnalogR, 0f, 1f);
        }
        catch
        {
            // The device becomes available after RE Engine finishes HID startup.
        }
    }

    private static void ReadMotion(
        IObject player,
        out uint bank,
        out uint id,
        out float frame)
    {
        bank = 0;
        id = 0;
        frame = 0;

        try
        {
            var motion = player.Call("get_cachedMotion") as IObject;
            var layer = motion?.Call("getLayer", 0) as IObject;
            if (layer is null) return;

            bank = ToUInt(layer.Call("get_MotionBankID"));
            id = ToUInt(layer.Call("get_MotionID"));
            frame = ToSingle(layer.Call("get_Frame"));
        }
        catch
        {
            // Motion telemetry is optional; health, character and rumble continue working.
        }
    }

    private static string DetectCharacter(IObject player)
    {
        var identity = "";

        // The concrete managed type already identifies every playable class.
        // Calling get_NetworkName on DMC5's player objects makes REFramework log
        // a failed method lookup every update because that method is not present.
        try { identity += " " + player.GetTypeDefinition().FullName; }
        catch { }

        if (!ContainsCharacterIdentity(identity))
        {
            try
            {
                var gameObject = player.Call("get_GameObject") as IObject;
                identity += " " + Convert.ToString(gameObject?.Call("get_Name"), CultureInfo.InvariantCulture);
            }
            catch { }
        }

        identity = identity.ToLowerInvariant();
        if (identity.Contains("pl0000") || identity.Contains("nero")) return "nero";
        if (identity.Contains("pl0100") || identity.Contains("dante")) return "dante";
        // app.PlayerV lowercases to app.playerv. Check Vergil first because
        // app.playervergil has the same prefix; the old player_v/" v" test
        // therefore left V in the Void reported as unknown/inGameplay=false.
        if (identity.Contains("pl0400") || identity.Contains("vergil")) return "vergil";
        if (identity.Contains("pl0200") || identity.Contains("player_v") ||
            identity.Contains("playerv") || identity.Contains(" v")) return "v";
        return "unknown";
    }

    private static bool ContainsCharacterIdentity(string identity)
    {
        var lower = identity.ToLowerInvariant();
        return lower.Contains("playernero") || lower.Contains("playerdante") ||
               lower.Contains("playerv") || lower.Contains("playervergil") ||
               lower.Contains("pl0000") || lower.Contains("pl0100") ||
               lower.Contains("pl0200") || lower.Contains("pl0400");
    }

    private static object? GetFieldIfPresent(IObject instance, string fieldName)
    {
        try
        {
            var type = instance.GetTypeDefinition();
            while (type is not null)
            {
                foreach (var field in type.Fields)
                {
                    if (field.Name.Equals(fieldName, StringComparison.Ordinal))
                        return instance.GetField(fieldName);
                }

                type = type.ParentType;
            }
        }
        catch
        {
            // Runtime objects can disappear between lookup and access.
        }

        return null;
    }

    private static void SendState(
        string character,
        bool inGameplay,
        float hp,
        float maxHp,
        uint motionBank,
        uint motionId,
        float motionFrame,
        float exceedGauge = 0,
        float exceedGaugeMax = 0,
        int exceedStock = 0,
        bool exceedRequest = false,
        float exceedRequestValue = 0,
        int blueRoseChargeLevel = 0,
        float blueRoseTimer = 0,
        int danteWeaponId = -1,
        float triggerLeft = 0,
        float triggerRight = 0)
    {
        Send("{\"v\":1,\"type\":\"state\",\"character\":\"" + Escape(character) +
             "\",\"inGameplay\":" + (inGameplay ? "true" : "false") +
             ",\"hp\":" + F(hp) + ",\"maxHp\":" + F(maxHp) +
             ",\"motionBank\":" + motionBank.ToString(CultureInfo.InvariantCulture) +
             ",\"motionId\":" + motionId.ToString(CultureInfo.InvariantCulture) +
             ",\"motionFrame\":" + F(motionFrame) +
             ",\"exceedGauge\":" + F(exceedGauge) +
             ",\"exceedGaugeMax\":" + F(exceedGaugeMax) +
             ",\"exceedStock\":" + exceedStock.ToString(CultureInfo.InvariantCulture) +
             ",\"exceedRequest\":" + (exceedRequest ? "true" : "false") +
             ",\"exceedRequestValue\":" + F(exceedRequestValue) +
             ",\"blueRoseChargeLevel\":" + blueRoseChargeLevel.ToString(CultureInfo.InvariantCulture) +
             ",\"blueRoseTimer\":" + F(blueRoseTimer) +
             ",\"danteWeaponId\":" + danteWeaponId.ToString(CultureInfo.InvariantCulture) +
             ",\"left\":" + F(triggerLeft) + ",\"right\":" + F(triggerRight) + "}");
    }

    private static void Send(string json)
    {
        var udp = _udp;
        if (udp is null) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        udp.Send(bytes, bytes.Length);
    }

    private static void StartBridge()
    {
        var path = Path.Combine(_baseDirectory, "DMC5DualSense.Bridge.exe");
        if (!File.Exists(path))
        {
            LogError("Bridge executable not found: " + path);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = "--parent " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            WorkingDirectory = _baseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static bool ReadCalibrationSetting()
    {
        try
        {
            var config = File.ReadAllText(Path.Combine(_baseDirectory, "config.json"));
            var key = config.IndexOf("EnableCalibrationLog", StringComparison.OrdinalIgnoreCase);
            if (key < 0) return true;
            var tail = config.Substring(key, Math.Min(80, config.Length - key));
            return !tail.Contains("false", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    private static void LogMotion(
        string character,
        uint bank,
        uint id,
        float frame,
        float hp,
        float maxHp)
    {
        try
        {
            var path = Path.Combine(_baseDirectory, "calibration.csv");
            if (!File.Exists(path))
                File.AppendAllText(path, "utc,character,motion_bank,motion_id,frame,hp,max_hp\r\n");
            File.AppendAllText(path,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "," + character + "," +
                bank.ToString(CultureInfo.InvariantCulture) + "," +
                id.ToString(CultureInfo.InvariantCulture) + "," + F(frame) + "," + F(hp) + "," + F(maxHp) + "\r\n");
        }
        catch { }
    }

    private static void LogNeroState(
        float gauge,
        float gaugeMax,
        int stock,
        bool request,
        float requestValue,
        int chargeLevel,
        float blueRoseTimer,
        uint bank,
        uint motion,
        float frame)
    {
        try
        {
            var path = Path.Combine(_baseDirectory, "nero-input.csv");
            if (!File.Exists(path))
                File.AppendAllText(path, "utc,gauge,gauge_max,stock,request,request_value,blue_rose_level,blue_rose_timer,motion_bank,motion_id,motion_frame\r\n");
            File.AppendAllText(path,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "," +
                F(gauge) + "," + F(gaugeMax) + "," + stock.ToString(CultureInfo.InvariantCulture) + "," +
                (request ? "1" : "0") + "," + F(requestValue) + "," +
                chargeLevel.ToString(CultureInfo.InvariantCulture) + "," + F(blueRoseTimer) + "," +
                bank.ToString(CultureInfo.InvariantCulture) + "," +
                motion.ToString(CultureInfo.InvariantCulture) + "," + F(frame) + "\r\n");
        }
        catch { }
    }

    private static void LogMotor(int motor, float power, string character)
    {
        try
        {
            var path = Path.Combine(_baseDirectory, "motor.csv");
            if (!File.Exists(path))
                File.AppendAllText(path, "utc,character,motor,power\r\n");
            File.AppendAllText(path,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "," +
                EscapeCsv(character) + "," + motor.ToString(CultureInfo.InvariantCulture) + "," +
                F(power) + "\r\n");
        }
        catch { }
    }

    private static void DumpTypeHierarchy(IObject instance)
    {
        try
        {
            var path = Path.Combine(_baseDirectory, "player-type-dump.txt");
            var output = new StringBuilder();
            var type = instance.GetTypeDefinition();
            var depth = 0;

            while (type is not null && depth++ < 20)
            {
                output.AppendLine("TYPE " + type.GetFullName());
                output.AppendLine("FIELDS");
                foreach (var field in type.GetFields())
                    output.AppendLine("  " + field.GetName());
                output.AppendLine("METHODS");
                foreach (var method in type.GetMethods())
                    output.AppendLine("  " + method.GetMethodSignature());
                output.AppendLine();
                type = type.GetParentType();
            }

            File.WriteAllText(path, output.ToString());
            LogInfo("Player type metadata written to " + path);
        }
        catch (Exception ex)
        {
            LogThrottled("Type dump error: " + ex.Message);
        }
    }

    private static void LogThrottled(string message)
    {
        if (DateTime.UtcNow - _lastErrorUtc < TimeSpan.FromSeconds(5)) return;
        _lastErrorUtc = DateTime.UtcNow;
        LogError(message);
    }

    private static void LogInfo(string message)
    {
        API.LogInfo("[DMC5DualSense] " + message);
        WriteRuntimeLog("INFO " + message);
    }

    private static void LogError(string message)
    {
        API.LogError("[DMC5DualSense] " + message);
        WriteRuntimeLog("ERROR " + message);
    }

    private static void WriteRuntimeLog(string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_baseDirectory)) return;
            File.AppendAllText(
                Path.Combine(_baseDirectory, "plugin.log"),
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " +
                message + Environment.NewLine);
        }
        catch
        {
            // Diagnostics must never interfere with gameplay callbacks.
        }
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapeCsv(string value) =>
        "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string F(float value) =>
        float.IsFinite(value) ? value.ToString("0.######", CultureInfo.InvariantCulture) : "0";

    private static float ToSingle(object? value)
    {
        try { return value is null ? 0 : Convert.ToSingle(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static uint ToUInt(object? value)
    {
        try { return value is null ? 0 : Convert.ToUInt32(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static int ToInt(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static float SafeCallSingle(IObject instance, string method)
    {
        try { return ToSingle(instance.Call(method)); }
        catch { return 0; }
    }

    private static int SafeCallInt(IObject instance, string method)
    {
        try { return ToInt(instance.Call(method)); }
        catch { return 0; }
    }

    private static bool SafeCallBool(IObject instance, string method)
    {
        try
        {
            var value = instance.Call(method);
            return value is bool boolean ? boolean : ToInt(value) != 0;
        }
        catch { return false; }
    }
}
