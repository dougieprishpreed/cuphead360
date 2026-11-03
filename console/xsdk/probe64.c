/* Start-up probe of the console CPU (logged, synced to the drive, before the
 * player starts). The third hardware run cleared the compare instructions
 * and every load and store form, yet a long built by `sldi; or` and tested
 * by `cmpdi` on the next instruction took its low word's sign
 * (docs/VERIFICATION.md). probe64_raw.S times that: five producers of a
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

/* Writes one line per producer and consumer; returns how many results were wrong. */
int ch360_probe64(log_fn log, void (*sync)(void)) {
    static u64 load_cell = 0x0000000180000000ull, store_cell;
    char line[160];
    int wrong = 0, p, c, d;
    log("cpu probe: high word of a fresh 64-bit result, at 0 1 2 3 4 6 8 12 nops (- right, S low word sign-extended, T stale, Z zero)");
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
            snprintf(line, sizeof(line), "cpu probe %-7s > %-7s %s", producers[p], consumers[c], marks);
            log(line);
        }
    snprintf(line, sizeof(line), "cpu probe: %d of %d wrong", wrong, PRODUCERS * CONSUMERS * DISTANCES);
    log(line);
    return wrong;
}
