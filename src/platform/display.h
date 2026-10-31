#ifndef CH360_PLATFORM_DISPLAY_H
#define CH360_PLATFORM_DISPLAY_H

#include "core/types.h"

namespace ch360 {

bool DisplayInit(u32 width, u32 height, bool hidden);
void DisplayShutdown();
void DisplayPresent(const u32* argb_pixels);
void DisplayPump();
bool DisplayShouldQuit();

} // namespace ch360

#endif // CH360_PLATFORM_DISPLAY_H
