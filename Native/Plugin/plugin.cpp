#include <Windows.h>
#include <shellapi.h>
#include <winsock2.h>
#include <ws2tcpip.h>

#include <reframework/API.hpp>

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace {

using API = reframework::API;
using Object = API::ManagedObject;
using Clock = std::chrono::steady_clock;

constexpr int kPort = 27105;
constexpr int kGameActionAttackLarge = 1;
constexpr int kGameActionSpecial2 = 14;

const REFrameworkPluginInitializeParam* g_param{};
SOCKET g_udp{INVALID_SOCKET};
sockaddr_in g_endpoint{};
std::mutex g_send_gate;
std::filesystem::path g_base_directory;
bool g_calibration_log{false};
Clock::time_point g_last_state{};
Clock::time_point g_next_missing_player{};
Clock::time_point g_last_error{};
Clock::time_point g_last_act{};
Clock::time_point g_last_weapon_hit{};
Clock::time_point g_last_dante_shot{};
Clock::time_point g_last_blue_rose_shot{};
float g_last_hp{-1};
std::uint32_t g_last_motion_bank{UINT32_MAX};
std::uint32_t g_last_motion_id{UINT32_MAX};
std::string g_last_character{"unknown"};
int g_last_exceed_stock{-1};
bool g_blue_rose_charging{};
std::uintptr_t g_active_player{};
std::string g_active_character{"unknown"};
std::mutex g_active_gate;
std::array<float, 132> g_last_motor_power{};
std::array<Clock::time_point, 132> g_last_motor_time{};
int g_last_attack_large{INT32_MIN};
int g_last_special2{INT32_MIN};
bool g_pad_lookup_logged{};

thread_local Object* g_pending_dante_player{};
thread_local int g_pending_dante_weapon{-1};
thread_local bool g_pending_dante_ebony{};
thread_local bool g_dante_shell_pending{};

struct HookRecord {
    API::Method* method{};
    unsigned id{};
};
std::vector<HookRecord> g_hooks;

std::string lower_ascii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return value;
}

std::string format_float(float value) {
    if (!std::isfinite(value)) return "0";
    std::ostringstream text;
    text << std::fixed << std::setprecision(6) << value;
    auto result = text.str();
    while (result.size() > 1 && result.back() == '0') result.pop_back();
    if (!result.empty() && result.back() == '.') result.pop_back();
    return result;
}

std::string escape_json(std::string_view value) {
    std::string result;
    result.reserve(value.size() + 8);
    for (char character : value) {
        if (character == '\\' || character == '"') result.push_back('\\');
        result.push_back(character);
    }
    return result;
}

std::string utc_stamp() {
    SYSTEMTIME time{};
    GetSystemTime(&time);
    std::ostringstream text;
    text << std::setfill('0') << std::setw(4) << time.wYear << '-'
         << std::setw(2) << time.wMonth << '-' << std::setw(2) << time.wDay << 'T'
         << std::setw(2) << time.wHour << ':' << std::setw(2) << time.wMinute << ':'
         << std::setw(2) << time.wSecond << '.' << std::setw(3) << time.wMilliseconds << 'Z';
    return text.str();
}

void runtime_log(const std::string& line) {
    if (g_base_directory.empty()) return;
    std::ofstream stream(g_base_directory / L"plugin.log", std::ios::app | std::ios::binary);
    if (stream) stream << utc_stamp() << ' ' << line << "\r\n";
}

void log_info(const std::string& line) {
    if (g_param != nullptr) g_param->functions->log_info("[DMC5DualSense] %s", line.c_str());
    runtime_log("INFO " + line);
}

void log_error(const std::string& line) {
    if (g_param != nullptr) g_param->functions->log_error("[DMC5DualSense] %s", line.c_str());
    runtime_log("ERROR " + line);
}

void log_throttled(const std::string& line) {
    const auto now = Clock::now();
    if (now - g_last_error < std::chrono::seconds(5)) return;
    g_last_error = now;
    log_error(line);
}

