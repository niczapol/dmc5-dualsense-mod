#include "../Bridge/core.hpp"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string_view>

using namespace dmc5ds;

namespace {

int failures{};

void check(bool condition, std::string_view name) {
    if (condition) std::printf("PASS %.*s\n", static_cast<int>(name.size()), name.data());
    else {
        std::fprintf(stderr, "FAIL %.*s\n", static_cast<int>(name.size()), name.data());
        ++failures;
    }
}

GameState state(std::string character = "nero", int weapon_id = -1) {
    GameState result{};
    result.character = std::move(character);
    result.in_gameplay = true;
    result.health = result.max_health = 100.0F;
    result.dante_weapon_id = weapon_id;
    result.last_seen = std::chrono::steady_clock::now();
    return result;
}

std::int32_t read_i32(const auto& payload, std::size_t offset) {
    std::int32_t value{};
    std::memcpy(&value, payload.data() + offset, sizeof(value));
    return value;
}

} // namespace

int main() {
    BridgeConfig config{};

    check(matches_dualsense_audio_endpoint(
              L"Динамики (DualSense Wireless Controller)",
              L"DualSense Wireless Controller"),
          "Localized CFI-ZCT1/CFI-ZCT2 audio endpoint is accepted");
    check(matches_dualsense_audio_endpoint(
              L"Speakers (DualSense Edge Wireless Controller)",
              L"DualSense Wireless Controller"),
          "DualSense Edge audio endpoint is accepted by family name");
    check(!matches_dualsense_audio_endpoint(
              L"Speakers (Realtek(R) Audio)",
              L"DualSense Wireless Controller"),
          "Unrelated audio endpoint is rejected");
    check(classify_dualsense_audio_endpoint(
              L"My custom controller audio", L"DualSense Wireless Controller",
              L"{1}.USB\\VID_054C&PID_0CE6&MI_00\\6&ABC&0&0000",
              L"{2}.\\\\?\\usb#vid_054c&pid_0ce6&mi_00#...", 4).score == 1200,
          "Renamed DualSense endpoint is accepted by USB hardware identity");
    check(classify_dualsense_audio_endpoint(
              L"Renamed gamepad", L"DualSense Wireless Controller",
              L"USB\\VID_054C&PID_FFFF&MI_00\\...", L"", 4).score == 900,
          "Future Sony four-channel controller needs no known product id");
    check(classify_dualsense_audio_endpoint(
              L"Surround speakers", L"DualSense Wireless Controller",
              L"USB\\VID_1234&PID_5678\\...", L"", 8).score == 0,
          "Unrelated four-channel endpoint is rejected without Sony hardware identity");

    {
        AdaptiveTriggerRuntime runtime;
        runtime.update_bindings("dante", 0x20, 0);
        const auto output = runtime.build(state("dante", 0), config, {true, 0, 1});
        check(output.first.mode == TriggerMode::off && output.second.mode == TriggerMode::off &&
              runtime.dante_attack_large_mapping() == "None",
              "Dante face-button gun mapping keeps both triggers free");
    }
    {
        AdaptiveTriggerRuntime runtime;
        runtime.update_bindings("dante", 0x0800, 0);
        const auto first = runtime.build(state("dante", 0), config, {true, 0, 1});
        const auto second = runtime.build(state("dante", 0), config, {true, 0, 1});
        check(first == second && second.second == TriggerEffect::weapon(4, 5, 4),
              "Repeated shots cannot mutate a live Dante binding");
    }
    {
        AdaptiveTriggerRuntime runtime;
        runtime.update_bindings("dante", 0x0800, 0);
        const auto output = runtime.build(state("dante", 1), config, {true, 0, 0});
        check(output.second == TriggerEffect::weapon(4, 8, 4) &&
              runtime.dante_attack_large_mapping() == "Right",
              "Dante resistance follows an explicit R2 remap");
    }
    {
        AdaptiveTriggerRuntime runtime;
        runtime.update_bindings("dante", 0x0800, 0);
        runtime.update_bindings("dante", 0x0200, 0);
        const auto output = runtime.build(state("dante", 5), config, {true, 0, 0});
        check(output.first == TriggerEffect::vibration(0, 4, 76) &&
              output.second.mode == TriggerMode::off,
              "Live remapping moves Dante resistance to L2");
    }
    {
        AdaptiveTriggerRuntime runtime;
        runtime.update_bindings("nero", 0x0800, 0x0200);
        const auto output = runtime.build(state(), config, {true, 0, .9F});
        check(output.first == TriggerEffect::vibration(0, 1, 76) &&
              output.second == TriggerEffect::weapon(4, 8, 4),
              "Nero bindings remain independent");
    }
    {
        const auto payload = build_steam_trigger_payload(
            TriggerEffect::off(), TriggerEffect::off());
        check(payload.size() == 120 && payload[0] == 3 &&
              read_i32(payload, kSteamLeftCommandOffset) == 0 &&
              read_i32(payload, kSteamRightCommandOffset) == 0,
              "Steam DualSense payload encodes both triggers off");
    }
    {
        const auto payload = build_steam_trigger_payload(
            TriggerEffect::vibration(1, 4, 76), TriggerEffect::off());
        const auto data = kSteamLeftCommandOffset + 8;
        check(read_i32(payload, kSteamLeftCommandOffset) == 3 &&
              payload[data] == 1 && payload[data + 1] == 4 && payload[data + 2] == 76,
              "Steam DualSense payload encodes vibration parameters");
    }
    {
        const auto payload = build_steam_trigger_payload(
            TriggerEffect::off(), TriggerEffect::weapon(4, 8, 5));
        const auto data = kSteamRightCommandOffset + 8;
        check(read_i32(payload, kSteamLeftCommandOffset) == 0 &&
              read_i32(payload, kSteamRightCommandOffset) == 2 &&
              payload[data] == 4 && payload[data + 1] == 8 && payload[data + 2] == 5,
              "Steam DualSense payload encodes an independent right weapon effect");
    }

    return failures == 0 ? EXIT_SUCCESS : EXIT_FAILURE;
}
