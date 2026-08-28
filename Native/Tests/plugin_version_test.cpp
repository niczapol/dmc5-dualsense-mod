#include <windows.h>

#include <iostream>
#include <string_view>

struct PluginVersion {
    int major;
    int minor;
    int patch;
    const char* game_name;
};

using RequiredVersionFn = void (*)(PluginVersion*);

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) {
        std::cerr << "Expected the built plugin path.\n";
        return 2;
    }

    const auto module = LoadLibraryW(argv[1]);
    if (module == nullptr) {
        std::cerr << "Unable to load the built plugin.\n";
        return 3;
    }

    const auto required_version = reinterpret_cast<RequiredVersionFn>(
        GetProcAddress(module, "reframework_plugin_required_version"));
    if (required_version == nullptr) {
        std::cerr << "The plugin does not export reframework_plugin_required_version.\n";
        FreeLibrary(module);
        return 4;
    }

    PluginVersion version{};
    required_version(&version);
    const bool valid = version.major == 1 && version.minor == 10 &&
        version.patch == 0 && version.game_name != nullptr &&
        std::string_view{version.game_name} == "DMC5";
    FreeLibrary(module);

    if (!valid) {
        std::cerr << "Unexpected REFramework plugin requirement: "
                  << version.major << '.' << version.minor << '.' << version.patch
                  << "\n";
        return 5;
    }

    std::cout << "PASS Native plugin declares REFramework Plugin API 1.10\n";
    return 0;
}
