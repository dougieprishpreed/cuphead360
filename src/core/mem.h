#ifndef CH360_CORE_MEM_H
#define CH360_CORE_MEM_H

#include "core/types.h"

namespace ch360 {

void  MemInit();
void  MemShutdown();

void* MemAlloc(u32 size);
void* MemAllocAligned(u32 size, u32 alignment);
void* MemRealloc(void* ptr, u32 size);
void  MemFree(void* ptr);

u32   MemAllocatedBytes();
u32   MemPeakBytes();
u32   MemAllocationCount();

} // namespace ch360

#endif // CH360_CORE_MEM_H
