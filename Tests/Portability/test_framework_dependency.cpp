// Resolves the distributed C++/CLI plugin runtimeconfig without loading the game/plugin.
#include <Windows.h>
#include <hostfxr.h>
#include <filesystem>
#include <iostream>
int wmain(int argc, wchar_t** argv) {
    if (argc != 4) return 10;
    auto module=LoadLibraryW(argv[1]);
    if (!module) return 11;
    auto init=reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(GetProcAddress(module,"hostfxr_initialize_for_runtime_config"));
    auto close=reinterpret_cast<hostfxr_close_fn>(GetProcAddress(module,"hostfxr_close"));
    if (!init || !close) return 12;
    hostfxr_initialize_parameters params{};
    params.size=sizeof(params);
    params.dotnet_root=argv[3];
    hostfxr_handle context{};
    const auto result=init(argv[2], &params, &context);
    std::wcout << L"dotnet_root=" << argv[3] << L" result=0x" << std::hex << static_cast<unsigned>(result) << '\n';
    if (context) close(context);
    FreeLibrary(module);
    return result == 0 ? 0 : 1;
}
