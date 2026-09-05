// Audit-only Steam API test double. Never install in a game directory.
#include <cstdint>
#include <cstring>
static std::uint64_t current_handle = 101, last_write = 0;
static int enumeration_calls = 0;
static void* input_vtable[48]{};
static void* client_vtable[39]{};
static void** input_object = input_vtable;
static void** client_object = client_vtable;
static bool input_init(void*, bool) { return true; }
static bool input_shutdown(void*) { return true; }
static void run_frame(void*, bool) {}
static int enumerate(void*, std::uint64_t* handles) {
    ++enumeration_calls;
    if (current_handle == 0) return 0;
    handles[0] = current_handle;
    return 1;
}
static void vibrate(void*, std::uint64_t handle, std::uint16_t, std::uint16_t) { last_write = handle; }
static void led(void*, std::uint64_t handle, std::uint8_t, std::uint8_t, std::uint8_t, std::uint32_t) { last_write = handle; }
static int input_type(void*, std::uint64_t) { return 13; }
static void trigger(void*, std::uint64_t handle, void*) { last_write = handle; }
static void* get_input(void*, int, int, const char*) { return &input_object; }
#define EXPORT extern "C" __declspec(dllexport)
EXPORT bool SteamAPI_Init() {
    input_vtable[0] = reinterpret_cast<void*>(input_init);
    input_vtable[1] = reinterpret_cast<void*>(input_shutdown);
    input_vtable[3] = reinterpret_cast<void*>(run_frame);
    input_vtable[6] = reinterpret_cast<void*>(enumerate);
    input_vtable[30] = reinterpret_cast<void*>(vibrate);
    input_vtable[33] = reinterpret_cast<void*>(led);
    input_vtable[37] = reinterpret_cast<void*>(input_type);
    input_vtable[47] = reinterpret_cast<void*>(trigger);
    client_vtable[38] = reinterpret_cast<void*>(get_input);
    return true;
}
EXPORT void SteamAPI_Shutdown() {}
EXPORT int SteamAPI_GetHSteamUser() { return 1; }
EXPORT int SteamAPI_GetHSteamPipe() { return 1; }
EXPORT void* SteamInternal_CreateInterface(const char*) { return &client_object; }
EXPORT void AuditSetHandle(std::uint64_t value) { current_handle = value; }
EXPORT std::uint64_t AuditLastWrite() { return last_write; }
EXPORT int AuditEnumerationCalls() { return enumeration_calls; }
