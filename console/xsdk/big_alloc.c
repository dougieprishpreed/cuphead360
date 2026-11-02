/* Big blocks (1 MB and up: a texture's levels, a background's pack read)
 * from the kernel's pages instead of newlib's heap, each given back whole
 * when freed. xsdk's sbrk returns only the heap's top, so a 10 MB block
 * freed between smaller live ones stayed the heap's: a console session's
 * heap held 139 MB with 97 in use (each shop's 10 MB backdrop took more),
 * and the GPU's memory ran out (a texture made again and again after a
 * save and quit). The link wraps malloc, free, realloc and calloc
 * (CMakeLists.txt); newlib's own _malloc_r / _free_r pairs never see a big
 * block (its buffers are small), and a block this file did not make goes
 * to newlib's free. Without the kernel's pages (address space gone) a big
 * block comes from the heap as before. */
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include <xsdk/kernel.h>

void* __real_malloc(size_t n);
void __real_free(void* p);
void* __real_realloc(void* p, size_t n);
void* __real_calloc(size_t n, size_t size);

enum { BIG = 1024 * 1024, MAX_BIG = 256 };

static struct {
    void* p;
    size_t n;
} g_big[MAX_BIG];
static volatile int g_lock;
/* the span of every big block made so far (never shrinks): a pointer out of
 * it is newlib's, without the lock */
static volatile uintptr_t g_lo = UINTPTR_MAX, g_hi = 0;
static volatile size_t g_bytes;

static void lock(void) {
    while (__sync_lock_test_and_set(&g_lock, 1))
        while (g_lock) {
        }
}

static void unlock(void) { __sync_lock_release(&g_lock); }

static void* pages(size_t n) {
    void* p = NULL;
    uint32_t size = (uint32_t)n;
    if (n > 0x7FFFFFFFu || !NT_SUCCESS(NtAllocateVirtualMemory(&p, &size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE, 0)))
        return NULL;
    lock();
    int i;
    for (i = 0; i < MAX_BIG && g_big[i].p; ++i) {
    }
    if (i == MAX_BIG) {
        unlock();
        uint32_t none = 0;
        NtFreeVirtualMemory(&p, &none, MEM_RELEASE, 0);
        return NULL;
    }
    g_big[i].p = p;
    g_big[i].n = n;
    g_bytes += n;
    if ((uintptr_t)p < g_lo) g_lo = (uintptr_t)p;
    if ((uintptr_t)p + n > g_hi) g_hi = (uintptr_t)p + n;
    unlock();
    return p;
}

/* the big block at p (its size in *n), taken out of the table if take */
static int big_block(void* p, size_t* n, int take) {
    if (!p || (uintptr_t)p < g_lo || (uintptr_t)p >= g_hi) return 0;
    int found = 0;
    lock();
    for (int i = 0; i < MAX_BIG; ++i)
        if (g_big[i].p == p) {
            *n = g_big[i].n;
            if (take) {
                g_big[i].p = NULL;
                g_bytes -= g_big[i].n;
            }
            found = 1;
            break;
        }
    unlock();
    return found;
}

static void release(void* p) {
    uint32_t none = 0;
    NtFreeVirtualMemory(&p, &none, MEM_RELEASE, 0);
}

size_t spire_big_bytes(void) { return g_bytes; }

void* __wrap_malloc(size_t n) {
    if (n >= BIG) {
        void* p = pages(n);
        if (p) return p;
    }
    return __real_malloc(n);
}

void __wrap_free(void* p) {
    size_t n;
    if (big_block(p, &n, 1)) release(p);
    else __real_free(p);
}

void* __wrap_calloc(size_t count, size_t size) {
    if (size && count > (size_t)-1 / size) return NULL;
    /* the kernel's pages come zeroed */
    if (count * size >= BIG) {
        void* p = pages(count * size);
        if (p) return p;
    }
    return __real_calloc(count, size);
}

void* __wrap_realloc(void* p, size_t n) {
    size_t had;
    if (!big_block(p, &had, 0)) {
        /* newlib's block: grown or shrunk where it is */
        return __real_realloc(p, n);
    }
    if (n <= had && n >= BIG) return p;
    void* q = __wrap_malloc(n);
    if (!q) return NULL;
    memcpy(q, p, n < had ? n : had);
    __wrap_free(p);
    return q;
}
