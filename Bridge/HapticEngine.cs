using System.Reflection;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DMC5DualSense.Bridge;

internal sealed class HapticEngine : IWaveProvider, IDisposable
{
    private const int SampleRate = 48_000;
    private const int ChannelCount = 4;
    private readonly object _gate = new();
    private readonly List<Voice> _voices = [];
    private readonly List<SampleVoice> _sampleVoices = [];
    private readonly Dictionary<string, SampleDefinition> _samples;
    private readonly float _strength;
    private readonly RumbleRuntime _rumble;
    private WasapiOut? _output;
    private MMDevice? _audioDevice;
    private float? _previousEndpointVolume;
    private bool? _previousEndpointMute;
    private float? _managedEndpointVolume;
    private string _status = "disabled";
    private DateTime _advancedHapticsUntilUtc;
    private long _renderedFrames;
    private long _nonZeroRenderedFrames;
    private long _limitedFrames;
    private float _renderPeak;

    public HapticEngine(float strength) : this(strength, loadOriginalSamples: true)
    {
    }

    internal HapticEngine(float strength, bool loadOriginalSamples)
    {
        _strength = Math.Clamp(strength, 0f, 1f);
        _rumble = new RumbleRuntime(_strength);
        WaveFormat = new WaveFormatExtensible(SampleRate, 16, ChannelCount);
        _samples = loadOriginalSamples
            ? LoadOriginalSamples()
            : new Dictionary<string, SampleDefinition>(StringComparer.OrdinalIgnoreCase);
    }

    public WaveFormat WaveFormat { get; }
    public string Status => _status;
    public bool Started => _output is not null;
    public int OriginalSampleCount => _samples.Count;

    public IReadOnlyList<OriginalSampleDiagnostic> GetOriginalSampleDiagnostics() =>
        _samples.Values
            .OrderBy(sample => sample.Index)
            .Select(sample => new OriginalSampleDiagnostic(
                sample.Index,
                sample.Key,
                sample.FileName,
                sample.Channels,
                (sample.Interleaved.Length / (double)sample.Channels) /
                    SampleRate / sample.PlaybackRate,
                sample.GainDb,
                sample.PitchCents,
                sample.DelayFrames / (double)SampleRate,
                sample.Loop,
                sample.Interleaved.Max(value => Math.Abs(value))))
            .ToArray();

