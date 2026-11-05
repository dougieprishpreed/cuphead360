/* Start-up probe of the console CPU (logged, synced to the drive, before the
 * player starts). The third hardware run cleared the compare instructions
 * and every load and store form, yet a long built by `sldi; or` and tested
 * by `cmpdi` on the next instruction took its low word's sign
 * (round 11). probe64_raw.S times that: five producers of a
 * 64-bit value whose high word matters (0x00000001_80000000) and five
 * consumers of it, 0 to 12 nops apart, the register first holding a stale
 * 0x7EADBEEF high word. Each line reads, per distance 0 1 2 3 4 6 8 12:
 *   -  right       S  low word sign-extended   T  stale high word
 *   Z  high word zero    ?  something else */
#include <stdio.h>
#include <string.h>

typedef unsigned long long u64;
typedef u64 (*hz_fn)(u64 a, u64 b, const u64* load, u64* store);
extern hz_fn ch360_hz_table[];
u64 ch360_raw_mfmsr(void);

enum { PRODUCERS = 5, CONSUMERS = 5, DISTANCES = 8 };
static const char* const producers[PRODUCERS] = { "or", "sldi+or", "add", "ld", "mulld" };
static const char* const consumers[CONSUMERS] = { "cmpdi", "srdi 32", "std", "mr", "addi" };

static const u64 V = 0x0000000180000000ull;
static volatile u64 g_in[PRODUCERS][2] = {
    { 0x0000000180000000ull, 0 }, { 1, 0x80000000ull }, { 0x100000000ull, 0x80000000ull }, { 0, 0 }, { 0xC0000000ull, 2 },
};

/* What a value, or a compare nibble (8 LT, 4 GT, 2 EQ), says about the high word the consumer saw. */
static char classify(int consumer, u64 got) {
    u64 sign_ext = 0xFFFFFFFF80000000ull, stale = 0x7EADBEEF80000000ull, zero = 0x80000000ull;
    if (consumer == 0) {
        unsigned n = (unsigned)got & 14;
        return n == 4 ? '-' : n == 8 ? 'S' : '?';   /* stale and zero high words compare GT too */
    }
    if (consumer == 1) return got == 1 ? '-' : got == 0xFFFFFFFFull ? 'S' : got == 0x7EADBEEFull ? 'T' : got == 0 ? 'Z' : '?';
    return got == V ? '-' : got == sign_ext ? 'S' : got == stale ? 'T' : got == zero ? 'Z' : '?';
}

typedef void (*log_fn)(const char* line);

/* Runs the table on the calling thread; with detail, one line per producer and
 * consumer, otherwise one summary line (where names the thread). Returns how
 * many results were wrong. */
int ch360_probe64(log_fn log, void (*sync)(void), const char* where, int detail) {
    u64 load_cell = 0x0000000180000000ull, store_cell;
    char line[160];
    int wrong = 0, p, c, d;
    u64 msr = ch360_raw_mfmsr();
    if (detail) log("cpu probe: high word of a fresh 64-bit result, at 0 1 2 3 4 6 8 12 nops (- right, S low word sign-extended, T stale, Z zero)");
    sync();
    for (p = 0; p < PRODUCERS; ++p)
        for (c = 0; c < CONSUMERS; ++c) {
            char marks[DISTANCES + 1];
            for (d = 0; d < DISTANCES; ++d) {
                hz_fn f = ch360_hz_table[(p * CONSUMERS + c) * DISTANCES + d];
                u64 got;
                store_cell = 0;
                got = f(g_in[p][0], g_in[p][1], &load_cell, &store_cell);
                if (c == 2) got = store_cell;
                marks[d] = classify(c, got);
                if (marks[d] != '-') ++wrong;
            }
            marks[DISTANCES] = 0;
            if (detail || strchr(marks, 'S') || strchr(marks, 'T') || strchr(marks, 'Z') || strchr(marks, '?')) {
                snprintf(line, sizeof(line), "cpu probe %s: %-7s > %-7s %s", where, producers[p], consumers[c], marks);
                log(line);
            }
        }
    snprintf(line, sizeof(line), "cpu probe %s: MSR %08x%08x, %d of %d wrong", where, (unsigned)(msr >> 32), (unsigned)msr, wrong,
             PRODUCERS * CONSUMERS * DISTANCES);
    log(line);
    return wrong;
}
