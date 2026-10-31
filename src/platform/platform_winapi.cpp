#include "platform/platform.h"

#if defined(_XBOX)
#include <xtl.h>
#else
#include <windows.h>
#endif

namespace ch360 {

// Common Win32-family platform services. Xbox 360 exposes a subset of the
// Win32 API (xtl.h); both targets use it here so timing, sleep, and debug
// break behaviour stay identical. Display/audio/input backends live in
// separate files under the platform directories.

bool PlatformInit() { return true; }
void PlatformShutdown() {}

const char* PlatformName() {
#if defined(_XBOX)
    return "xbox360";
#else
    return "win32";
#endif
}

const char* PlatformDefaultLogPath() {
#if defined(_XBOX)
    return "game:\\cuphead360.log";
#else
    return "cuphead360_win32.log";
#endif
}

u32 PlatformNowMs() {
    return (u32)GetTickCount();
}

void PlatformSleepMs(u32 ms) {
    if (ms) Sleep(ms);
}

void PlatformDebugBreak() {
#if defined(_XBOX)
    DebugBreak();
#else
    __debugbreak();
#endif
}

void PlatformDebugOutput(const char* text) {
    if (text) OutputDebugStringA(text);
}

} // namespace ch360
