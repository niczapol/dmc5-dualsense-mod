#include "platform.hpp"
#include <Windows.h>
#include <iostream>
#include <filesystem>
int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 10;
    auto root = std::filesystem::absolute(argv[1]);
    auto module = LoadLibraryW((root / "steam_api64.dll").c_str());
    if (!module) return 11;
    auto set = reinterpret_cast<void(*)(std::uint64_t)>(GetProcAddress(module,"AuditSetHandle"));
    auto last = reinterpret_cast<std::uint64_t(*)()>(GetProcAddress(module,"AuditLastWrite"));
    auto enumerations = reinterpret_cast<int(*)()>(GetProcAddress(module,"AuditEnumerationCalls"));
    dmc5ds::SteamInputOutputDevice device(root / "DMC5DualSense");
    dmc5ds::ControllerOutput output{};
    if (!device.ensure_connected() || !device.write(output) || last() != 101) return 12;
    std::cout << "PASS initial controller 101 selected\n";
    set(0);
    const bool disconnected_write = device.write(output);
    std::cout << "After disconnect: connected=" << device.connected()
              << " writeReportedSuccess=" << disconnected_write
              << " lastTarget=" << last() << '\n';
    set(202);
    Sleep(1100);
    const bool reconnected_write = device.write(output);
    std::cout << "After reconnect with handle 202: writeReportedSuccess=" << reconnected_write
              << " lastTarget=" << last() << " enumerationCalls=" << enumerations() << '\n';
    const bool passed = !disconnected_write && last() == 202;
    std::cout << (passed ? "PASS" : "FAIL") << " reconnect tracks replacement handle\n";
    return passed ? 0 : 1;
}