void send_json(const std::string& value) {
    std::scoped_lock lock(g_send_gate);
    if (g_udp == INVALID_SOCKET) return;
    sendto(g_udp, value.data(), static_cast<int>(value.size()), 0,
           reinterpret_cast<const sockaddr*>(&g_endpoint), sizeof(g_endpoint));
}

API::VMContext* vm() { return API::get()->get_vm_context(); }

template <typename T>
T call(Object* object, std::string_view method, T fallback = T{}) {
    if (object == nullptr) return fallback;
    const auto type = object->get_type_definition();
    const auto target = type == nullptr ? nullptr : type->find_method(method);
    if (target == nullptr) return fallback;
    return target->call<T>(vm(), object);
}

template <typename T, typename A>
T call_arg(Object* object, std::string_view method, A argument, T fallback = T{}) {
    if (object == nullptr) return fallback;
    const auto type = object->get_type_definition();
    const auto target = type == nullptr ? nullptr : type->find_method(method);
    if (target == nullptr) return fallback;
    return target->call<T>(vm(), object, argument);
}

Object* get_singleton(const char* name) {
    return API::get()->get_managed_singleton(name);
}

std::string detect_character(Object* player) {
    if (player == nullptr) return "unknown";
    const auto type = player->get_type_definition();
    const auto identity = lower_ascii(type == nullptr ? std::string{} : type->get_full_name());
    if (identity.find("pl0000") != std::string::npos ||
        identity.find("nero") != std::string::npos) return "nero";
    if (identity.find("pl0100") != std::string::npos ||
        identity.find("dante") != std::string::npos) return "dante";
    if (identity.find("pl0400") != std::string::npos ||
        identity.find("vergil") != std::string::npos) return "vergil";
    if (identity.find("pl0200") != std::string::npos ||
        identity.find("player_v") != std::string::npos ||
        identity.find("playerv") != std::string::npos) return "v";
    return "unknown";
}

void set_active(Object* player, const std::string& character) {
    std::scoped_lock lock(g_active_gate);
    g_active_player = reinterpret_cast<std::uintptr_t>(player);
    g_active_character = character;
}

void clear_active() {
    std::scoped_lock lock(g_active_gate);
    g_active_player = 0;
    g_active_character = "unknown";
}

bool is_active_character(std::string_view character) {
    std::scoped_lock lock(g_active_gate);
    return g_active_player != 0 && g_active_character == character;
}

bool is_active_player(int argc, void** argv, std::string_view character = {}) {
    if (argc <= 1 || argv[1] == nullptr) return false;
    std::scoped_lock lock(g_active_gate);
    return g_active_player == reinterpret_cast<std::uintptr_t>(argv[1]) &&
           (character.empty() || g_active_character == character);
}

void read_gamepad(float& left, float& right) {
    left = right = 0;
    auto method = API::get()->tdb()->find_method("via.hid.GamePad", "get_Device");
    if (method == nullptr) return;
    auto device = method->call<Object*>(vm());
    if (device == nullptr) return;
    left = std::clamp(call<float>(device, "get_AnalogL"), 0.0F, 1.0F);
    right = std::clamp(call<float>(device, "get_AnalogR"), 0.0F, 1.0F);
}

Object* resolve_pad_manager() {
    if (auto exact = get_singleton("app.PadManager")) return exact;
    for (const auto& singleton : API::get()->get_managed_singletons()) {
        const auto type = reinterpret_cast<API::TypeDefinition*>(singleton.t);
        if (type == nullptr) continue;
        const auto name = lower_ascii(type->get_full_name());
        if (name == "app.padmanager" || name.ends_with(".padmanager"))
            return reinterpret_cast<Object*>(singleton.instance);
    }
    return nullptr;
}

Object* resolve_key_assign(Object* manager) {
    if (manager == nullptr) return nullptr;
    if (auto direct = call<Object*>(manager, "get_KeyAssign")) return direct;
    auto pad_input = call<Object*>(manager, "get_padInput");
    return call<Object*>(pad_input, "get_keyAssign");
}

