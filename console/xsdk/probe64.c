/* Start-up probe of the console CPU's loads and stores (logged, synced to the
 * drive, before the player starts). The guest keeps many 64-bit values only
 * 4-byte aligned, and the second hardware run formatted such values with
 * their low word's sign (docs/VERIFICATION.md, round 8), while the
 * doubleword compares themselves were right. Each instruction in
 * probe64_raw.S runs at every offset 0-7 inside a cache line, across a
 * cache line and across a page, against the bytes it should give or leave,
 * and the aligned and misaligned forms are timed. The player's own 64-bit
 * accesses go through two 32-bit ones (console/w2c2/w2c2_base.h), checked
 * here the same way. */
#include <stdio.h>
#include <string.h>
#include <time.h>

typedef unsigned long long u64;

u64 ch360_raw_ld(const void* p);
void ch360_raw_std(void* p, u64 v);
u64 ch360_raw_ldbrx(const void* p);
void ch360_raw_stdbrx(void* p, u64 v);
unsigned ch360_raw_lwz(const void* p);
void ch360_raw_stw(void* p, u64 v);
unsigned ch360_raw_lwbrx(const void* p);
void ch360_raw_stwbrx(void* p, u64 v);
void ch360_raw_ld_loop(const void* p, unsigned n);
void ch360_raw_lwz_loop(const void* p, unsigned n);

/* The player's accessors as console/w2c2/w2c2_base.h has them. */
static u64 acc_load(const unsigned char* p) {
    unsigned lo, hi;
    memcpy(&lo, p, 4);
    memcpy(&hi, p + 4, 4);
    return ((u64)__builtin_bswap32(hi) << 32) | __builtin_bswap32(lo);
}
static void acc_store(unsigned char* p, u64 v) {
    unsigned lo = __builtin_bswap32((unsigned)v), hi = __builtin_bswap32((unsigned)(v >> 32));
    memcpy(p, &lo, 4);
    memcpy(p + 4, &hi, 4);
}

static unsigned char g_buf[3 * 65536] __attribute__((aligned(65536)));
static const u64 STORED = 0x0102030405060708ull;

/* The bytes' value read big-endian (be) or little-endian. */
static u64 bytes_value(const unsigned char* p, int n, int be) {
    u64 v = 0;
    int i;
    for (i = 0; i < n; ++i) v = v << 8 | p[be ? i : n - 1 - i];
    return v;
}

static void fill(void) {
    unsigned i;
    for (i = 0; i < sizeof(g_buf); ++i) g_buf[i] = (unsigned char)(i * 7 + 1);
}

typedef void (*log_fn)(const char* line);

static double now_us(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec * 1e6 + (double)ts.tv_nsec / 1e3;
}

/* Where a case runs: offset o of a base inside a line, at a line's end, at a page's end. */
static unsigned char* place(int where, int o) {
    return where == 0 ? g_buf + 256 + o : where == 1 ? g_buf + 128 * 4 - 4 + o : g_buf + 65536 - 4 + o;
}

/* One load instruction (kind 0 ld, 1 ldbrx, 2 lwz, 3 lwbrx, 4 the player's accessor) at every place; returns the wrong count. */
static int probe_load(log_fn log, void (*sync)(void), int kind) {
    static const char* const names[] = { "ld", "ldbrx", "lwz", "lwbrx", "player load64" };
    char line[400];
    int len = 0, wrong = 0, where, o;
    len += snprintf(line + len, sizeof(line) - len, "mem probe %s:", names[kind]);
    sync();
    for (where = 0; where < 3; ++where)
        for (o = 0; o < 8; ++o) {
            unsigned char* p = place(where, o);
            int n = kind == 2 || kind == 3 ? 4 : 8;
            u64 got, want = bytes_value(p, n, kind == 0 || kind == 2);
            got = kind == 0 ? ch360_raw_ld(p) : kind == 1 ? ch360_raw_ldbrx(p) : kind == 2 ? ch360_raw_lwz(p)
                : kind == 3 ? ch360_raw_lwbrx(p) : acc_load(p);
            if (got != want) {
                if (++wrong <= 3)
                    len += snprintf(line + len, sizeof(line) - len, " [%s+%d got %08x%08x want %08x%08x]",
                                    where == 0 ? "line" : where == 1 ? "line end" : "page end", o, (unsigned)(got >> 32),
                                    (unsigned)got, (unsigned)(want >> 32), (unsigned)want);
            }
        }
    snprintf(line + len, sizeof(line) - len, " %d of 24 wrong", wrong);
    log(line);
    return wrong;
}

static int probe_store(log_fn log, void (*sync)(void), int kind) {
    static const char* const names[] = { "std", "stdbrx", "stw", "stwbrx", "player store64" };
    char line[400];
    int len = 0, wrong = 0, where, o;
    len += snprintf(line + len, sizeof(line) - len, "mem probe %s:", names[kind]);
    sync();
    for (where = 0; where < 3; ++where)
        for (o = 0; o < 8; ++o) {
            unsigned char* p = place(where, o);
            unsigned char want[16], got[16];
            int n = kind == 2 || kind == 3 ? 4 : 8, i;
            u64 v = n == 4 ? STORED & 0xFFFFFFFFull : STORED;
            fill();
            memcpy(want, p - 4, 16);
            for (i = 0; i < n; ++i) {
                int be = kind == 0 || kind == 2;
                want[4 + i] = (unsigned char)(v >> 8 * (be ? n - 1 - i : i));
            }
            if (kind == 0) ch360_raw_std(p, v);
            else if (kind == 1) ch360_raw_stdbrx(p, v);
            else if (kind == 2) ch360_raw_stw(p, v);
            else if (kind == 3) ch360_raw_stwbrx(p, v);
            else acc_store(p, v);
            memcpy(got, p - 4, 16);
            if (memcmp(got, want, 16) != 0) {
                if (++wrong <= 3) {
                    len += snprintf(line + len, sizeof(line) - len, " [%s+%d:", where == 0 ? "line" : where == 1 ? "line end" : "page end", o);
                    for (i = 0; i < 16; ++i) len += snprintf(line + len, sizeof(line) - len, " %02x", got[i]);
                    len += snprintf(line + len, sizeof(line) - len, "]");
                }
            }
        }
    snprintf(line + len, sizeof(line) - len, " %d of 24 wrong", wrong);
    log(line);
    return wrong;
}

/* Writes the probe's lines; returns how many of the player's own accesses were wrong. */
int ch360_probe64(log_fn log, void (*sync)(void)) {
    char line[200];
    int k, player = 0, o;
    fill();
    for (k = 0; k < 5; ++k) {
        int w = probe_load(log, sync, k);
        if (k == 4) player += w;
    }
    for (k = 0; k < 5; ++k) {
        int w = probe_store(log, sync, k);
        if (k == 4) player += w;
    }
    /* Cost: 20000 loads at each offset inside a line, nanoseconds a load. */
    sync();
    for (k = 0; k < 2; ++k) {
        int len = snprintf(line, sizeof(line), "mem probe %s cost, ns a load at offset 0-7:", k ? "lwz" : "ld");
        for (o = 0; o < 8; ++o) {
            double t0 = now_us();
            if (k) ch360_raw_lwz_loop(g_buf + 256 + o, 20000);
            else ch360_raw_ld_loop(g_buf + 256 + o, 20000);
            len += snprintf(line + len, sizeof(line) - len, " %u", (unsigned)((now_us() - t0) * 1000.0 / 20000));
        }
        log(line);
    }
    return player;
}
