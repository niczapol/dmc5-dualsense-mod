#pragma once

#include "core.hpp"

#include <cstdint>
#include <filesystem>
#include <memory>
#include <string>

namespace dmc5ds {

struct ControllerWriteDiagnostic {
    std::uint64_t attempts{};
    std::uint64_t successes{};
    std::uint64_t trigger_effect_writes{};
    std::uint64_t rumble_writes{};
};

class SteamInputOutputDevice {
public:
    explicit SteamInputOutputDevice(const std::filesystem::path& base_directory);
    ~SteamInputOutputDevice();
    SteamInputOutputDevice(const SteamInputOutputDevice&) = delete;
    SteamInputOutputDevice& operator=(const SteamInputOutputDevice&) = delete;

    bool ensure_connected();
    bool write(const ControllerOutput& output);
    void reset();
    bool connected() const;
    std::string description() const;
    ControllerWriteDiagnostic take_diagnostic();

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

XInputSnapshot read_first_xinput();

} // namespace dmc5ds