void read_bindings(int& attack_large, int& special2) {
    attack_large = special2 = -1;
    auto manager = resolve_pad_manager();
    auto assign = resolve_key_assign(manager);
    if (assign == nullptr) {
        if (!g_pad_lookup_logged) {
            g_pad_lookup_logged = true;
            log_info("Controller binding lookup unavailable; adaptive triggers remain off for unconfirmed buttons.");
        }
        return;
    }
    attack_large = call_arg<int>(assign, "FindButton", kGameActionAttackLarge, -1);
    special2 = call_arg<int>(assign, "FindButton", kGameActionSpecial2, -1);
    if (attack_large != g_last_attack_large || special2 != g_last_special2) {
        g_last_attack_large = attack_large;
        g_last_special2 = special2;
        std::ostringstream line;
        line << "Control bindings: AttackL=0x" << std::hex << std::uppercase << attack_large
             << ", Special2=0x" << special2 << '.';
        log_info(line.str());
    }
}

void read_motion(Object* player, std::uint32_t& bank, std::uint32_t& id, float& frame) {
    bank = id = 0;
    frame = 0;
    auto motion = call<Object*>(player, "get_cachedMotion");
    auto layer = call_arg<Object*>(motion, "getLayer", 0);
    if (layer == nullptr) return;
    bank = call<std::uint32_t>(layer, "get_MotionBankID");
    id = call<std::uint32_t>(layer, "get_MotionID");
    frame = call<float>(layer, "get_Frame");
}

void send_event(const std::string& name, float value = 0) {
    float left{}, right{};
    read_gamepad(left, right);
    send_json("{\"v\":1,\"type\":\"event\",\"name\":\"" + escape_json(name) +
        "\",\"value\":" + format_float(value) + ",\"left\":" + format_float(left) +
        ",\"right\":" + format_float(right) + '}');
}

void send_act(const std::string& name) {
    const auto now = Clock::now();
    if (now - g_last_act < std::chrono::milliseconds(70)) return;
    g_last_act = now;
    send_event(name);
}

void send_state(const std::string& character, bool gameplay, float hp, float max_hp,
                std::uint32_t motion_bank, std::uint32_t motion_id, float motion_frame,
                float exceed_gauge = 0, float exceed_max = 0, int exceed_stock = 0,
                bool exceed_request = false, float exceed_request_value = 0,
                int charge_level = 0, float blue_rose_timer = 0, int dante_weapon = -1,
                int attack_large = -1, int special2 = -1, float left = 0, float right = 0) {
    send_json("{\"v\":1,\"type\":\"state\",\"character\":\"" +
        escape_json(character) + "\",\"inGameplay\":" + (gameplay ? "true" : "false") +
        ",\"hp\":" + format_float(hp) + ",\"maxHp\":" + format_float(max_hp) +
        ",\"motionBank\":" + std::to_string(motion_bank) + ",\"motionId\":" +
        std::to_string(motion_id) + ",\"motionFrame\":" + format_float(motion_frame) +
        ",\"exceedGauge\":" + format_float(exceed_gauge) + ",\"exceedGaugeMax\":" +
        format_float(exceed_max) + ",\"exceedStock\":" + std::to_string(exceed_stock) +
        ",\"exceedRequest\":" + (exceed_request ? "true" : "false") +
        ",\"exceedRequestValue\":" + format_float(exceed_request_value) +
        ",\"blueRoseChargeLevel\":" + std::to_string(charge_level) +
        ",\"blueRoseTimer\":" + format_float(blue_rose_timer) +
        ",\"danteWeaponId\":" + std::to_string(dante_weapon) +
        ",\"attackLargeButton\":" + std::to_string(attack_large) +
        ",\"special2Button\":" + std::to_string(special2) +
        ",\"left\":" + format_float(left) + ",\"right\":" + format_float(right) + '}');
}

void publish_missing() {
    g_next_missing_player = Clock::now() + std::chrono::milliseconds(750);
    clear_active();
    send_state("unknown", false, 0, 0, 0, 0, 0);
    g_last_hp = -1;
    g_last_exceed_stock = -1;
    g_blue_rose_charging = false;
}

