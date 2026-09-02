#include "core.hpp"
#include "haptics.hpp"
#include "platform.hpp"

#include <Windows.h>
#include <objbase.h>
#include <shellapi.h>
#include <winsock2.h>
#include <ws2tcpip.h>

#include <nlohmann/json.hpp>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iomanip>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

namespace dmc5ds {
namespace {

using json = nlohmann::json;
using Clock = std::chrono::steady_clock;

class ComApartment {
public:
    ComApartment() : result_(CoInitializeEx(nullptr, COINIT_MULTITHREADED)) {}
    ~ComApartment() {
        if (SUCCEEDED(result_)) CoUninitialize();
    }
    ComApartment(const ComApartment&) = delete;
    ComApartment& operator=(const ComApartment&) = delete;

private:
    HRESULT result_{};
};

std::string lower_ascii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char value) {
        return static_cast<char>(std::tolower(value));
    });
    return value;
}

std::string utf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<std::size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                        result.data(), size, nullptr, nullptr);
    return result;
}

std::filesystem::path module_directory() {
    std::wstring value(32768, L'\0');
    const DWORD count = GetModuleFileNameW(nullptr, value.data(),
                                           static_cast<DWORD>(value.size()));
    value.resize(count);
    return std::filesystem::path(value).parent_path();
}

std::string timestamp() {
    SYSTEMTIME time{};
    GetLocalTime(&time);
    std::ostringstream text;
    text << '[' << std::setfill('0') << std::setw(4) << time.wYear << '-'
         << std::setw(2) << time.wMonth << '-' << std::setw(2) << time.wDay << ' '
         << std::setw(2) << time.wHour << ':' << std::setw(2) << time.wMinute << ':'
         << std::setw(2) << time.wSecond << '.' << std::setw(3) << time.wMilliseconds
         << "] ";
    return text.str();
}

class Logger {
public:
    explicit Logger(std::filesystem::path path) : path_(std::move(path)) {}
    void operator()(const std::string& message) {
        std::scoped_lock lock(gate_);
        std::ofstream stream(path_, std::ios::app | std::ios::binary);
        if (stream) stream << timestamp() << message << "\r\n";
    }

private:
    std::filesystem::path path_;
    std::mutex gate_;
};

template <typename T>
T config_value(const json& object, const char* name, T fallback) {
    const auto wanted = lower_ascii(name);
    for (auto item = object.begin(); item != object.end(); ++item) {
        if (lower_ascii(item.key()) != wanted) continue;
        try { return item.value().get<T>(); } catch (...) { return fallback; }
    }
    return fallback;
}

BridgeConfig load_config(const std::filesystem::path& path) {
    BridgeConfig config;
    std::ifstream stream(path, std::ios::binary);
    if (!stream) return config;
    try {
        json source;
        stream >> source;
        config.port = config_value(source, "Port", config.port);
        config.adaptive_profile = config_value(source, "AdaptiveProfile", config.adaptive_profile);
        config.trigger_strength = config_value(source, "TriggerStrength", config.trigger_strength);
        config.haptics_strength = config_value(source, "HapticsStrength", config.haptics_strength);
        config.lightbar_strength = config_value(source, "LightbarStrength", config.lightbar_strength);
        config.enable_adaptive_triggers = config_value(source, "EnableAdaptiveTriggers",
                                                        config.enable_adaptive_triggers);
        config.enable_advanced_haptics = config_value(source, "EnableAdvancedHaptics",
                                                       config.enable_advanced_haptics);
        config.enable_lightbar = config_value(source, "EnableLightbar", config.enable_lightbar);
        config.enable_calibration_log = config_value(source, "EnableCalibrationLog",
                                                      config.enable_calibration_log);
        config.audio_device_contains = config_value(source, "AudioDeviceContains",
                                                     config.audio_device_contains);
        config.ensure_haptics_endpoint_audible = config_value(
            source, "EnsureHapticsEndpointAudible", config.ensure_haptics_endpoint_audible);
        config.haptics_endpoint_volume = config_value(source, "HapticsEndpointVolume",
                                                       config.haptics_endpoint_volume);
    } catch (...) {
        // Keep defaults if an edited configuration is temporarily malformed.
    }
    config.port = std::clamp(config.port, 1, 65535);
    return config;
}

struct Arguments {
    bool probe{};
    bool self_test{};
    bool full_self_test{};
    DWORD parent{};
};

