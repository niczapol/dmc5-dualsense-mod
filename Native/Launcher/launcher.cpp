#include <Windows.h>
#include <TlHelp32.h>
#include <shellapi.h>
#include <winsock2.h>
#include <ws2tcpip.h>

#include <algorithm>
#include <cstdio>
#include <string>
#include <string_view>
#include <vector>

namespace {

constexpr unsigned short kBridgePort = 27105;

struct Handle {
    HANDLE value{};
    Handle() = default;
    explicit Handle(HANDLE handle) : value(handle) {}
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value(other.value) { other.value = nullptr; }
    Handle& operator=(Handle&& other) noexcept {
        if (this != &other) {
            if (value != nullptr && value != INVALID_HANDLE_VALUE) CloseHandle(value);
            value = other.value;
            other.value = nullptr;
        }
        return *this;
    }
    ~Handle() { if (value != nullptr && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    explicit operator bool() const { return value != nullptr && value != INVALID_HANDLE_VALUE; }
};

struct ChildProcess {
    Handle process;
    DWORD pid{};
};

std::wstring module_path() {
    std::wstring value(32768, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, value.data(),
                                            static_cast<DWORD>(value.size()));
    if (length == 0 || length >= value.size()) return {};
    value.resize(length);
    return value;
}

std::wstring directory_name(std::wstring path) {
    const auto separator = path.find_last_of(L"\\/");
    return separator == std::wstring::npos ? L"." : path.substr(0, separator);
}

std::wstring file_name(std::wstring_view path) {
    const auto separator = path.find_last_of(L"\\/");
    return std::wstring(separator == std::wstring_view::npos ? path :
                        path.substr(separator + 1));
}

std::wstring join_path(std::wstring_view left, std::wstring_view right) {
    std::wstring result(left);
    if (!result.empty() && result.back() != L'\\' && result.back() != L'/')
        result.push_back(L'\\');
    result.append(right);
    return result;
}

std::wstring lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t character) {
        return static_cast<wchar_t>(towlower(character));
    });
    return value;
}

bool file_exists(std::wstring_view path) {
    const DWORD attributes = GetFileAttributesW(std::wstring(path).c_str());
    return attributes != INVALID_FILE_ATTRIBUTES &&
           (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

void append_log(const std::wstring& path, std::string_view message) {
    Handle file(CreateFileW(path.c_str(), FILE_APPEND_DATA,
                            FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
                            FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file) return;
    SYSTEMTIME now{};
    GetLocalTime(&now);
    char prefix[64]{};
    const int count = std::snprintf(prefix, sizeof(prefix),
        "[%04u-%02u-%02u %02u:%02u:%02u.%03u] ", now.wYear, now.wMonth,
        now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds);
    DWORD written{};
    if (count > 0) WriteFile(file.value, prefix, count, &written, nullptr);
    WriteFile(file.value, message.data(), static_cast<DWORD>(message.size()), &written, nullptr);
    WriteFile(file.value, "\r\n", 2, &written, nullptr);
}

std::wstring quote(std::wstring_view argument) {
    if (argument.find_first_of(L" \t\"") == std::wstring_view::npos)
        return std::wstring(argument);
    std::wstring result{L'"'};
    std::size_t slashes{};
    for (wchar_t character : argument) {
        if (character == L'\\') ++slashes;
        else if (character == L'"') {
            result.append(slashes * 2 + 1, L'\\');
            result.push_back(L'"');
            slashes = 0;
        } else {
            result.append(slashes, L'\\');
            slashes = 0;
            result.push_back(character);
        }
    }
    result.append(slashes * 2, L'\\');
    result.push_back(L'"');
    return result;
}

bool start_process(const std::wstring& executable,
                   const std::vector<std::wstring>& arguments,
                   const std::wstring& working_directory, bool hidden,
                   ChildProcess& child) {
    std::wstring command = quote(executable);
    for (const auto& argument : arguments) command += L" " + quote(argument);
    STARTUPINFOW startup{sizeof(startup)};
    if (hidden) {
        startup.dwFlags = STARTF_USESHOWWINDOW;
        startup.wShowWindow = SW_HIDE;
    }
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE,
                        hidden ? CREATE_NO_WINDOW : 0, nullptr,
                        working_directory.c_str(), &startup, &process)) return false;
    CloseHandle(process.hThread);
    child.process = Handle(process.hProcess);
    child.pid = process.dwProcessId;
    return true;
}

std::vector<DWORD> process_ids(std::wstring_view wanted) {
    std::vector<DWORD> result;
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (!snapshot) return result;
    PROCESSENTRY32W entry{sizeof(entry)};
    if (!Process32FirstW(snapshot.value, &entry)) return result;
    const auto target = lower(std::wstring(wanted));
    do {
        if (lower(entry.szExeFile) == target) result.push_back(entry.th32ProcessID);
    } while (Process32NextW(snapshot.value, &entry));
    return result;
}

void send_bridge_shutdown() {
    WSADATA data{};
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0) return;
    const SOCKET socket_handle = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket_handle != INVALID_SOCKET) {
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_port = htons(kBridgePort);
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        constexpr char payload[] = "{\"v\":1,\"type\":\"shutdown\"}";
        sendto(socket_handle, payload, sizeof(payload) - 1, 0,
               reinterpret_cast<const sockaddr*>(&address), sizeof(address));
        closesocket(socket_handle);
    }
    WSACleanup();
}

