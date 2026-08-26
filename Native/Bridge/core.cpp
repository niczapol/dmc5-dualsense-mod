#include "core.hpp"

#include <algorithm>
#include <cmath>
#include <cctype>
#include <cwctype>

namespace dmc5ds {
namespace {

constexpr int kLeftTriggerButton = 0x0200;
constexpr int kRightTriggerButton = 0x0800;

std::string lower_ascii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return value;
}

std::uint8_t scale_byte(std::uint8_t value, float amount) {
    return static_cast<std::uint8_t>(std::clamp(
        static_cast<int>(std::nearbyint(value * std::clamp(amount, 0.0F, 1.0F))), 0, 255));
}

struct Color { std::uint8_t red{}, green{}, blue{}; };

Color character_color(const std::string& character) {
    if (character == "nero") return {0, 0, 220};
    if (character == "dante") return {195, 0, 0};
    if (character == "v") return {120, 0, 255};
    if (character == "vergil") return {0, 255, 160};
    return {200, 200, 200};
}

Color blend(Color a, Color b, float amount) {
    amount = std::clamp(amount, 0.0F, 1.0F);
    return {
        static_cast<std::uint8_t>(a.red + (b.red - a.red) * amount),
        static_cast<std::uint8_t>(a.green + (b.green - a.green) * amount),
        static_cast<std::uint8_t>(a.blue + (b.blue - a.blue) * amount)
    };
}

void write_i32(std::array<std::uint8_t, kSteamTriggerPayloadSize>& payload,
               std::size_t offset, std::int32_t value) {
    const auto raw = static_cast<std::uint32_t>(value);
    payload[offset] = static_cast<std::uint8_t>(raw);
    payload[offset + 1] = static_cast<std::uint8_t>(raw >> 8U);
    payload[offset + 2] = static_cast<std::uint8_t>(raw >> 16U);
    payload[offset + 3] = static_cast<std::uint8_t>(raw >> 24U);
}

void write_steam_trigger(
    std::array<std::uint8_t, kSteamTriggerPayloadSize>& payload,
    std::size_t command_offset, const TriggerEffect& effect) {
    constexpr std::size_t data_offset = 8;
    const auto data = command_offset + data_offset;
    switch (effect.mode) {
        case TriggerMode::feedback:
            if (effect.strength > 0) {
                write_i32(payload, command_offset, 1);
                payload[data] = std::clamp<std::uint8_t>(effect.position, 0, 9);
                payload[data + 1] = std::clamp<std::uint8_t>(effect.strength, 0, 8);
                return;
            }
            break;
        case TriggerMode::weapon:
            if (effect.strength > 0) {
                const auto start = std::clamp<std::uint8_t>(effect.position, 2, 7);
                const auto end = std::clamp<std::uint8_t>(
                    effect.end_position, static_cast<std::uint8_t>(start + 1), 8);
                write_i32(payload, command_offset, 2);
                payload[data] = start;
                payload[data + 1] = end;
                payload[data + 2] = std::clamp<std::uint8_t>(effect.strength, 0, 8);
                return;
            }
            break;
        case TriggerMode::vibration:
            if (effect.strength > 0 && effect.frequency > 0) {
                write_i32(payload, command_offset, 3);
                payload[data] = std::clamp<std::uint8_t>(effect.position, 0, 9);
                payload[data + 1] = std::clamp<std::uint8_t>(effect.strength, 0, 8);
                payload[data + 2] = effect.frequency;
                return;
            }
            break;
        default: break;
    }
    write_i32(payload, command_offset, 0);
}

} // namespace