Arguments parse_arguments() {
    Arguments result;
    int count{};
    LPWSTR* values = CommandLineToArgvW(GetCommandLineW(), &count);
    if (values == nullptr) return result;
    for (int index = 1; index < count; ++index) {
        const auto value = lower_ascii(utf8(values[index]));
        if (value == "--probe") result.probe = true;
        else if (value == "--self-test") result.self_test = true;
        else if (value == "--self-test-all") result.full_self_test = true;
        else if (value == "--parent" && index + 1 < count)
            result.parent = static_cast<DWORD>(_wtoi(values[++index]));
    }
    LocalFree(values);
    return result;
}

void write_ready(const std::filesystem::path& path,
                 bool controller_ready, bool haptics_ready,
                 const std::string& description) {
    const auto temporary = std::filesystem::path(path.wstring() + L".tmp");
    json value{
        {"pid", GetCurrentProcessId()},
        {"controllerReady", controller_ready},
        {"advancedHapticsReady", haptics_ready},
        {"outputBackend", "SteamInput006"},
        {"description", description},
        {"native", true}
    };
    {
        std::ofstream stream(temporary, std::ios::binary | std::ios::trunc);
        if (!stream) return;
        stream << value.dump();
    }
    MoveFileExW(temporary.c_str(), path.c_str(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
}

struct SharedState {
    std::mutex gate;
    GameState game;
    AdaptiveTriggerRuntime triggers;
    std::atomic<bool> shutdown{};
    std::atomic<std::uint64_t> telemetry_packets{};
    std::atomic<std::uint64_t> motor_packets{};
    std::atomic<std::uint64_t> weapon_hit_events{};
};

template <typename T>
T message_value(const json& message, const char* name, T fallback) {
    const auto item = message.find(name);
    if (item == message.end()) return fallback;
    try { return item->get<T>(); } catch (...) { return fallback; }
}

void receive_loop(SOCKET socket, SharedState& shared, HapticEngine& haptics,
                  const BridgeConfig& config, bool allow_shutdown, Logger& log) {
    std::array<char, 65536> buffer{};
    std::string last_character;
    while (!shared.shutdown.load(std::memory_order_acquire)) {
        const int size = recv(socket, buffer.data(), static_cast<int>(buffer.size()), 0);
        if (size == SOCKET_ERROR) {
            const int error = WSAGetLastError();
            if (error == WSAETIMEDOUT || error == WSAEWOULDBLOCK) continue;
            if (!shared.shutdown.load()) log("UDP telemetry receive failed: " +
                                             std::to_string(error));
            break;
        }
        try {
            const auto message = json::parse(buffer.data(), buffer.data() + size);
            if (message_value(message, "v", 0) != 1) continue;
            shared.telemetry_packets.fetch_add(1, std::memory_order_relaxed);
            const auto type = lower_ascii(message_value(message, "type", std::string{}));
            if (type == "state") {
                GameState next;
                next.character = lower_ascii(message_value(message, "character",
                                                             std::string{"unknown"}));
                next.in_gameplay = message_value(message, "inGameplay", false);
                next.health = message_value(message, "hp", 0.0F);
                next.max_health = message_value(message, "maxHp", 0.0F);
                next.motion_bank = message_value(message, "motionBank", 0U);
                next.motion_id = message_value(message, "motionId", 0U);
                next.motion_frame = message_value(message, "motionFrame", 0.0F);
                next.exceed_gauge = message_value(message, "exceedGauge", 0.0F);
                next.exceed_gauge_max = message_value(message, "exceedGaugeMax", 0.0F);
                next.exceed_stock = message_value(message, "exceedStock", 0);
                next.exceed_request = message_value(message, "exceedRequest", false);
                next.exceed_request_value = message_value(message, "exceedRequestValue", 0.0F);
                next.blue_rose_charge_level = message_value(message, "blueRoseChargeLevel", 0);
                next.blue_rose_timer = message_value(message, "blueRoseTimer", 0.0F);
                next.dante_weapon_id = message_value(message, "danteWeaponId", -1);
                next.attack_large_button = message_value(message, "attackLargeButton", -1);
                next.special2_button = message_value(message, "special2Button", -1);
                next.trigger_left = message_value(message, "left", 0.0F);
                next.trigger_right = message_value(message, "right", 0.0F);
                next.last_seen = Clock::now();

                const auto before = shared.triggers.exceed_mapping() + '/' +
                    shared.triggers.nero_attack_large_mapping() + '/' +
                    shared.triggers.dante_attack_large_mapping();
                shared.triggers.update_bindings(next.character, next.attack_large_button,
                                                next.special2_button);
                const auto after = shared.triggers.exceed_mapping() + '/' +
                    shared.triggers.nero_attack_large_mapping() + '/' +
                    shared.triggers.dante_attack_large_mapping();
                if (before != after) {
                    log("Adaptive mapping read from DMC5 controls: Exceed=" +
                        shared.triggers.exceed_mapping() + ", NeroAttackL=" +
                        shared.triggers.nero_attack_large_mapping() + ", DanteAttackL=" +
                        shared.triggers.dante_attack_large_mapping() + '.');
                }
                {
                    std::scoped_lock lock(shared.gate);
                    shared.game = next;
                }
                if (next.character != last_character) {
                    last_character = next.character;
                    log("Character detected: " + next.character + ".");
                }
            } else if (type == "rumble") {
                haptics.rumble_pulse(message_value(message, "left", 0.0F),
                                     message_value(message, "right", 0.0F),
                                     message_value(message, "duration", 0.1F));
            } else if (type == "padshake") {
                haptics.from_game_pad_shake(message_value(message, "motor", 0),
                    std::clamp(message_value(message, "value", 0.0F), 0.0F, 1.0F),
                    message_value(message, "duration", 0.1F));
            } else if (type == "motor") {
                shared.motor_packets.fetch_add(1, std::memory_order_relaxed);
                haptics.set_game_motor(message_value(message, "motor", 0),
                                       message_value(message, "value", 0.0F));
            } else if (type == "event") {
                const auto name = lower_ascii(message_value(message, "name", std::string{}));
                if (name == "weapon_hit")
                    shared.weapon_hit_events.fetch_add(1, std::memory_order_relaxed);
                if (name == "damage" && lower_ascii(config.adaptive_profile) == "enhanced") {
                    haptics.impact(std::clamp(message_value(message, "value", .15F), .15F, 1.0F));
                } else if (name == "weapon_hit" &&
                           lower_ascii(config.adaptive_profile) == "enhanced") {
                    GameState latest;
                    {
                        std::scoped_lock lock(shared.gate);
                        latest = shared.game;
                    }
                    haptics.weapon_hit(latest.character, std::clamp(
                        message_value(message, "value", 1.0F), .2F, 1.0F));
                } else if (name != "exceed_input" && name != "ex_act" &&
                           name != "max_act" && name.rfind("gun_charge_", 0) != 0) {
                    haptics.play_original(name);
                }
            } else if (type == "shutdown" && allow_shutdown) {
                log("Session bridge received shutdown request.");
                shared.shutdown.store(true, std::memory_order_release);
            }
        } catch (const std::exception& error) {
            log(std::string("Ignored invalid telemetry packet: ") + error.what());
        }
    }
}

SOCKET open_udp(int port, Logger& log) {
    SOCKET socket = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket == INVALID_SOCKET) return INVALID_SOCKET;
    DWORD timeout = 250;
    setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO,
               reinterpret_cast<const char*>(&timeout), sizeof(timeout));
    sockaddr_in endpoint{};
    endpoint.sin_family = AF_INET;
    endpoint.sin_port = htons(static_cast<u_short>(port));
    endpoint.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(socket, reinterpret_cast<const sockaddr*>(&endpoint), sizeof(endpoint)) != 0) {
        log("Could not bind telemetry UDP port " + std::to_string(port) + ": " +
            std::to_string(WSAGetLastError()));
        closesocket(socket);
        return INVALID_SOCKET;
    }
    return socket;
}