void stop_orphan_bridges(const std::wstring& log_path) {
    const auto ids = process_ids(L"DMC5DualSense.Bridge.exe");
    if (ids.empty()) return;
    send_bridge_shutdown();
    Sleep(600);
    for (DWORD id : ids) {
        Handle process(OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE, FALSE, id));
        if (process && WaitForSingleObject(process.value, 0) == WAIT_TIMEOUT) {
            TerminateProcess(process.value, 1);
            append_log(log_path, "Stopped stale session bridge PID " + std::to_string(id) + ".");
        }
    }
}

bool read_file(std::wstring_view path, std::string& result) {
    Handle file(CreateFileW(std::wstring(path).c_str(), GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file) return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file.value, &size) || size.QuadPart < 0 ||
        size.QuadPart > 1024 * 1024) return false;
    result.resize(static_cast<std::size_t>(size.QuadPart));
    DWORD read{};
    return result.empty() || (ReadFile(file.value, result.data(),
        static_cast<DWORD>(result.size()), &read, nullptr) && read == result.size());
}

bool json_bool(std::string value, std::string property, bool fallback) {
    std::transform(value.begin(), value.end(), value.begin(), ::tolower);
    std::transform(property.begin(), property.end(), property.begin(), ::tolower);
    const auto key = value.find("\"" + property + "\"");
    if (key == std::string::npos) return fallback;
    const auto colon = value.find(':', key);
    const auto start = value.find_first_not_of(" \t\r\n", colon + 1);
    if (start == std::string::npos) return fallback;
    if (value.compare(start, 4, "true") == 0) return true;
    if (value.compare(start, 5, "false") == 0) return false;
    return fallback;
}

bool wait_for_bridge(const std::wstring& ready_path, bool require_haptics,
                     DWORD timeout_ms) {
    const ULONGLONG deadline = GetTickCount64() + timeout_ms;
    while (GetTickCount64() < deadline) {
        std::string ready;
        if (read_file(ready_path, ready) &&
            (!require_haptics || json_bool(ready, "advancedHapticsReady", false)))
            return true;
        Sleep(50);
    }
    return false;
}

void hide_parent_command_window() {
    if (HWND window = GetConsoleWindow(); window != nullptr) ShowWindow(window, SW_HIDE);
}

} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    const auto launcher = module_path();
    if (launcher.empty()) return 4;
    const auto base = directory_name(launcher);
    const auto log_path = join_path(base, L"launcher.log");
    const auto ready_path = join_path(base, L"bridge.ready.json");
    hide_parent_command_window();

    int count{};
    LPWSTR* raw = CommandLineToArgvW(GetCommandLineW(), &count);
    if (raw == nullptr) return 4;
    std::vector<std::wstring> arguments;
    for (int index = 1; index < count; ++index) arguments.emplace_back(raw[index]);
    LocalFree(raw);

    if (std::any_of(arguments.begin(), arguments.end(), [](const auto& value) {
            return lower(value) == L"--background";
        })) {
        append_log(log_path, "Background/resident mode is intentionally unsupported.");
        return 2;
    }

    std::wstring game;
    std::vector<std::wstring> game_arguments;
    if (!arguments.empty() && file_exists(arguments.front()) &&
        lower(file_name(arguments.front())) == L"devilmaycry5.exe") {
        game = arguments.front();
        game_arguments.assign(arguments.begin() + 1, arguments.end());
    } else {
        game = join_path(directory_name(base), L"DevilMayCry5.exe");
        game_arguments = arguments;
    }
    if (!file_exists(game)) {
        append_log(log_path, "DevilMayCry5.exe was not supplied by Steam and was not found.");
        return 2;
    }
    if (!process_ids(L"DevilMayCry5.exe").empty()) {
        append_log(log_path, "DMC5 is already running; no second session was created.");
        return 0;
    }

    stop_orphan_bridges(log_path);
    DeleteFileW(ready_path.c_str());
    DeleteFileW((ready_path + L".tmp").c_str());

    ChildProcess bridge{};
    const auto bridge_exe = join_path(base, L"DMC5DualSense.Bridge.exe");
    const bool bridge_started = file_exists(bridge_exe) && start_process(
        bridge_exe, {L"--parent", std::to_wstring(GetCurrentProcessId())}, base, true, bridge);

    std::string config;
    const bool require_haptics = !read_file(join_path(base, L"config.json"), config) ||
        json_bool(config, "EnableAdvancedHaptics", true);
    if (bridge_started && !wait_for_bridge(ready_path, require_haptics, 3000))
        append_log(log_path, "Bridge readiness was not complete after 3 seconds; session continues.");
    else if (!bridge_started)
        append_log(log_path, "Bridge could not be started; DMC5 will still be launched.");

    ChildProcess dmc{};
    if (!start_process(game, game_arguments, directory_name(game), false, dmc)) {
        append_log(log_path, "Steam command did not create a DMC5 process.");
        if (bridge_started) send_bridge_shutdown();
        return 3;
    }
    append_log(log_path, "DMC5 started as PID " + std::to_string(dmc.pid) +
                         "; session bridge PID " +
                         (bridge_started ? std::to_string(bridge.pid) : "none") + ".");
    WaitForSingleObject(dmc.process.value, INFINITE);
    DWORD exit_code{};
    GetExitCodeProcess(dmc.process.value, &exit_code);

    if (bridge_started) {
        send_bridge_shutdown();
        if (WaitForSingleObject(bridge.process.value, 2000) == WAIT_TIMEOUT)
            TerminateProcess(bridge.process.value, 1);
    }
    DeleteFileW(ready_path.c_str());
    append_log(log_path, "DMC5 exited with code " + std::to_string(exit_code) + ".");
    return static_cast<int>(exit_code);
}
