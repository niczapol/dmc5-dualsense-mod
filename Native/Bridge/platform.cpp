#include "platform.hpp"

#include <Windows.h>
#include <Xinput.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <mutex>
#include <sstream>

namespace dmc5ds {
namespace {

constexpr char kSteamClientVersion[] = "SteamClient020";
constexpr char kSteamInputVersion[] = "SteamInput006";
constexpr int kSteamClientGetInputIndex = 38;
constexpr int kSteamInputInitIndex = 0;
constexpr int kSteamInputShutdownIndex = 1;
constexpr int kSteamInputRunFrameIndex = 3;
constexpr int kSteamInputGetConnectedControllersIndex = 6;
constexpr int kSteamInputTriggerVibrationIndex = 30;
constexpr int kSteamInputSetLedColorIndex = 33;
constexpr int kSteamInputGetInputTypeForHandleIndex = 37;
constexpr int kSteamInputSetDualSenseTriggerEffectIndex = 47;
constexpr int kPs5ControllerType = 13;
constexpr std::uint32_t kLedFlagRestoreUserDefault = 1;

using SteamApiInit = bool (*)();
using SteamApiShutdown = void (*)();
using SteamApiGetHandle = int (*)();
using SteamInternalCreateInterface = void* (*)(const char*);
using SteamClientGetInput = void* (*)(void*, int, int, const char*);
using SteamInputInit = bool (*)(void*, bool);
using SteamInputShutdown = bool (*)(void*);
using SteamInputRunFrame = void (*)(void*, bool);
using SteamInputGetConnectedControllers = int (*)(void*, std::uint64_t*);
using SteamInputTriggerVibration = void (*)(void*, std::uint64_t, std::uint16_t,
                                             std::uint16_t);
using SteamInputSetLedColor = void (*)(void*, std::uint64_t, std::uint8_t,
                                       std::uint8_t, std::uint8_t, std::uint32_t);
using SteamInputGetInputTypeForHandle = int (*)(void*, std::uint64_t);
using SteamInputSetDualSenseTriggerEffect = void (*)(void*, std::uint64_t, void*);

template <typename T>
T load_export(HMODULE module, const char* name) {
    return reinterpret_cast<T>(GetProcAddress(module, name));
}

template <typename T>
T load_method(void* instance, int index) {
    if (instance == nullptr) return nullptr;
    const auto table = *reinterpret_cast<void***>(instance);
    return table == nullptr ? nullptr : reinterpret_cast<T>(table[index]);
}

std::string windows_error(DWORD code = GetLastError()) {
    return "Windows error " + std::to_string(code);
}

std::uint16_t scale_rumble(std::uint8_t value) {
    return static_cast<std::uint16_t>(value) * 257U;
}

} // namespace

struct SteamInputOutputDevice::Impl {
    mutable std::recursive_mutex gate;
    std::filesystem::path steam_api_path;
    HMODULE module{};
    void* steam_input{};
    std::uint64_t controller_handle{};
    bool steam_api_initialized{};
    bool steam_input_initialized{};
    std::chrono::steady_clock::time_point next_retry{};
    std::string last_error{"not initialized"};
    ControllerWriteDiagnostic diagnostic{};

    SteamApiShutdown steam_api_shutdown{};
    SteamInputShutdown input_shutdown{};
    SteamInputRunFrame run_frame{};
    SteamInputGetConnectedControllers get_connected_controllers{};
    SteamInputTriggerVibration trigger_vibration{};
    SteamInputSetLedColor set_led_color{};
    SteamInputGetInputTypeForHandle get_input_type{};
    SteamInputSetDualSenseTriggerEffect set_trigger_effect{};

    explicit Impl(const std::filesystem::path& base_directory)
        : steam_api_path(std::filesystem::absolute(base_directory / ".." /
                                                   "steam_api64.dll")) {}

    void shutdown() {
        controller_handle = 0;
        if (steam_input_initialized && steam_input != nullptr && input_shutdown != nullptr)
            input_shutdown(steam_input);
        steam_input_initialized = false;
        steam_input = nullptr;
        if (steam_api_initialized && steam_api_shutdown != nullptr) steam_api_shutdown();
        steam_api_initialized = false;
        if (module != nullptr) FreeLibrary(module);
        module = nullptr;
    }

