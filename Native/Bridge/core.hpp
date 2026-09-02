#pragma once

#include <array>
#include <chrono>
#include <cstdint>
#include <mutex>
#include <string>
#include <string_view>
#include <utility>

namespace dmc5ds {

bool matches_dualsense_audio_endpoint(
    std::wstring_view friendly_name,
    std::wstring_view configured_fragment);

struct AudioEndpointMatch {
    int score{};
    std::string_view reason;
};

AudioEndpointMatch classify_dualsense_audio_endpoint(
    std::wstring_view friendly_name,
    std::wstring_view configured_fragment,
    std::wstring_view controller_device_id,
    std::wstring_view interface_key,
    int channel_count);

enum class TriggerMode : std::uint8_t {
    off = 0x05,
    feedback = 0x21,
    weapon = 0x25,
    vibration = 0x26
};

struct TriggerEffect {
    TriggerMode mode{TriggerMode::off};
    std::uint8_t position{};
    std::uint8_t strength{};
    std::uint8_t end_position{};
    std::uint8_t frequency{};

    static TriggerEffect off();
    static TriggerEffect feedback(std::uint8_t position, std::uint8_t strength);
    static TriggerEffect weapon(std::uint8_t start, std::uint8_t end, std::uint8_t strength);
    static TriggerEffect vibration(std::uint8_t position, std::uint8_t amplitude,
                                   std::uint8_t frequency);

    bool operator==(const TriggerEffect&) const = default;
};

struct ControllerOutput {
    TriggerEffect left_trigger{};
    TriggerEffect right_trigger{};
    std::uint8_t red{};
    std::uint8_t green{};
    std::uint8_t blue{};
    std::uint8_t player_leds{0x04};
    std::uint8_t left_rumble{};
    std::uint8_t right_rumble{};
};

struct RumbleOutput {
    std::uint8_t low{};
    std::uint8_t high{};

    bool operator==(const RumbleOutput&) const = default;
};

class RumbleRuntime {
public:
    using Clock = std::chrono::steady_clock;
    using TimePoint = Clock::time_point;

    explicit RumbleRuntime(float strength = 1.0F);

    void set_game_motor(int motor, float power,
                        TimePoint now = Clock::now());
    void pulse(float low, float high, float duration_seconds,
               TimePoint now = Clock::now());
    bool has_recent_game_motor(
        std::chrono::milliseconds age,
        TimePoint now = Clock::now()) const;
    RumbleOutput output(TimePoint now = Clock::now());

private:
    struct TimedMotor {
        float power{};
        TimePoint until{};
    };

    struct TransientMotor {
        float power{};
        TimePoint start{};
        TimePoint until{};
    };

    static int normalize_motor(int motor);
    static float transient_value(TransientMotor& motor, TimePoint now);

    float strength_{};
    std::array<TimedMotor, 4> motors_{};
    TransientMotor transient_low_{};
    TransientMotor transient_high_{};
    TimePoint last_motor_signal_{};
};

// Advanced haptics are mixed in floating point. This limiter leaves ordinary
// signal levels untouched and smoothly contains overlapping event peaks before
// conversion to the controller's 16-bit actuator stream.
double soft_limit_haptic(double value);

RumbleOutput arbitrate_rumble(RumbleOutput ordinary, bool advanced_haptics_active);

struct BridgeConfig {
    int port{27105};
    std::string adaptive_profile{"Authentic"};
    float trigger_strength{1.0F};
    float haptics_strength{1.0F};
    float lightbar_strength{1.0F};
    bool enable_adaptive_triggers{true};
    bool enable_advanced_haptics{true};
    bool enable_lightbar{true};
    bool enable_calibration_log{false};
    std::string audio_device_contains{"DualSense Wireless Controller"};
    bool ensure_haptics_endpoint_audible{true};
    float haptics_endpoint_volume{1.0F};
};

struct GameState {
    std::string character{"unknown"};
    bool in_gameplay{};
    float health{};
    float max_health{};
    std::uint32_t motion_bank{};
    std::uint32_t motion_id{};
    float motion_frame{};
    float exceed_gauge{};
    float exceed_gauge_max{};
    int exceed_stock{};
    bool exceed_request{};
    float exceed_request_value{};
    int blue_rose_charge_level{};
    float blue_rose_timer{};
    int dante_weapon_id{-1};
    int attack_large_button{-1};
    int special2_button{-1};
    float trigger_left{};
    float trigger_right{};
    std::chrono::steady_clock::time_point last_seen{};

    bool is_fresh(std::chrono::steady_clock::time_point now) const;
    float health_ratio() const;
    float exceed_ratio() const;
};

struct XInputSnapshot {
    bool connected{};
    float left_trigger{};
    float right_trigger{};
};

class AdaptiveTriggerRuntime {
public:
    void update_bindings(const std::string& character, int attack_large_button,
                         int special2_button);
    std::pair<TriggerEffect, TriggerEffect> build(
        const GameState& state, const BridgeConfig& config,
        const XInputSnapshot& input,
        std::chrono::steady_clock::time_point now = std::chrono::steady_clock::now());

    std::string exceed_mapping() const;
    std::string nero_attack_large_mapping() const;
    std::string dante_attack_large_mapping() const;

private:
    enum class TriggerSide { none, left, right };

    static TriggerSide from_button(int button);
    static float read(TriggerSide side, const XInputSnapshot& input);
    static void apply(TriggerEffect& left, TriggerEffect& right, TriggerSide side,
                      const TriggerEffect& effect);
    static std::uint8_t scale_level(std::uint8_t level, float amount);
    static TriggerEffect dante_attack_large_effect(int weapon_id, float strength);
    static std::string side_name(TriggerSide side);

    mutable std::mutex mutex_;
    TriggerSide exceed_side_{TriggerSide::none};
    TriggerSide nero_attack_large_side_{TriggerSide::none};
    TriggerSide dante_attack_large_side_{TriggerSide::none};
};

ControllerOutput build_profile(const GameState& state, const BridgeConfig& config,
                               double seconds, const TriggerEffect& left,
                               const TriggerEffect& right,
                               std::chrono::steady_clock::time_point now =
                                   std::chrono::steady_clock::now());

inline constexpr std::size_t kSteamTriggerPayloadSize = 120;
inline constexpr std::size_t kSteamLeftCommandOffset = 8;
inline constexpr std::size_t kSteamRightCommandOffset = 64;

std::array<std::uint8_t, kSteamTriggerPayloadSize> build_steam_trigger_payload(
    const TriggerEffect& left, const TriggerEffect& right);

} // namespace dmc5ds