void parent_monitor(DWORD parent, SharedState& shared, Logger& log) {
    if (parent == 0) return;
    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, parent);
    if (process == nullptr) {
        shared.shutdown.store(true, std::memory_order_release);
        return;
    }
    while (!shared.shutdown.load(std::memory_order_acquire)) {
        if (WaitForSingleObject(process, 250) == WAIT_OBJECT_0) {
            log("DMC5 exited; shutting down bridge.");
            shared.shutdown.store(true, std::memory_order_release);
            break;
        }
    }
    CloseHandle(process);
}

int run() {
    HANDLE instance = CreateMutexW(nullptr, TRUE, L"Local\\DMC5DualSense.Bridge");
    if (instance == nullptr) return 3;
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        CloseHandle(instance);
        return 0;
    }
    ComApartment com;

    const auto directory = module_directory();
    Logger log(directory / L"bridge.log");
    const auto config = load_config(directory / L"config.json");
    const auto arguments = parse_arguments();
    const auto ready_path = directory / L"bridge.ready.json";
    std::error_code ignored;
    std::filesystem::remove(ready_path, ignored);
    std::filesystem::remove(std::filesystem::path(ready_path.wstring() + L".tmp"), ignored);

    log("Native session bridge started for the current DMC5 process.");
    SharedState shared;
    SteamInputOutputDevice controller(directory);
    const bool controller_ready = controller.ensure_connected();
    log(controller_ready
        ? "DualSense output connected through Steam Input: " + controller.description()
        : "DualSense Steam Input output is waiting: " + controller.description());

    HapticEngine haptics(config.haptics_strength);
    bool haptics_ready = !config.enable_advanced_haptics;
    if (config.enable_advanced_haptics) {
        haptics_ready = haptics.start(config.audio_device_contains,
            config.ensure_haptics_endpoint_audible, config.haptics_endpoint_volume,
            directory / L"Haptics");
        log(haptics_ready ? "Advanced haptics audio: " + haptics.status()
                          : "Advanced haptics unavailable: " + haptics.status());
    }

    if (arguments.probe) {
        controller.reset();
        ReleaseMutex(instance);
        CloseHandle(instance);
        return controller_ready ? 0 : 2;
    }
    if (arguments.self_test) {
        ControllerOutput first;
        first.left_trigger = TriggerEffect::vibration(0, 4, 76);
        first.blue = 220;
        controller.write(first);
        Sleep(700);
        ControllerOutput second;
        second.left_trigger = TriggerEffect::weapon(4, 8, 4);
        second.right_trigger = TriggerEffect::weapon(4, 8, 4);
        second.blue = 220;
        controller.write(second);
        Sleep(1200);
        controller.reset();
        log("Native trigger/light self-test completed.");
        ReleaseMutex(instance);
        CloseHandle(instance);
        return controller_ready ? 0 : 2;
    }
    if (arguments.full_self_test) {
        if (!controller_ready || !haptics_ready || haptics.original_sample_count() != 12) {
            log("Full native self-test cannot start: controller=" +
                std::to_string(controller_ready) + ", haptics=" +
                std::to_string(haptics_ready) + ", samples=" +
                std::to_string(haptics.original_sample_count()) + ".");
            controller.reset();
            ReleaseMutex(instance);
            CloseHandle(instance);
            return 2;
        }

        struct TriggerTest {
            const char* name;
            TriggerEffect left;
            TriggerEffect right;
            std::uint8_t red;
            std::uint8_t green;
            std::uint8_t blue;
        };
        const TriggerTest trigger_tests[]{
            {"Nero Exceed", TriggerEffect::vibration(0, 4, 76), TriggerEffect::off(), 0, 0, 220},
            {"Nero Blue Rose", TriggerEffect::off(), TriggerEffect::weapon(4, 8, 4), 0, 0, 220},
            {"Dante weapon 0", TriggerEffect::off(), TriggerEffect::weapon(4, 5, 4), 195, 0, 0},
            {"Dante weapon 1", TriggerEffect::off(), TriggerEffect::weapon(4, 8, 4), 195, 0, 0},
            {"Dante weapons 2/3/4", TriggerEffect::off(), TriggerEffect::weapon(2, 8, 5), 195, 0, 0},
            {"Dante weapon 5", TriggerEffect::off(), TriggerEffect::vibration(0, 4, 76), 195, 0, 0},
            {"V (triggers off)", TriggerEffect::off(), TriggerEffect::off(), 120, 0, 255},
            {"Vergil (triggers off)", TriggerEffect::off(), TriggerEffect::off(), 0, 255, 160}
        };
        log("Full native self-test: 8 adaptive-trigger/light profiles and all 12 haptic samples.");
        for (const auto& test : trigger_tests) {
            ControllerOutput output;
            output.left_trigger = test.left;
            output.right_trigger = test.right;
            output.red = test.red;
            output.green = test.green;
            output.blue = test.blue;
            if (!controller.write(output)) {
                log(std::string("Full native self-test Steam Input write failed at ") +
                    test.name + '.');
                controller.reset();
                ReleaseMutex(instance);
                CloseHandle(instance);
                return 2;
            }
            log(std::string("Trigger/light test: ") + test.name + '.');
            Sleep(550);
        }

        const char* haptic_tests[]{
            "coyote_shot_shell", "bluerose_shot_shell", "jr_jigenzan_shot_shell",
            "evony_shot_shell", "ivory_shot_shell", "jigenzan_shot_shell",
            "beo_sp_impact", "mirage_sp_loop", "mirage_sp_end", "beo_sp_pre",
            "yamato_zetsu_return", "yamato_zetsu_noutou"
        };
        ControllerOutput neutral;
        neutral.red = neutral.green = neutral.blue = 200;
        controller.write(neutral);
        for (const auto* event : haptic_tests) {
            haptics.stop_original();
            if (!haptics.play_original(event)) {
                log(std::string("Full native self-test could not schedule haptic ") + event + '.');
                controller.reset();
                ReleaseMutex(instance);
                CloseHandle(instance);
                return 2;
            }
            log(std::string("Haptic test: ") + event + '.');
            Sleep(std::string_view(event) == "mirage_sp_loop" ? 900 : 650);
        }
        haptics.stop_original();
        controller.reset();
        const auto audio = haptics.take_render_diagnostic();
        log("Full native self-test completed: nonzero audio frames=" +
            std::to_string(audio.non_zero_frames) + "/" + std::to_string(audio.frames) +
            ", peak=" + std::to_string(audio.peak) + ".");
        ReleaseMutex(instance);
        CloseHandle(instance);
        return audio.non_zero_frames > 0 && audio.peak > 0 ? 0 : 2;
    }
    WSADATA winsock{};
    if (WSAStartup(MAKEWORD(2, 2), &winsock) != 0) return 3;
    SOCKET udp = open_udp(config.port, log);
    if (udp == INVALID_SOCKET) {
        WSACleanup();
        return 3;
    }
    log("Listening for DMC5 telemetry on 127.0.0.1:" + std::to_string(config.port) + '.');

    std::thread parent;
    if (arguments.parent != 0)
        parent = std::thread(parent_monitor, arguments.parent, std::ref(shared), std::ref(log));
    std::thread receiver(receive_loop, udp, std::ref(shared), std::ref(haptics),
                         std::cref(config), true, std::ref(log));

    const auto started = Clock::now();
    auto next_ready = Clock::time_point{};
    auto next_audio_retry = Clock::time_point{};
    auto next_diagnostic = Clock::now() + std::chrono::seconds(5);
    while (!shared.shutdown.load(std::memory_order_acquire)) {
        GameState state;
        {
            std::scoped_lock lock(shared.gate);
            state = shared.game;
        }
        const auto now = Clock::now();
        const auto output_state = state.is_fresh(now) ? state : GameState{};
        const auto xinput = output_state.is_fresh(now)
            ? XInputSnapshot{true, output_state.trigger_left, output_state.trigger_right}
            : read_first_xinput();
        const auto effects = shared.triggers.build(output_state, config, xinput, now);
        auto output = build_profile(output_state, config,
            std::chrono::duration<double>(now - started).count(), effects.first,
            effects.second, now);
        const auto rumble = haptics.rumble_output();
        output.left_rumble = rumble.low;
        output.right_rumble = rumble.high;
        const bool connected = controller.write(output);

        if (config.enable_advanced_haptics && !haptics.started() &&
            now >= next_audio_retry) {
            next_audio_retry = now + std::chrono::seconds(1);
            haptics_ready = haptics.start(config.audio_device_contains,
                config.ensure_haptics_endpoint_audible, config.haptics_endpoint_volume,
                directory / L"Haptics");
        } else {
            haptics_ready = !config.enable_advanced_haptics || haptics.started();
        }

        if (now >= next_ready) {
            next_ready = now + std::chrono::seconds(2);
            write_ready(ready_path, connected, haptics_ready, controller.description());
        }
        if (config.enable_calibration_log && now >= next_diagnostic) {
            next_diagnostic = now + std::chrono::seconds(5);
            const auto steam = controller.take_diagnostic();
            const auto packet_count = shared.telemetry_packets.exchange(0);
            const auto motor_count = shared.motor_packets.exchange(0);
            const auto weapon_hit_count = shared.weapon_hit_events.exchange(0);
            const auto audio = haptics.take_render_diagnostic();
            log("Native output 5s: SteamInput=" + std::to_string(steam.successes) + '/' +
                std::to_string(steam.attempts) + ", triggerWrites=" +
                std::to_string(steam.trigger_effect_writes) + ", rumbleWrites=" +
                std::to_string(steam.rumble_writes) + ", telemetry=" +
                std::to_string(packet_count) + ", motorPackets=" +
                std::to_string(motor_count) + ", weaponHits=" +
                std::to_string(weapon_hit_count) + ", audio=" +
                std::to_string(audio.non_zero_frames) + '/' +
                std::to_string(audio.frames) + ", limited=" +
                std::to_string(audio.limited_frames) + ", peak=" +
                std::to_string(audio.peak) + '.');
        }
        Sleep(33);
    }

    controller.reset();
    closesocket(udp);
    if (receiver.joinable()) receiver.join();
    if (parent.joinable()) parent.join();
    std::filesystem::remove(ready_path, ignored);
    std::filesystem::remove(std::filesystem::path(ready_path.wstring() + L".tmp"), ignored);
    WSACleanup();
    ReleaseMutex(instance);
    CloseHandle(instance);
    return 0;
}

} // namespace
} // namespace dmc5ds

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    return dmc5ds::run();
}