bool matches_dualsense_audio_endpoint(
    std::wstring_view friendly_name,
    std::wstring_view configured_fragment) {
    auto lower = [](std::wstring_view value) {
        std::wstring result(value);
        std::transform(result.begin(), result.end(), result.begin(), [](wchar_t character) {
            return static_cast<wchar_t>(std::towlower(character));
        });
        return result;
    };

    const auto name = lower(friendly_name);
    const auto configured = lower(configured_fragment);
    if (!configured.empty() && name.find(configured) != std::wstring::npos) return true;

    // CFI-ZCT1/CFI-ZCT2 use "DualSense Wireless Controller" while the Edge
    // endpoint inserts "Edge" between those words. Match the product family
    // rather than a single revision-specific USB product string. The localized
    // Windows prefix (for example "Speakers" or "Динамики") is irrelevant.
    return name.find(L"dualsense") != std::wstring::npos &&
           name.find(L"wireless controller") != std::wstring::npos;
}

TriggerEffect TriggerEffect::off() { return {}; }
TriggerEffect TriggerEffect::feedback(std::uint8_t position, std::uint8_t strength) {
    return {TriggerMode::feedback, position, strength};
}
TriggerEffect TriggerEffect::weapon(std::uint8_t start, std::uint8_t end,
                                    std::uint8_t strength) {
    return {TriggerMode::weapon, start, strength, end};
}
TriggerEffect TriggerEffect::vibration(std::uint8_t position, std::uint8_t amplitude,
                                       std::uint8_t frequency) {
    return {TriggerMode::vibration, position, amplitude, 0, frequency};
}

bool GameState::is_fresh(std::chrono::steady_clock::time_point now) const {
    return last_seen.time_since_epoch().count() != 0 && now - last_seen < std::chrono::seconds(2);
}

float GameState::health_ratio() const {
    return max_health > 0.0F ? std::clamp(health / max_health, 0.0F, 1.0F) : 1.0F;
}

float GameState::exceed_ratio() const {
    return exceed_gauge_max > 0.0F
        ? std::clamp(exceed_gauge / exceed_gauge_max, 0.0F, 1.0F) : 0.0F;
}

void AdaptiveTriggerRuntime::update_bindings(const std::string& character,
                                             int attack_large_button,
                                             int special2_button) {
    std::scoped_lock lock(mutex_);
    const auto lower = lower_ascii(character);
    if (lower == "nero") {
        exceed_side_ = from_button(special2_button);
        nero_attack_large_side_ = from_button(attack_large_button);
    } else if (lower == "dante") {
        dante_attack_large_side_ = from_button(attack_large_button);
    }
}

std::pair<TriggerEffect, TriggerEffect> AdaptiveTriggerRuntime::build(
    const GameState& state, const BridgeConfig& config, const XInputSnapshot& input,
    std::chrono::steady_clock::time_point now) {
    if (!config.enable_adaptive_triggers || !state.is_fresh(now) || !state.in_gameplay)
        return {TriggerEffect::off(), TriggerEffect::off()};

    std::scoped_lock lock(mutex_);
    TriggerEffect left = TriggerEffect::off();
    TriggerEffect right = TriggerEffect::off();
    const float strength = std::clamp(config.trigger_strength, 0.0F, 1.0F);
    const auto character = lower_ascii(state.character);
    if (character == "nero") {
        const float analog = read(exceed_side_, input);
        const auto amplitude = static_cast<std::uint8_t>(std::clamp(
            static_cast<int>(std::max(analog * 0.5F, 0.2F) * 8.0F), 1, 4));
        apply(left, right, exceed_side_,
              TriggerEffect::vibration(0, scale_level(amplitude, strength), 76));
        apply(left, right, nero_attack_large_side_,
              TriggerEffect::weapon(4, 8, scale_level(4, strength)));
    } else if (character == "dante") {
        apply(left, right, dante_attack_large_side_,
              dante_attack_large_effect(state.dante_weapon_id, strength));
    }
    return {left, right};
}

