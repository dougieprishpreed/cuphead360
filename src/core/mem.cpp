#include "core/mem.h"
#include "core/log.h"

#include <stdlib.h>
#include <string.h>
#include <stddef.h>

namespace ch360 {

static u32 s_allocated = 0;
static u32 s_peak = 0;
static u32 s_count = 0;

// Small allocation header. Keeps alignment at 16 bytes on both targets.
struct AllocHeader {
    u32 magic;
    u32 size;
    u32 pad0;
    u32 pad1;
};

static const u32 kMagic = 0x43483336u; // "CH36"
static const u32 kHeaderSize = 16;

void MemInit() {
    s_allocated = 0;
    s_peak = 0;
    s_count = 0;
}

void MemShutdown() {
    if (s_allocated != 0) {
        LogWarn("mem", "shutdown with %u bytes still allocated in %u blocks", s_allocated, s_count);
    }
}

void* MemAlloc(u32 size) {
    if (size == 0) size = 1;
    u8* raw = (u8*)malloc(size + kHeaderSize);
    if (!raw) {
        LogError("mem", "out of memory allocating %u bytes", size);
        return 0;
    }
    AllocHeader* h = (AllocHeader*)raw;
    h->magic = kMagic;
    h->size = size;
    h->pad0 = 0;
    h->pad1 = 0;
    s_allocated += size;
    ++s_count;
    if (s_allocated > s_peak) s_peak = s_allocated;
    return raw + kHeaderSize;
}

void* MemAllocAligned(u32 size, u32 alignment) {
    if (!IsPowerOfTwo(alignment) || alignment < 16) return MemAlloc(size);
    // Reserve enough for the header plus alignment slack. Store the original
    // pointer immediately before the aligned result.
    u32 total = size + alignment + kHeaderSize;
    u8* raw = (u8*)malloc(total);
    if (!raw) {
        LogError("mem", "out of memory allocating %u aligned bytes", size);
        return 0;
    }
    u8* payload = raw + kHeaderSize;
    u32 misalign = ((u32)(size_t)payload) & (alignment - 1u);
    u8* aligned = payload + (misalign ? (alignment - misalign) : 0);
    AllocHeader* h = (AllocHeader*)(aligned - kHeaderSize);
    h->magic = kMagic | 0x80000000u;
    h->size = size;
    h->pad0 = (u32)(size_t)raw;
    h->pad1 = 0;
    s_allocated += size;
    ++s_count;
    if (s_allocated > s_peak) s_peak = s_allocated;
    return aligned;
}

void MemFree(void* ptr) {
    if (!ptr) return;
    AllocHeader* h = (AllocHeader*)((u8*)ptr - kHeaderSize);
    if ((h->magic & 0x7FFFFFFFu) != kMagic) {
        LogError("mem", "free of invalid pointer %p", ptr);
        return;
    }
    if (s_allocated >= h->size) s_allocated -= h->size;
    else s_allocated = 0;
    if (s_count) --s_count;
    if (h->magic & 0x80000000u) {
        free((void*)(size_t)h->pad0);
    } else {
        free(h);
    }
}

void* MemRealloc(void* ptr, u32 size) {
    if (!ptr) return MemAlloc(size);
    AllocHeader* h = (AllocHeader*)((u8*)ptr - kHeaderSize);
    if ((h->magic & 0x7FFFFFFFu) != kMagic) {
        LogError("mem", "realloc of invalid pointer %p", ptr);
        return 0;
    }
    void* fresh = MemAlloc(size);
    if (!fresh) return 0;
    u32 copy = h->size < size ? h->size : size;
    memcpy(fresh, ptr, copy);
    MemFree(ptr);
    return fresh;
}

u32 MemAllocatedBytes() { return s_allocated; }
u32 MemPeakBytes() { return s_peak; }
u32 MemAllocationCount() { return s_count; }

} // namespace ch360