void write_calibration(const std::string& character, std::uint32_t bank,
                       std::uint32_t id, float frame, float hp, float max_hp) {
    if (!g_calibration_log) return;
    const auto path = g_base_directory / L"calibration.csv";
    const bool exists = std::filesystem::exists(path);
    std::ofstream stream(path, std::ios::app | std::ios::binary);
    if (!stream) return;
    if (!exists) stream << "utc,character,motion_bank,motion_id,frame,hp,max_hp\r\n";
    stream << utc_stamp() << ',' << character << ',' << bank << ',' << id << ','
           << format_float(frame) << ',' << format_float(hp) << ',' << format_float(max_hp)
           << "\r\n";
}

void on_update() {
    const auto now = Clock::now();
    if (now - g_last_state < std::chrono::milliseconds(50) || now < g_next_missing_player)
        return;
    g_last_state = now;
    try {
        auto manager = get_singleton("app.PlayerManager");
        auto player = call<Object*>(manager, "get_manualPlayer");
        if (player == nullptr) {
            publish_missing();
            return;
        }
        const auto character = detect_character(player);
        if (character == "unknown") {
            publish_missing();
            return;
        }
        g_next_missing_player = {};
        set_active(player, character);

        const float hp = call<float>(player, "get_hp");
        const float max_hp = call<float>(player, "get_maxHp");
        std::uint32_t motion_bank{}, motion_id{};
        float motion_frame{};
        read_motion(player, motion_bank, motion_id, motion_frame);
        float left{}, right{};
        read_gamepad(left, right);
        int attack_large{}, special2{};
        read_bindings(attack_large, special2);

        float exceed{}, exceed_max{}, request_value{}, blue_timer{};
        int stock{}, charge_level{}, dante_weapon{-1};
        bool request{};
        if (character == "nero") {
            exceed = call<float>(player, "get_exceedGauge");
            exceed_max = call<float>(player, "get_MaxExceedGauge");
            stock = call<int>(player, "get_exceedStock");
            request = call<bool>(player, "get_exceedReqTrigger");
            request_value = call<float>(player, "get_reqExceed");
            charge_level = call<int>(player, "get_reserveChargeLevel");
            blue_timer = call<float>(player, "get_blueRoseTimer");
            if (g_last_exceed_stock >= 0 && stock > g_last_exceed_stock &&
                now - g_last_weapon_hit < std::chrono::milliseconds(280)) {
                const int gained = stock - g_last_exceed_stock;
                send_act(gained >= 2 || (g_last_exceed_stock == 0 && stock >= 3)
                    ? "max_act" : "ex_act");
            }
            g_last_exceed_stock = stock;
        } else if (character == "dante") {
            dante_weapon = call<int>(player, "get_weaponL_ID", -1);
        }

        send_state(character, true, hp, max_hp, motion_bank, motion_id, motion_frame,
            exceed, exceed_max, stock, request, request_value, charge_level, blue_timer,
            dante_weapon, attack_large, special2, left, right);
        if (g_last_hp >= 0 && hp < g_last_hp && max_hp > 0)
            send_event("damage", std::clamp((g_last_hp - hp) / max_hp * 4.0F, .15F, 1.0F));
        if (motion_bank != g_last_motion_bank || motion_id != g_last_motion_id ||
            character != g_last_character)
            write_calibration(character, motion_bank, motion_id, motion_frame, hp, max_hp);
        g_last_hp = hp;
        g_last_motion_bank = motion_bank;
        g_last_motion_id = motion_id;
        g_last_character = character;
    } catch (const std::exception& error) {
        log_throttled(std::string("Telemetry error: ") + error.what());
    } catch (...) {
        log_throttled("Telemetry error: native exception.");
    }
}

int hook_motor(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (argc >= 4) {
        const int motor = static_cast<int>(reinterpret_cast<std::intptr_t>(argv[2]));
        const auto raw = static_cast<std::uint32_t>(reinterpret_cast<std::uintptr_t>(argv[3]));
        const float power = std::clamp(std::bit_cast<float>(raw), 0.0F, 1.0F);
        if (motor >= 0 && motor < static_cast<int>(g_last_motor_power.size())) {
            const auto now = Clock::now();
            if (std::abs(power - g_last_motor_power[motor]) >= .005F ||
                now - g_last_motor_time[motor] >= std::chrono::milliseconds(120)) {
                g_last_motor_power[motor] = power;
                g_last_motor_time[motor] = now;
                send_json("{\"v\":1,\"type\":\"motor\",\"motor\":" +
                    std::to_string(motor) + ",\"value\":" + format_float(power) + '}');
            }
        }
    }
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}

