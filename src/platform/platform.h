#ifndef CH360_PLATFORM_PLATFORM_H
#define CH360_PLATFORM_PLATFORM_H

#include "core/types.h"

namespace ch360 {

bool PlatformInit();
void PlatformShutdown();

const char* PlatformName();
const char* PlatformDefaultLogPath();
u32  PlatformNowMs();
void PlatformSleepMs(u32 ms);
void PlatformDebugBreak();
void PlatformDebugOutput(const char* text);

} // namespace ch360

#endif // CH360_PLATFORM_PLATFORM_H