    public bool Start(
        string deviceNameFragment,
        bool ensureEndpointAudible,
        float endpointVolume)
    {
        if (_output is not null) return true;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var candidates = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(device => new
                {
                    Device = device,
                    Match = ClassifyAudioEndpoint(
                        device.FriendlyName,
                        deviceNameFragment,
                        ReadStringProperty(device, PropertyKeys.PKEY_Device_ControllerDeviceId),
                        ReadStringProperty(device, PropertyKeys.PKEY_Device_InterfaceKey),
                        ReadChannelCount(device))
                })
                .ToArray();
            var selected = candidates
                .Where(candidate => candidate.Match.Score > 0)
                .OrderByDescending(candidate => candidate.Match.Score)
                .FirstOrDefault();
            _audioDevice = selected?.Device;

            foreach (var candidate in candidates)
            {
                if (!ReferenceEquals(candidate.Device, _audioDevice))
                    candidate.Device.Dispose();
            }

            if (_audioDevice is null)
            {
                _status = "DualSense 4-channel audio endpoint not found";
                return false;
            }

            var volumeStatus = ConfigureEndpointVolume(
                ensureEndpointAudible,
                endpointVolume);
            _output = new WasapiOut(_audioDevice, AudioClientShareMode.Shared, true, 20);
            _output.Init(this);
            _output.Play();
            _status = $"{_audioDevice.FriendlyName}; {selected!.Match.Reason}; " +
                      $"{volumeStatus}; " +
                      $"{_samples.Count}/12 original PS5 samples";
            return true;
        }
        catch (Exception ex)
        {
            _status = ex.Message;
            _output?.Dispose();
            _output = null;
            RestoreEndpointVolume();
            _audioDevice?.Dispose();
            _audioDevice = null;
            return false;
        }
    }

    internal static bool MatchesAudioEndpointName(
        string friendlyName,
        string configuredFragment)
    {
        if (!string.IsNullOrWhiteSpace(configuredFragment) &&
            friendlyName.Contains(configuredFragment, StringComparison.OrdinalIgnoreCase))
            return true;

        // Standard CFI-ZCT1/CFI-ZCT2 endpoints and DualSense Edge use the same
        // product-family words, but Edge inserts its model name in the middle.
        // Ignore the localized Windows prefix and avoid a revision-specific
        // exact match.
        return friendlyName.Contains("DualSense", StringComparison.OrdinalIgnoreCase) &&
               friendlyName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase);
    }

    internal static AudioEndpointMatch ClassifyAudioEndpoint(
        string friendlyName,
        string configuredFragment,
        string controllerDeviceId,
        string interfaceKey,
        int channelCount)
    {
        var hardwareIdentity = controllerDeviceId + " " + interfaceKey;
        var hasSonyUsbVendor = hardwareIdentity.Contains(
            "VID_054C", StringComparison.OrdinalIgnoreCase);
        var hasKnownDualSenseProduct =
            hardwareIdentity.Contains("PID_0CE6", StringComparison.OrdinalIgnoreCase) ||
            hardwareIdentity.Contains("PID_0DF2", StringComparison.OrdinalIgnoreCase) ||
            hardwareIdentity.Contains("PID_0E5F", StringComparison.OrdinalIgnoreCase);
        var hasHapticsChannels = channelCount >= ChannelCount;
        if (!hasHapticsChannels) return new AudioEndpointMatch(0, "four haptic audio channels are required");

        if (hasKnownDualSenseProduct && hasHapticsChannels)
            return new AudioEndpointMatch(1200, "hardware-id DualSense, 4-channel");
        if (hasKnownDualSenseProduct)
            return new AudioEndpointMatch(1100, "hardware-id DualSense");
        if (hasSonyUsbVendor && hasHapticsChannels)
            return new AudioEndpointMatch(900, "Sony USB hardware-id, 4-channel");

        if (MatchesAudioEndpointName(friendlyName, configuredFragment))
            return new AudioEndpointMatch(
                hasHapticsChannels ? 700 : 500,
                hasHapticsChannels ? "friendly-name fallback, 4-channel" :
                                     "friendly-name fallback");

        return new AudioEndpointMatch(0, "not a DualSense haptics endpoint");
    }

    private static string ReadStringProperty(MMDevice device, PropertyKey key)
    {
        try
        {
            return device.Properties.TryGetValue<string>(key, out var value)
                ? value ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static int ReadChannelCount(MMDevice device)
    {
        try
        {
            using var client = device.AudioClient;
            return client.MixFormat.Channels;
        }
        catch { return 0; }
    }

    private string ConfigureEndpointVolume(bool ensureAudible, float endpointVolume)
    {
        if (_audioDevice is null) return "endpoint unavailable";

        var endpoint = _audioDevice.AudioEndpointVolume;
        var originalVolume = endpoint.MasterVolumeLevelScalar;
        var originalMute = endpoint.Mute;
        if (!ensureAudible)
            return $"endpoint volume {originalVolume:P0}, mute={originalMute}";

        var target = Math.Clamp(endpointVolume, 0.05f, 1f);
        _previousEndpointVolume = originalVolume;
        _previousEndpointMute = originalMute;
        endpoint.Mute = false;
        endpoint.MasterVolumeLevelScalar = target;
        _managedEndpointVolume = target;
        return $"endpoint volume {originalVolume:P0}->{target:P0} for this session, " +
               $"mute={originalMute}->False";
    }

    private void RestoreEndpointVolume()
    {
        if (_audioDevice is null ||
            _previousEndpointVolume is null ||
            _previousEndpointMute is null ||
            _managedEndpointVolume is null)
            return;

        try
        {
            var endpoint = _audioDevice.AudioEndpointVolume;
            // Restore only if the endpoint still has the exact state applied by
            // this session. A deliberate user change while DMC5 is running wins.
            if (!endpoint.Mute &&
                Math.Abs(endpoint.MasterVolumeLevelScalar - _managedEndpointVolume.Value) < 0.01f)
            {
                endpoint.MasterVolumeLevelScalar = _previousEndpointVolume.Value;
                endpoint.Mute = _previousEndpointMute.Value;
            }
        }
        catch
        {
            // Endpoint removal during USB disconnect is a normal shutdown case.
        }
        finally
        {
            _previousEndpointVolume = null;
            _previousEndpointMute = null;
            _managedEndpointVolume = null;
        }
    }

    public bool PlayOriginal(string eventName)
    {
        var key = NormalizeEventName(eventName);
        lock (_gate)
        {
            if (key == "stop_all")
            {
                _sampleVoices.Clear();
                return true;
            }

            if (key == "mirage_sp_end")
                _sampleVoices.RemoveAll(voice => voice.Key == "mirage_sp_loop");

            if (!_samples.TryGetValue(key, out var sample)) return false;

            // Wwise does not stack the infinite Mirage Blade loop with itself.
            // Re-triggering any named event restarts that event from frame zero.
            _sampleVoices.RemoveAll(voice => voice.Key == key);
            _sampleVoices.Add(new SampleVoice(
                key,
                sample,
                position: -sample.DelayFrames,
                gain: sample.Gain * _strength));
            return true;
        }
    }

    public void StopOriginalHaptics()
    {
        lock (_gate) _sampleVoices.Clear();
    }

    public void Pulse(
        float lowMotor,
        float highMotor,
        float durationSeconds,
        float lowFrequency = 72f,
        float highFrequency = 162f)
    {
        lowMotor = Math.Clamp(lowMotor, 0f, 1f);
        highMotor = Math.Clamp(highMotor, 0f, 1f);
        durationSeconds = Math.Clamp(durationSeconds <= 0 ? 0.08f : durationSeconds, 0.025f, 1.5f);

        lock (_gate)
        {
            _voices.Add(new Voice(
                remainingSamples: (int)(SampleRate * durationSeconds),
                totalSamples: (int)(SampleRate * durationSeconds),
                lowAmplitude: lowMotor * _strength,
                highAmplitude: highMotor * _strength,
                lowPhase: 0,
                highPhase: 0,
                lowFrequency: Math.Clamp(lowFrequency, 30f, 220f),
                highFrequency: Math.Clamp(highFrequency, 40f, 320f)));

            if (_voices.Count > 24)
                _voices.RemoveRange(0, _voices.Count - 24);
        }
    }

    public void RumblePulse(float lowMotor, float highMotor, float durationSeconds)
    {
        lock (_gate) _rumble.Pulse(lowMotor, highMotor, durationSeconds);
    }

    public void Impact(float amount = 1f)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        Pulse(amount, amount * 0.72f, 0.12f);
    }

    public void FromGamePadShake(int motor, float power, float durationSeconds)
    {
        lock (_gate)
        {
            // The low-level motor hook supplies the exact live envelope. PadShake is
            // retained as a fallback for code paths that do not reach setMotorPower.
            if (_rumble.HasRecentGameMotor(TimeSpan.FromMilliseconds(120)))
                return;
            power = Math.Clamp(power, 0f, 1f);
            switch (motor)
            {
                case 1: // BothMotor
                    _rumble.Pulse(power, power, durationSeconds);
                    break;
                case 2: // LowMotor
                    _rumble.Pulse(power, 0, durationSeconds);
                    break;
                case 3: // HigtMotor (spelling used by DMC5 metadata)
                    _rumble.Pulse(0, power, durationSeconds);
                    break;
            }
        }
    }

    public RumbleOutput GetRumbleOutput()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            return HapticOutputSafety.Arbitrate(
                _rumble.GetOutput(now), now < _advancedHapticsUntilUtc);
        }
    }

    public void WeaponHit(string character, float amount = 1f)
    {
        amount = Math.Clamp(amount, 0.2f, 1f);
        switch (character.ToLowerInvariant())
        {
            case "nero":
                Pulse(0.62f * amount, 0.86f * amount, 0.105f, 76f, 205f);
                break;
            case "dante":
                Pulse(0.90f * amount, 0.68f * amount, 0.125f, 61f, 178f);
                break;
            case "v":
                Pulse(0.38f * amount, 0.93f * amount, 0.115f, 96f, 238f);
                break;
            case "vergil":
                Pulse(0.50f * amount, 1.00f * amount, 0.095f, 88f, 255f);
                break;
            default:
                Pulse(0.64f * amount, 0.78f * amount, 0.11f, 72f, 200f);
                break;
        }
    }

    public void SetGameMotor(int motor, float power)
    {
        lock (_gate) _rumble.SetGameMotor(motor, power);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        var bytesPerFrame = sizeof(short) * ChannelCount;
        var frameCount = count / bytesPerFrame;

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var priorityUntil = now.AddSeconds(frameCount / (double)SampleRate)
                .AddMilliseconds(50);
            for (var frame = 0; frame < frameCount; frame++)
            {
                double left = 0;
                double right = 0;
                var advancedFrame = MixGeneratedVoices(ref left, ref right);
                advancedFrame |= MixOriginalSamples(ref left, ref right);
                if (advancedFrame) _advancedHapticsUntilUtc = priorityUntil;
                if (Math.Abs(left) > 0.90 || Math.Abs(right) > 0.90) _limitedFrames++;

                var leftSample = ToInt16(HapticOutputSafety.SoftLimit(left));
                var rightSample = ToInt16(HapticOutputSafety.SoftLimit(right));
                var target = offset + frame * bytesPerFrame;

                _renderedFrames++;
                if (leftSample != 0 || rightSample != 0) _nonZeroRenderedFrames++;
                _renderPeak = Math.Max(_renderPeak,
                    Math.Max(Math.Abs(leftSample / 32768f), Math.Abs(rightSample / 32768f)));

                // Channels 1/2 are the controller speaker and remain silent.
                // Channels 3/4 drive the left/right voice-coil haptic actuators.
                WriteInt16(buffer, target + 0, 0);
                WriteInt16(buffer, target + 2, 0);
                WriteInt16(buffer, target + 4, leftSample);
                WriteInt16(buffer, target + 6, rightSample);
            }
        }

        return count;
    }

    public AudioRenderDiagnostic GetAndResetRenderDiagnostic()
    {
        lock (_gate)
        {
            var diagnostic = new AudioRenderDiagnostic(
                _renderedFrames,
                _nonZeroRenderedFrames,
                _limitedFrames,
                _renderPeak,
                _output?.PlaybackState.ToString() ?? "Stopped");
            _renderedFrames = 0;
            _nonZeroRenderedFrames = 0;
            _limitedFrames = 0;
            _renderPeak = 0;
            return diagnostic;
        }
    }

    private bool MixGeneratedVoices(ref double left, ref double right)
    {
        var active = false;
        for (var index = _voices.Count - 1; index >= 0; index--)
        {
            var voice = _voices[index];
            var progress = 1.0 - (double)voice.RemainingSamples / voice.TotalSamples;
            var envelope = Math.Pow(Math.Max(0.0, 1.0 - progress), 1.7);

            voice.LowPhase += 2.0 * Math.PI * voice.LowFrequency / SampleRate;
            voice.HighPhase += 2.0 * Math.PI * voice.HighFrequency / SampleRate;

            var low = Math.Sin(voice.LowPhase) * voice.LowAmplitude;
            var high = Math.Sin(voice.HighPhase) * voice.HighAmplitude;
            var addLeft = (low + high * 0.62) * envelope * 0.64;
            var addRight = (low * 0.88 + high * 0.78) * envelope * 0.64;
            left += addLeft;
            right += addRight;
            active |= Math.Abs(addLeft) > 0.0001 || Math.Abs(addRight) > 0.0001;

            voice.RemainingSamples--;
            if (voice.RemainingSamples <= 0)
                _voices.RemoveAt(index);
            else
                _voices[index] = voice;
        }
        return active;
    }

    private bool MixOriginalSamples(ref double left, ref double right)
    {
        var active = false;
        for (var index = _sampleVoices.Count - 1; index >= 0; index--)
        {
            var voice = _sampleVoices[index];
            if (voice.Position < 0)
            {
                voice.Position += 1;
                _sampleVoices[index] = voice;
                continue;
            }

            var sample = voice.Sample;
            var frameCount = sample.Interleaved.Length / sample.Channels;
            if (frameCount == 0)
            {
                _sampleVoices.RemoveAt(index);
                continue;
            }

            if (voice.Position >= frameCount)
            {
                if (!sample.Loop)
                {
                    _sampleVoices.RemoveAt(index);
                    continue;
                }
                voice.Position %= frameCount;
            }

            var frame0 = Math.Clamp((int)voice.Position, 0, frameCount - 1);
            var frame1 = sample.Loop
                ? (frame0 + 1) % frameCount
                : Math.Min(frame0 + 1, frameCount - 1);
            var fraction = voice.Position - frame0;

            var sourceLeft = Interpolate(sample, frame0, frame1, 0, fraction);
            var sourceRight = sample.Channels == 1
                ? sourceLeft
                : Interpolate(sample, frame0, frame1, 1, fraction);

            var addLeft = sourceLeft * voice.Gain;
            var addRight = sourceRight * voice.Gain;
            left += addLeft;
            right += addRight;
            active |= Math.Abs(addLeft) > 0.0001 || Math.Abs(addRight) > 0.0001;
            voice.Position += sample.PlaybackRate;
            _sampleVoices[index] = voice;
        }
        return active;
    }

    private static float Interpolate(
        SampleDefinition sample,
        int frame0,
        int frame1,
        int channel,
        double amount)
    {
        var a = sample.Interleaved[frame0 * sample.Channels + channel];
        var b = sample.Interleaved[frame1 * sample.Channels + channel];
        return (float)(a + (b - a) * amount);
    }

    private static Dictionary<string, SampleDefinition> LoadOriginalSamples()
    {
        var specs = new[]
        {
            new SampleSpec(0, "coyote_shot_shell", "87828053.wav", 3f),
            new SampleSpec(1, "bluerose_shot_shell", "683314104.wav", 0f),
            new SampleSpec(2, "jr_jigenzan_shot_shell", "297926011.wav", 5f),
            new SampleSpec(3, "evony_shot_shell", "511441928.wav", 2f),
            new SampleSpec(4, "ivory_shot_shell", "1040252522.wav", 2f),
            new SampleSpec(5, "jigenzan_shot_shell", "193630586.wav", -1f, 300f),
            new SampleSpec(6, "beo_sp_impact", "752139616.wav", 8f, DelaySeconds: 0.1f),
            new SampleSpec(7, "mirage_sp_loop", "310261087.wav", 5f, Loop: true),
            new SampleSpec(8, "mirage_sp_end", "748704802.wav", 6f),
            new SampleSpec(9, "beo_sp_pre", "317387691.wav", 0f, -250f, 0.3f),
            new SampleSpec(10, "yamato_zetsu_return", "564764444.wav", -96f),
            new SampleSpec(11, "yamato_zetsu_noutou", "726668428.wav", 1f)
        };

        var result = new Dictionary<string, SampleDefinition>(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var spec in specs)
        {
            var resourceName = $"DMC5DualSense.Bridge.Assets.Haptics.{spec.FileName}";
            using var resource = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded PS5 haptic resource is missing: {resourceName}");
            using var reader = new WaveFileReader(resource);
            if (reader.WaveFormat.SampleRate != SampleRate || reader.WaveFormat.Channels is < 1 or > 2)
                throw new InvalidOperationException(
                    $"Unsupported PS5 haptic format in {spec.FileName}: {reader.WaveFormat}");

            var provider = reader.ToSampleProvider();
            var samples = new List<float>((int)(reader.Length / 2));
            var block = new float[8192];
            int read;
            while ((read = provider.Read(block, 0, block.Length)) > 0)
                samples.AddRange(block.AsSpan(0, read).ToArray());

            if (samples.Count == 0 || samples.Any(value => !float.IsFinite(value)))
                throw new InvalidOperationException($"Invalid PS5 haptic samples in {spec.FileName}.");

            result[spec.Key] = new SampleDefinition(
                spec.Index,
                spec.Key,
                spec.FileName,
                samples.ToArray(),
                reader.WaveFormat.Channels,
                DbToLinear(spec.GainDb),
                spec.GainDb,
                Math.Pow(2.0, spec.PitchCents / 1200.0),
                spec.PitchCents,
                (int)Math.Round(spec.DelaySeconds * SampleRate),
                spec.Loop);
        }

        return result;
    }

    private static string NormalizeEventName(string name)
    {
        var normalized = name.Trim().Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
        return normalized switch
        {
            "blue_rose_shot" or "bluerose_shot" => "bluerose_shot_shell",
            "dante_coyote_shot" => "coyote_shot_shell",
            "dante_evony_shot" or "dante_ebony_shot" => "evony_shot_shell",
            "dante_ivory_shot" => "ivory_shot_shell",
            "judgement_cut" or "jigenzan_shot" => "jigenzan_shot_shell",
            "judgement_cut_jr" or "jr_jigenzan_shot" => "jr_jigenzan_shot_shell",
            "beowulf_pre" => "beo_sp_pre",
            "beowulf_impact" => "beo_sp_impact",
            "mirage_loop" => "mirage_sp_loop",
            "mirage_end" => "mirage_sp_end",
            "yamato_return" or "judgement_cut_end" => "yamato_zetsu_return",
            "yamato_noutou" => "yamato_zetsu_noutou",
            "stop" or "stopall" => "stop_all",
            _ => normalized
        };
    }

    private static float DbToLinear(float decibels) =>
        (float)Math.Pow(10.0, decibels / 20.0);

    private static short ToInt16(double value) =>
        (short)Math.Clamp((int)Math.Round(value * short.MaxValue), short.MinValue, short.MaxValue);

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    public void Dispose()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        RestoreEndpointVolume();
        _audioDevice?.Dispose();
        _audioDevice = null;
    }

    private readonly record struct SampleSpec(
        int Index,
        string Key,
        string FileName,
        float GainDb,
        float PitchCents = 0,
        float DelaySeconds = 0,
        bool Loop = false);

    private sealed record SampleDefinition(
        int Index,
        string Key,
        string FileName,
        float[] Interleaved,
        int Channels,
        float Gain,
        float GainDb,
        double PlaybackRate,
        float PitchCents,
        int DelayFrames,
        bool Loop);

    public readonly record struct OriginalSampleDiagnostic(
        int Index,
        string Key,
        string FileName,
        int Channels,
        double DurationSeconds,
        float GainDb,
        float PitchCents,
        double DelaySeconds,
        bool Loop,
        float SourcePeak);

    public readonly record struct AudioRenderDiagnostic(
        long Frames,
        long NonZeroFrames,
        long LimitedFrames,
        float Peak,
        string PlaybackState);

    private struct SampleVoice(
        string key,
        SampleDefinition sample,
        double position,
        float gain)
    {
        public readonly string Key = key;
        public readonly SampleDefinition Sample = sample;
        public double Position = position;
        public readonly float Gain = gain;
    }

    private struct Voice(
        int remainingSamples,
        int totalSamples,
        float lowAmplitude,
        float highAmplitude,
        double lowPhase,
        double highPhase,
        float lowFrequency,
        float highFrequency)
    {
        public int RemainingSamples = remainingSamples;
        public readonly int TotalSamples = totalSamples;
        public readonly float LowAmplitude = lowAmplitude;
        public readonly float HighAmplitude = highAmplitude;
        public double LowPhase = lowPhase;
        public double HighPhase = highPhase;
        public readonly float LowFrequency = lowFrequency;
        public readonly float HighFrequency = highFrequency;
    }
}

internal readonly record struct AudioEndpointMatch(int Score, string Reason);