    bool initialize_steam() {
        if (!std::filesystem::is_regular_file(steam_api_path)) {
            last_error = "steam_api64.dll not found beside DevilMayCry5.exe";
            return false;
        }
        if (GetEnvironmentVariableW(L"SteamAppId", nullptr, 0) == 0)
            SetEnvironmentVariableW(L"SteamAppId", L"601150");
        if (GetEnvironmentVariableW(L"SteamGameId", nullptr, 0) == 0)
            SetEnvironmentVariableW(L"SteamGameId", L"601150");

        module = LoadLibraryW(steam_api_path.c_str());
        if (module == nullptr) {
            last_error = "Steam Input initialization failed: " + windows_error();
            return false;
        }
        const auto init = load_export<SteamApiInit>(module, "SteamAPI_Init");
        steam_api_shutdown = load_export<SteamApiShutdown>(module, "SteamAPI_Shutdown");
        const auto get_user = load_export<SteamApiGetHandle>(module, "SteamAPI_GetHSteamUser");
        const auto get_pipe = load_export<SteamApiGetHandle>(module, "SteamAPI_GetHSteamPipe");
        const auto create_interface = load_export<SteamInternalCreateInterface>(
            module, "SteamInternal_CreateInterface");
        if (init == nullptr || steam_api_shutdown == nullptr || get_user == nullptr ||
            get_pipe == nullptr || create_interface == nullptr || !init()) {
            last_error = "Steam Input initialization failed: required Steam API is unavailable";
            shutdown();
            return false;
        }
        steam_api_initialized = true;
        void* steam_client = create_interface(kSteamClientVersion);
        const auto get_input = load_method<SteamClientGetInput>(
            steam_client, kSteamClientGetInputIndex);
        steam_input = get_input == nullptr ? nullptr :
            get_input(steam_client, get_user(), get_pipe(), kSteamInputVersion);
        if (steam_input == nullptr) {
            last_error = "SteamInput006 is unavailable";
            shutdown();
            return false;
        }
        const auto input_init = load_method<SteamInputInit>(steam_input, kSteamInputInitIndex);
        input_shutdown = load_method<SteamInputShutdown>(steam_input, kSteamInputShutdownIndex);
        run_frame = load_method<SteamInputRunFrame>(steam_input, kSteamInputRunFrameIndex);
        get_connected_controllers = load_method<SteamInputGetConnectedControllers>(
            steam_input, kSteamInputGetConnectedControllersIndex);
        trigger_vibration = load_method<SteamInputTriggerVibration>(
            steam_input, kSteamInputTriggerVibrationIndex);
        set_led_color = load_method<SteamInputSetLedColor>(
            steam_input, kSteamInputSetLedColorIndex);
        get_input_type = load_method<SteamInputGetInputTypeForHandle>(
            steam_input, kSteamInputGetInputTypeForHandleIndex);
        set_trigger_effect = load_method<SteamInputSetDualSenseTriggerEffect>(
            steam_input, kSteamInputSetDualSenseTriggerEffectIndex);
        if (input_init == nullptr || input_shutdown == nullptr || run_frame == nullptr ||
            get_connected_controllers == nullptr || trigger_vibration == nullptr ||
            set_led_color == nullptr || get_input_type == nullptr ||
            set_trigger_effect == nullptr || !input_init(steam_input, true)) {
            last_error = "Steam Input initialization failed: SteamInput006 method unavailable";
            shutdown();
            return false;
        }
        steam_input_initialized = true;
        return true;
    }