int hook_exceed_input(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_player(argc, argv, "nero") && argc > 2 &&
        (reinterpret_cast<std::uintptr_t>(argv[2]) & 1U) != 0) send_event("exceed_input");
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_max_act(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_player(argc, argv, "nero") && argc > 2 &&
        (reinterpret_cast<std::uintptr_t>(argv[2]) & 1U) != 0) send_act("max_act");
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_stock(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_character("nero") && argc >= 5) {
        const int amount = static_cast<int>(reinterpret_cast<std::intptr_t>(argv[2]));
        const bool full = (reinterpret_cast<std::uintptr_t>(argv[4]) & 1U) != 0;
        if (amount > 0 && full) send_act(amount >= 3 ? "max_act" : "ex_act");
    }
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_weapon_hit(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_player(argc, argv) && Clock::now() - g_last_weapon_hit >=
        std::chrono::milliseconds(32)) {
        g_last_weapon_hit = Clock::now();
        send_event("weapon_hit", 1.0F);
    }
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_charge_start(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_player(argc, argv, "nero") && !g_blue_rose_charging) {
        g_blue_rose_charging = true; send_event("gun_charge_start");
    }
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_charge_level(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_player(argc, argv, "nero")) {
        g_blue_rose_charging = true; send_event("gun_charge_level");
    }
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_charge_end(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_player(argc, argv, "nero") && g_blue_rose_charging) {
        g_blue_rose_charging = false; send_event("gun_charge_end");
    }
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_blue_rose_shot(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    const auto now = Clock::now();
    if (is_active_player(argc, argv, "nero") &&
        now - g_last_blue_rose_shot >= std::chrono::milliseconds(35)) {
        g_last_blue_rose_shot = now; g_blue_rose_charging = false;
        send_event("blue_rose_shot");
    }
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}