std::string AdaptiveTriggerRuntime::exceed_mapping() const {
    std::scoped_lock lock(mutex_);
    return side_name(exceed_side_);
}
std::string AdaptiveTriggerRuntime::nero_attack_large_mapping() const {
    std::scoped_lock lock(mutex_);
    return side_name(nero_attack_large_side_);
}
std::string AdaptiveTriggerRuntime::dante_attack_large_mapping() const {
    std::scoped_lock lock(mutex_);
    return side_name(dante_attack_large_side_);
}

AdaptiveTriggerRuntime::TriggerSide AdaptiveTriggerRuntime::from_button(int button) {
    if (button == kLeftTriggerButton) return TriggerSide::left;
    if (button == kRightTriggerButton) return TriggerSide::right;
    return TriggerSide::none;
}

float AdaptiveTriggerRuntime::read(TriggerSide side, const XInputSnapshot& input) {
    if (side == TriggerSide::left) return std::clamp(input.left_trigger, 0.0F, 1.0F);
    if (side == TriggerSide::right) return std::clamp(input.right_trigger, 0.0F, 1.0F);
    return 0.0F;
}

void AdaptiveTriggerRuntime::apply(TriggerEffect& left, TriggerEffect& right,
                                   TriggerSide side, const TriggerEffect& effect) {
    if (side == TriggerSide::left) left = effect;
    if (side == TriggerSide::right) right = effect;
}

std::uint8_t AdaptiveTriggerRuntime::scale_level(std::uint8_t level, float amount) {
    const int minimum = level == 0 ? 0 : 1;
    return static_cast<std::uint8_t>(std::clamp(
        static_cast<int>(std::nearbyint(level * std::clamp(amount, 0.0F, 1.0F))),
        minimum, 8));
}

TriggerEffect AdaptiveTriggerRuntime::dante_attack_large_effect(int weapon_id,
                                                                 float strength) {
    switch (weapon_id) {
        case 0: return TriggerEffect::weapon(4, 5, scale_level(4, strength));
        case 1: return TriggerEffect::weapon(4, 8, scale_level(4, strength));
        case 2:
        case 3:
        case 4: return TriggerEffect::weapon(2, 8, scale_level(5, strength));
        case 5: return TriggerEffect::vibration(0, scale_level(4, strength), 76);
        default: return TriggerEffect::off();
    }
}

std::string AdaptiveTriggerRuntime::side_name(TriggerSide side) {
    if (side == TriggerSide::left) return "Left";
    if (side == TriggerSide::right) return "Right";
    return "None";
}

ControllerOutput build_profile(const GameState& state, const BridgeConfig& config,
                               double seconds, const TriggerEffect& left,
                               const TriggerEffect& right,
                               std::chrono::steady_clock::time_point now) {
    const std::string character = state.is_fresh(now) && state.in_gameplay
        ? lower_ascii(state.character) : "unknown";
    auto color = character_color(character);
    const float intensity = config.enable_lightbar
        ? std::clamp(config.lightbar_strength, 0.0F, 1.0F) : 0.0F;
    if (lower_ascii(config.adaptive_profile) == "enhanced" && state.is_fresh(now) &&
        state.in_gameplay && state.health_ratio() < 0.25F) {
        constexpr double pi = 3.14159265358979323846;
        const float pulse = 0.35F + 0.65F * static_cast<float>(
            (std::sin(seconds * pi * 3.0) + 1.0) * 0.5);
        color = blend(color, {255, 0, 0}, pulse * (1.0F - state.health_ratio() * 2.0F));
    }
    return {left, right, scale_byte(color.red, intensity),
            scale_byte(color.green, intensity), scale_byte(color.blue, intensity)};
}

std::array<std::uint8_t, kSteamTriggerPayloadSize> build_steam_trigger_payload(
    const TriggerEffect& left, const TriggerEffect& right) {
    std::array<std::uint8_t, kSteamTriggerPayloadSize> payload{};
    payload[0] = 0x03;
    write_steam_trigger(payload, kSteamLeftCommandOffset, left);
    write_steam_trigger(payload, kSteamRightCommandOffset, right);
    return payload;
}

} // namespace dmc5ds
