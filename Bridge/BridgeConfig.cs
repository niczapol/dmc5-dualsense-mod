using System.Text.Json;

namespace DMC5DualSense.Bridge;

internal sealed class BridgeConfig
{
    public int Port { get; set; } = 27105;
    public string AdaptiveProfile { get; set; } = "Authentic";
    public float TriggerStrength { get; set; } = 1.0f;
    public float HapticsStrength { get; set; } = 1.0f;
    public float LightbarStrength { get; set; } = 1.0f;
    public bool EnableAdaptiveTriggers { get; set; } = true;
    public bool EnableAdvancedHaptics { get; set; } = true;
    public bool EnableLightbar { get; set; } = true;
    public bool EnableCalibrationLog { get; set; }
    public string AudioDeviceContains { get; set; } = "DualSense Wireless Controller";
    public bool EnsureHapticsEndpointAudible { get; set; } = true;
    public float HapticsEndpointVolume { get; set; } = 1.0f;

    public static BridgeConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new BridgeConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
            return defaults;
        }

        return JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path), JsonOptions)
               ?? new BridgeConfig();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
