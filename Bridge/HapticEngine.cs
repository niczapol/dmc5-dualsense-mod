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
    private WasapiOut? _output;
    private MMDevice? _audioDevice;
    private string _status = "disabled";
    private float _continuousLow;
    private float _continuousHigh;
    private double _continuousLowPhase;
    private double _continuousHighPhase;
    private DateTime _continuousUntilUtc;
    private DateTime _lastMotorSignalUtc;
    private float _transientRumbleLow;
    private float _transientRumbleHigh;
    private DateTime _transientRumbleStartUtc;
    private DateTime _transientRumbleUntilUtc;

    public HapticEngine(float strength)
    {
        _strength = Math.Clamp(strength, 0f, 1f);
        WaveFormat = new WaveFormatExtensible(SampleRate, 16, ChannelCount);
        _samples = LoadOriginalSamples();
    }

    public WaveFormat WaveFormat { get; }
    public string Status => _status;
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

    public bool Start(string deviceNameFragment)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _audioDevice = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(device =>
                    device.FriendlyName.Contains(deviceNameFragment, StringComparison.OrdinalIgnoreCase));

            if (_audioDevice is null)
            {
                _status = "DualSense 4-channel audio endpoint not found";
                return false;
            }

            _output = new WasapiOut(_audioDevice, AudioClientShareMode.Shared, true, 20);
            _output.Init(this);
            _output.Play();
            _status = $"{_audioDevice.FriendlyName}; {_samples.Count}/12 original PS5 samples";
            return true;
        }
        catch (Exception ex)
        {
            _status = ex.Message;
            _output?.Dispose();
            _output = null;
            _audioDevice?.Dispose();
            _audioDevice = null;
            return false;
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
            var now = DateTime.UtcNow;
            _transientRumbleLow = Math.Max(_transientRumbleLow, lowMotor * _strength);
            _transientRumbleHigh = Math.Max(_transientRumbleHigh, highMotor * _strength);
            _transientRumbleStartUtc = now;
            _transientRumbleUntilUtc = now.AddSeconds(durationSeconds);

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
            if (DateTime.UtcNow - _lastMotorSignalUtc < TimeSpan.FromMilliseconds(120))
                return;
        }

        power = Math.Clamp(power, 0f, 1f);
        switch (motor)
        {
            case 1: // BothMotor
                Pulse(power, power, durationSeconds);
                break;
            case 2: // LowMotor
                Pulse(power, 0, durationSeconds);
                break;
            case 3: // HigtMotor (spelling used by DMC5 metadata)
                Pulse(0, power, durationSeconds);
                break;
        }
    }

    public (byte Low, byte High) GetRumbleOutput()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var low = 0f;
            var high = 0f;

            if (now < _transientRumbleUntilUtc)
            {
                var total = Math.Max(0.001,
                    (_transientRumbleUntilUtc - _transientRumbleStartUtc).TotalSeconds);
                var remaining = Math.Clamp(
                    (_transientRumbleUntilUtc - now).TotalSeconds / total, 0.0, 1.0);
                var envelope = (float)Math.Sqrt(remaining);
                low = _transientRumbleLow * envelope;
                high = _transientRumbleHigh * envelope;
            }
            else
            {
                _transientRumbleLow = 0;
                _transientRumbleHigh = 0;
            }

            if (now < _continuousUntilUtc)
            {
                low = Math.Max(low, _continuousLow * _strength);
                high = Math.Max(high, _continuousHigh * _strength);
            }

            return (
                (byte)Math.Clamp((int)Math.Round(low * 255f), 0, 255),
                (byte)Math.Clamp((int)Math.Round(high * 255f), 0, 255));
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
        power = Math.Clamp(power, 0f, 1f);
        lock (_gate)
        {
            switch (motor)
            {
                case 0:
                case 128: // LowFrequencyMotor
                    _continuousLow = power;
                    break;
                case 1:
                case 129: // HighFrequencyMotor
                    _continuousHigh = power;
                    break;
                case 2:
                case 130: // LAnalogTriggerMotor on platforms that expose it
                    _continuousLow = power * 0.55f;
                    break;
                case 3:
                case 131: // RAnalogTriggerMotor
                    _continuousHigh = power * 0.55f;
                    break;
                default:
                    return;
            }

            _lastMotorSignalUtc = DateTime.UtcNow;
            // DMC5 does not consistently emit a final zero command. Repeated live
            // motor packets arrive at least every 120 ms, so a short watchdog
            // removes the otherwise multi-second rumble tail after an attack.
            _continuousUntilUtc = _lastMotorSignalUtc.AddMilliseconds(180);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        var bytesPerFrame = sizeof(short) * ChannelCount;
        var frameCount = count / bytesPerFrame;

        lock (_gate)
        {
            var continuousActive = DateTime.UtcNow < _continuousUntilUtc &&
                                   (_continuousLow > 0.0001f || _continuousHigh > 0.0001f);
            for (var frame = 0; frame < frameCount; frame++)
            {
                double left = 0;
                double right = 0;

                if (continuousActive)
                {
                    _continuousLowPhase += 2.0 * Math.PI * 72.0 / SampleRate;
                    _continuousHighPhase += 2.0 * Math.PI * 162.0 / SampleRate;
                    var low = Math.Sin(_continuousLowPhase) * _continuousLow * _strength;
                    var high = Math.Sin(_continuousHighPhase) * _continuousHigh * _strength;
                    left += (low + high * 0.56) * 0.64;
                    right += (low * 0.86 + high * 0.82) * 0.64;
                }

                MixGeneratedVoices(ref left, ref right);
                MixOriginalSamples(ref left, ref right);

                var leftSample = ToInt16(left);
                var rightSample = ToInt16(right);
                var target = offset + frame * bytesPerFrame;

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

    private void MixGeneratedVoices(ref double left, ref double right)
    {
        for (var index = _voices.Count - 1; index >= 0; index--)
        {
            var voice = _voices[index];
            var progress = 1.0 - (double)voice.RemainingSamples / voice.TotalSamples;
            var envelope = Math.Pow(Math.Max(0.0, 1.0 - progress), 1.7);

            voice.LowPhase += 2.0 * Math.PI * voice.LowFrequency / SampleRate;
            voice.HighPhase += 2.0 * Math.PI * voice.HighFrequency / SampleRate;

            var low = Math.Sin(voice.LowPhase) * voice.LowAmplitude;
            var high = Math.Sin(voice.HighPhase) * voice.HighAmplitude;
            left += (low + high * 0.62) * envelope * 0.64;
            right += (low * 0.88 + high * 0.78) * envelope * 0.64;

            voice.RemainingSamples--;
            if (voice.RemainingSamples <= 0)
                _voices.RemoveAt(index);
            else
                _voices[index] = voice;
        }
    }

    private void MixOriginalSamples(ref double left, ref double right)
    {
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

            left += sourceLeft * voice.Gain;
            right += sourceRight * voice.Gain;
            voice.Position += sample.PlaybackRate;
            _sampleVoices[index] = voice;
        }
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
        _audioDevice?.Dispose();
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