    bool connect() {
        const auto now = std::chrono::steady_clock::now();
        if (controller_handle == 0 && now < next_retry) return false;
        next_retry = now + std::chrono::seconds(1);
        if (!steam_api_initialized && !initialize_steam()) return false;
        run_frame(steam_input, true);
        std::array<std::uint64_t, 16> handles{};
        const int count = std::clamp(get_connected_controllers(steam_input, handles.data()),
                                     0, static_cast<int>(handles.size()));
        // Revalidate cached handles; output API calls do not acknowledge hardware.
        const auto previous = controller_handle;
        controller_handle = 0;
        for (int index = 0; index < count; ++index) {
            if (handles[index] == previous && previous != 0) {
                controller_handle = previous;
                return true;
            }
        }
        for (int index = 0; index < count; ++index) {
            if (handles[index] != 0 && get_input_type(steam_input, handles[index]) ==
                                       kPs5ControllerType) {
                controller_handle = handles[index];
                last_error.clear();
                return true;
            }
        }
        last_error = count == 0 ? "Steam Input has no connected controller" :
            "Steam Input found " + std::to_string(count) +
            " controller(s), but no PS5 DualSense";
        return false;
    }
};

SteamInputOutputDevice::SteamInputOutputDevice(
    const std::filesystem::path& base_directory)
    : impl_(std::make_unique<Impl>(base_directory)) {}

SteamInputOutputDevice::~SteamInputOutputDevice() {
    reset();
    std::scoped_lock lock(impl_->gate);
    impl_->shutdown();
}

bool SteamInputOutputDevice::ensure_connected() {
    std::scoped_lock lock(impl_->gate);
    return impl_->connect();
}

bool SteamInputOutputDevice::write(const ControllerOutput& output) {
    std::scoped_lock lock(impl_->gate);
    ++impl_->diagnostic.attempts;
    if (!impl_->connect()) return false;
    impl_->run_frame(impl_->steam_input, true);
    auto payload = build_steam_trigger_payload(output.left_trigger, output.right_trigger);
    impl_->set_trigger_effect(impl_->steam_input, impl_->controller_handle, payload.data());
    impl_->set_led_color(impl_->steam_input, impl_->controller_handle,
                         output.red, output.green, output.blue, 0);
    impl_->trigger_vibration(impl_->steam_input, impl_->controller_handle,
                             scale_rumble(output.left_rumble),
                             scale_rumble(output.right_rumble));
    ++impl_->diagnostic.successes;
    if (output.left_trigger.mode != TriggerMode::off ||
        output.right_trigger.mode != TriggerMode::off)
        ++impl_->diagnostic.trigger_effect_writes;
    if (output.left_rumble != 0 || output.right_rumble != 0)
        ++impl_->diagnostic.rumble_writes;
    return true;
}

void SteamInputOutputDevice::reset() {
    std::scoped_lock lock(impl_->gate);
    if (impl_->steam_input == nullptr || impl_->controller_handle == 0) return;
    auto payload = build_steam_trigger_payload(TriggerEffect::off(), TriggerEffect::off());
    impl_->set_trigger_effect(impl_->steam_input, impl_->controller_handle, payload.data());
    impl_->trigger_vibration(impl_->steam_input, impl_->controller_handle, 0, 0);
    impl_->set_led_color(impl_->steam_input, impl_->controller_handle, 0, 0, 0,
                         kLedFlagRestoreUserDefault);
}

bool SteamInputOutputDevice::connected() const {
    std::scoped_lock lock(impl_->gate);
    return impl_->controller_handle != 0;
}

std::string SteamInputOutputDevice::description() const {
    std::scoped_lock lock(impl_->gate);
    if (impl_->controller_handle == 0) return impl_->last_error;
    std::ostringstream text;
    text << "Steam Input PS5 controller 0x" << std::hex << std::uppercase
         << impl_->controller_handle;
    return text.str();
}

ControllerWriteDiagnostic SteamInputOutputDevice::take_diagnostic() {
    std::scoped_lock lock(impl_->gate);
    const auto value = impl_->diagnostic;
    impl_->diagnostic = {};
    return value;
}

XInputSnapshot read_first_xinput() {
    for (DWORD index = 0; index < 4; ++index) {
        XINPUT_STATE state{};
        if (XInputGetState(index, &state) == ERROR_SUCCESS) {
            return {true, state.Gamepad.bLeftTrigger / 255.0F,
                    state.Gamepad.bRightTrigger / 255.0F};
        }
    }
    return {};
}

} // namespace dmc5ds