int hook_dante_shell_pre(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    g_pending_dante_player = nullptr;
    g_pending_dante_weapon = -1;
    g_pending_dante_ebony = g_dante_shell_pending = false;
    if (is_active_player(argc, argv, "dante")) {
        g_pending_dante_player = reinterpret_cast<Object*>(argv[1]);
        g_pending_dante_weapon = call<int>(g_pending_dante_player, "get_weaponL_ID", -1);
        g_pending_dante_ebony = call<bool>(g_pending_dante_player, "get_isEbonyShot");
        g_dante_shell_pending = true;
    }
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_dante_ebony(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (g_dante_shell_pending && is_active_player(argc, argv, "dante") && argc > 2)
        g_pending_dante_ebony = (reinterpret_cast<std::uintptr_t>(argv[2]) & 1U) != 0;
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
void hook_dante_shell_post(void**, REFrameworkTypeDefinitionHandle, unsigned long long) {
    const auto now = Clock::now();
    if (g_pending_dante_player != nullptr &&
        now - g_last_dante_shot >= std::chrono::milliseconds(20)) {
        if (g_pending_dante_weapon == 0) {
            g_last_dante_shot = now;
            send_event(g_pending_dante_ebony ? "dante_ebony_shot" : "dante_ivory_shot");
        } else if (g_pending_dante_weapon == 1) {
            g_last_dante_shot = now; send_event("dante_coyote_shot");
        }
    }
    g_pending_dante_player = nullptr;
    g_pending_dante_weapon = -1;
    g_pending_dante_ebony = g_dante_shell_pending = false;
}

int hook_judgement(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_player(argc, argv, "vergil")) {
        auto player = reinterpret_cast<Object*>(argv[1]);
        send_event(call<bool>(player, "get_isJudgeMentCutJR") ? "judgement_cut_jr"
                                                               : "judgement_cut");
    }
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_judgement_end(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_player(argc, argv, "vergil")) {
        send_event("yamato_return"); send_event("yamato_noutou");
    }
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_beowulf_pre(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_player(argc, argv, "vergil")) send_event("beowulf_pre");
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_beowulf_impact(int argc, void** argv, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_player(argc, argv, "vergil")) send_event("beowulf_impact");
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_mirage_loop(int, void**, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_character("vergil")) send_event("mirage_loop");
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}
int hook_mirage_end(int, void**, REFrameworkTypeDefinitionHandle*, unsigned long long) {
    if (is_active_character("vergil")) send_event("mirage_end");
    return REFRAMEWORK_HOOK_CALL_ORIGINAL;
}

void install_hook(const char* type, const char* signature, REFPreHookFn pre,
                  REFPostHookFn post, const char* label) {
    auto method = API::get()->tdb()->find_method(type, signature);
    if (method == nullptr) {
        log_error(std::string("Hook not found: ") + type + '.' + signature);
        return;
    }
    const auto id = method->add_hook(pre, post, false);
    g_hooks.push_back({method, id});
    log_info(std::string(label) + " hook installed.");
}

void install_hooks() {
    install_hook("via.hid.GamePadDevice", "setMotorPower(via.hid.GamePadMotor, System.Single)",
                 hook_motor, nullptr, "RE Engine motor output");
    install_hook("app.PlayerNero", "set_exceedReqTrigger(System.Boolean)", hook_exceed_input,
                 nullptr, "Nero Exceed input");
    install_hook("app.PlayerNero", "setMaxAct(System.Boolean)", hook_max_act, nullptr,
                 "Nero MAX-Act");
    install_hook("app.player.ExceedGauge", "addStock(System.Int32, System.Boolean, System.Boolean)",
                 hook_stock, nullptr, "Nero EX/MAX-Act stock");
    install_hook("app.PlayerNero", "onBlueRoseChargeStart()", hook_charge_start, nullptr,
                 "Blue Rose charge start");
    install_hook("app.PlayerNero", "onBlueRoseChargeLevelUp()", hook_charge_level, nullptr,
                 "Blue Rose charge level");
    install_hook("app.PlayerNero", "onBlueRoseChargeCancel()", hook_charge_end, nullptr,
                 "Blue Rose charge cancel");
    install_hook("app.PlayerNero", "onBlueRoseChargeComplete()", hook_charge_level, nullptr,
                 "Blue Rose charge complete");
    install_hook("app.PlayerNero", "setBRShot(System.Boolean, System.Boolean)",
                 hook_blue_rose_shot, nullptr, "Blue Rose shot HD haptic");
    install_hook("app.PlayerDante", "createShell(app.ShellTrack)", hook_dante_shell_pre,
                 hook_dante_shell_post, "Dante firearm HD haptics");
    install_hook("app.PlayerDante", "set_isEbonyShot(System.Boolean)", hook_dante_ebony,
                 nullptr, "Dante Ebony/Ivory selector");
    install_hook("app.PlayerVergilPL", "onChargeCompleteJudgementCut()", hook_judgement,
                 nullptr, "Vergil Judgment Cut");
    install_hook("app.PlayerVergilPL", "finishJudgementCutEnd()", hook_judgement_end,
                 nullptr, "Vergil Judgment Cut End");
    install_hook("app.PlayerVergilPL", "onCheckChargeStartBeowulf()", hook_beowulf_pre,
                 nullptr, "Vergil Beowulf pre-impact");
    install_hook("app.PlayerVergilPL", "setBeowulfJustReleaseRate(app.HitController.DamageInfo)",
                 hook_beowulf_impact, nullptr, "Vergil Beowulf impact");
    install_hook("app.fsm2.player.pl0800.PL0820ForceedgeDeadlyAction",
                 "start(via.behaviortree.ActionArg)", hook_mirage_loop, nullptr,
                 "Mirage Edge special loop");
    install_hook("app.fsm2.player.pl0800.PL0820ForceedgeDeadlyAction",
                 "end(via.behaviortree.ActionArg)", hook_mirage_end, nullptr,
                 "Mirage Edge special end");
    for (const auto* type : {"app.Player", "app.PlayerNero", "app.PlayerDante",
                             "app.PlayerV", "app.PlayerVergilPL"})
        install_hook(type, "attackHitCore(app.HitController.DamageInfo)", hook_weapon_hit,
                     nullptr, "Weapon hit");
}

std::filesystem::path game_directory() {
    std::wstring path(32768, L'\0');
    path.resize(GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size())));
    return std::filesystem::path(path).parent_path();
}

