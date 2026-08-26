#pragma once

#include <cstdint>
#include <filesystem>
#include <memory>
#include <string>

namespace dmc5ds {

struct RumbleOutput {
    std::uint8_t low{};
    std::uint8_t high{};
};

struct AudioRenderDiagnostic {
    std::uint64_t frames{};
    std::uint64_t non_zero_frames{};
    float peak{};
    std::string state;
};

class HapticEngine {
public:
    explicit HapticEngine(float strength);
    ~HapticEngine();
    HapticEngine(const HapticEngine&) = delete;
    HapticEngine& operator=(const HapticEngine&) = delete;

    bool start(const std::string& device_name_fragment, bool ensure_endpoint_audible,
               float endpoint_volume, const std::filesystem::path& sample_directory);
    bool started() const;
    std::string status() const;
    std::size_t original_sample_count() const;

    bool play_original(const std::string& event_name);
    void stop_original();
    void pulse(float low, float high, float duration_seconds,
               float low_frequency = 72.0F, float high_frequency = 162.0F);
    void impact(float amount = 1.0F);
    void from_game_pad_shake(int motor, float power, float duration_seconds);
    void weapon_hit(const std::string& character, float amount = 1.0F);
    void set_game_motor(int motor, float power);
    RumbleOutput rumble_output();
    AudioRenderDiagnostic take_render_diagnostic();

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace dmc5ds
