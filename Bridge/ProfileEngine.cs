namespace DMC5DualSense.Bridge;

internal static class ProfileEngine
{
    public static ControllerOutput Build(
        GameState state,
        BridgeConfig config,
        double seconds,
        TriggerEffect left,
        TriggerEffect right)
    {
        var character = state.IsFresh && state.InGameplay
            ? state.Character.ToLowerInvariant()
            : "unknown";

        var color = CharacterColor(character);
        var intensity = config.EnableLightbar
            ? Math.Clamp(config.LightbarStrength, 0f, 1f)
            : 0f;

        if (config.AdaptiveProfile.Equals("Enhanced", StringComparison.OrdinalIgnoreCase) &&
            state.IsFresh && state.InGameplay && state.HealthRatio < 0.25f)
        {
            var pulse = 0.35f + 0.65f * (float)((Math.Sin(seconds * Math.PI * 3.0) + 1.0) * 0.5);
            color = Blend(color, (255, 0, 0), pulse * (1f - state.HealthRatio * 2f));
        }

        return new ControllerOutput(
            left,
            right,
            Scale(color.r, intensity),
            Scale(color.g, intensity),
            Scale(color.b, intensity));
    }

    private static (byte r, byte g, byte b) CharacterColor(string character) => character switch
    {
        // Exact RGB table from PS5 app::PlayerManager::setManualPlayerLightBar.
        "nero" => (0, 0, 220),
        "dante" => (195, 0, 0),
        "v" => (120, 0, 255),
        "vergil" => (0, 255, 160),
        _ => (200, 200, 200)
    };

    private static (byte r, byte g, byte b) Blend(
        (byte r, byte g, byte b) a,
        (byte r, byte g, byte b) b,
        float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return (
            (byte)(a.r + (b.r - a.r) * amount),
            (byte)(a.g + (b.g - a.g) * amount),
            (byte)(a.b + (b.b - a.b) * amount));
    }

    private static byte Scale(byte value, float amount) =>
        (byte)Math.Clamp((int)Math.Round(value * Math.Clamp(amount, 0f, 1f)), 0, 255);
}
