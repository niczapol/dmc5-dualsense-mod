using System.Text.Json.Serialization;

namespace DMC5DualSense.Bridge;

internal sealed class BridgeMessage
{
    [JsonPropertyName("v")]
    public int Version { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("character")]
    public string Character { get; set; } = "unknown";

    [JsonPropertyName("inGameplay")]
    public bool InGameplay { get; set; }

    [JsonPropertyName("hp")]
    public float Health { get; set; }

    [JsonPropertyName("maxHp")]
    public float MaxHealth { get; set; }

    [JsonPropertyName("motionBank")]
    public uint MotionBank { get; set; }

    [JsonPropertyName("motionId")]
    public uint MotionId { get; set; }

    [JsonPropertyName("motionFrame")]
    public float MotionFrame { get; set; }

    [JsonPropertyName("left")]
    public float Left { get; set; }

    [JsonPropertyName("right")]
    public float Right { get; set; }

    [JsonPropertyName("duration")]
    public float Duration { get; set; }

    [JsonPropertyName("motor")]
    public int Motor { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public float Value { get; set; }

    [JsonPropertyName("exceedGauge")]
    public float ExceedGauge { get; set; }

    [JsonPropertyName("exceedGaugeMax")]
    public float ExceedGaugeMax { get; set; }

    [JsonPropertyName("exceedStock")]
    public int ExceedStock { get; set; }

    [JsonPropertyName("exceedRequest")]
    public bool ExceedRequest { get; set; }

    [JsonPropertyName("exceedRequestValue")]
    public float ExceedRequestValue { get; set; }

    [JsonPropertyName("blueRoseChargeLevel")]
    public int BlueRoseChargeLevel { get; set; }

    [JsonPropertyName("blueRoseTimer")]
    public float BlueRoseTimer { get; set; }

    [JsonPropertyName("danteWeaponId")]
    public int DanteWeaponId { get; set; } = -1;
}

internal sealed record GameState(
    string Character,
    bool InGameplay,
    float Health,
    float MaxHealth,
    uint MotionBank,
    uint MotionId,
    float MotionFrame,
    float ExceedGauge,
    float ExceedGaugeMax,
    int ExceedStock,
    bool ExceedRequest,
    float ExceedRequestValue,
    int BlueRoseChargeLevel,
    float BlueRoseTimer,
    int DanteWeaponId,
    float TriggerLeft,
    float TriggerRight,
    DateTime LastSeenUtc)
{
    public static GameState Empty { get; } = new(
        "unknown", false, 0, 0, 0, 0, 0,
        0, 0, 0, false, 0, 0, 0, -1, 0, 0, DateTime.MinValue);

    public bool IsFresh => DateTime.UtcNow - LastSeenUtc < TimeSpan.FromSeconds(2);
    public float HealthRatio => MaxHealth > 0 ? Math.Clamp(Health / MaxHealth, 0f, 1f) : 1f;
    public float ExceedRatio => ExceedGaugeMax > 0
        ? Math.Clamp(ExceedGauge / ExceedGaugeMax, 0f, 1f)
        : 0f;
}