void start_bridge() {
    const auto executable = g_base_directory / L"DMC5DualSense.Bridge.exe";
    if (!std::filesystem::exists(executable)) {
        log_error("Bridge executable not found: " + executable.string());
        return;
    }
    std::wstring command = L'"' + executable.wstring() + L"\" --parent " +
                           std::to_wstring(GetCurrentProcessId());
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    if (CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE,
                       CREATE_NO_WINDOW, nullptr, g_base_directory.c_str(), &startup, &process)) {
        runtime_log("Bridge startup requested by the in-game plugin; PID " +
                    std::to_string(process.dwProcessId) + ".");
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
    } else {
        log_error("Bridge startup failed with Windows error " +
                  std::to_string(GetLastError()) + ". Gameplay hooks will continue.");
    }
}

bool read_calibration_setting() {
    std::ifstream stream(g_base_directory / L"config.json", std::ios::binary);
    if (!stream) return false;
    std::string value((std::istreambuf_iterator<char>(stream)), {});
    const auto lower = lower_ascii(value);
    const auto key = lower.find("enablecalibrationlog");
    if (key == std::string::npos) return false;
    return lower.substr(key, 80).find("true") != std::string::npos;
}

void hide_host_console() {
    if (HWND window = GetConsoleWindow(); window != nullptr) ShowWindow(window, SW_HIDE);
}

int read_port_setting() {
    std::ifstream stream(g_base_directory / L"config.json", std::ios::binary);
    if (!stream) return kPort;
    std::string value((std::istreambuf_iterator<char>(stream)), {});
    const auto lower = lower_ascii(value);
    const auto key = lower.find("\"port\"");
    if (key == std::string::npos) return kPort;
    const auto colon = lower.find(':', key + 6);
    if (colon == std::string::npos) return kPort;
    const char* begin = lower.c_str() + colon + 1;
    char* end{};
    const long port = std::strtol(begin, &end, 10);
    return end != begin && port >= 1 && port <= 65535 ? static_cast<int>(port) : kPort;
}

void initialize_udp(int port) {
    WSADATA data{};
    WSAStartup(MAKEWORD(2, 2), &data);
    g_udp = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    g_endpoint.sin_family = AF_INET;
    g_endpoint.sin_port = htons(static_cast<u_short>(port));
    g_endpoint.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
}

} // namespace

extern "C" __declspec(dllexport) void
reframework_plugin_required_version(REFrameworkPluginVersion* version) {
    version->major = REFRAMEWORK_PLUGIN_VERSION_MAJOR;
    version->minor = REFRAMEWORK_PLUGIN_VERSION_MINOR;
    version->patch = REFRAMEWORK_PLUGIN_VERSION_PATCH;
    version->game_name = "DMC5";
}

extern "C" __declspec(dllexport) bool
reframework_plugin_initialize(const REFrameworkPluginInitializeParam* param) {
    try {
        g_param = param;
        API::initialize(param);
        hide_host_console();
        g_base_directory = game_directory() / L"DMC5DualSense";
        std::filesystem::create_directories(g_base_directory);
        runtime_log("=== native plugin session " + utc_stamp() + " ===");
        g_calibration_log = read_calibration_setting();
        initialize_udp(read_port_setting());
        start_bridge();
        install_hooks();
        param->functions->on_pre_application_entry("UpdateBehavior", on_update);
        log_info("Native plugin loaded.");
        return true;
    } catch (const std::exception& error) {
        log_error(std::string("Native startup failed: ") + error.what());
        return false;
    } catch (...) {
        log_error("Native startup failed: unknown exception.");
        return false;
    }
}

BOOL APIENTRY DllMain(HANDLE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_DETACH) {
        if (g_udp != INVALID_SOCKET) closesocket(g_udp);
        g_udp = INVALID_SOCKET;
        WSACleanup();
    }
    return TRUE;
}
