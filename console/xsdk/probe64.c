/* Start-up probe of the console CPU's 64-bit compares (logged before the
 * player starts): the doubleword compare instructions as they are
 * (probe64_raw.S) beside the same compares in C, which safecmp.py rewrites
 * into word compares. Each case's two operands differ in the high word, so a
 * compare of the low words alone gives the wrong answer. */
#include <stdio.h>
#include <string.h>

typedef unsigned long long u64;
typedef long long i64;

unsigned ch360_raw_cmpd(u64 a, u64 b);
unsigned ch360_raw_cmpld(u64 a, u64 b);
unsigned ch360_raw_cmpdi0(u64 a);
unsigned ch360_raw_cntlzd(u64 a);

/* Kept out of reach of constant folding. */
static volatile u64 g_vals[5] = { 1ull << 32, 1, 1ull << 31, 0xFFFFFFFFull, (1ull << 32) + 5 };

__attribute__((noinline)) static unsigned c_cmp(i64 a, i64 b) { return a < b ? 8 : a > b ? 4 : 2; }
__attribute__((noinline)) static unsigned c_cmpu(u64 a, u64 b) { return a < b ? 8 : a > b ? 4 : 2; }

static const char* name(unsigned crf) {
    crf &= 14;   /* without SO */
    return crf == 8 ? "LT" : crf == 4 ? "GT" : crf == 2 ? "EQ" : "??";
}

/* Writes one line per case; returns how many of the rewritten C compares were wrong. */
int ch360_probe64(void (*log)(const char* line)) {
    struct { const char* what; unsigned raw, c, want; } cases[5];
    u64 two32 = g_vals[0], one = g_vals[1], two31 = g_vals[2], low_ones = g_vals[3], two32_5 = g_vals[4];
    char line[160];
    int wrong = 0, low_only = 0, i;
    cases[0].what = "cmpd 2^32 : 1";
    cases[0].raw = ch360_raw_cmpd(two32, one); cases[0].c = c_cmp((i64)two32, (i64)one); cases[0].want = 4;
    cases[1].what = "cmpd 2^31 : 0";
    cases[1].raw = ch360_raw_cmpd(two31, 0); cases[1].c = c_cmp((i64)two31, 0); cases[1].want = 4;
    cases[2].what = "cmpld 2^32 : 2^32-1";
    cases[2].raw = ch360_raw_cmpld(two32, low_ones); cases[2].c = c_cmpu(two32, low_ones); cases[2].want = 4;
    cases[3].what = "cmpd 2^32+5 : 5";
    cases[3].raw = ch360_raw_cmpd(two32_5, 5); cases[3].c = c_cmp((i64)two32_5, 5); cases[3].want = 4;
    cases[4].what = "cmpdi 2^32 : 0";
    cases[4].raw = ch360_raw_cmpdi0(two32); cases[4].c = c_cmp((i64)two32, 0); cases[4].want = 4;
    for (i = 0; i < 5; ++i) {
        if ((cases[i].raw & 14) != cases[i].want) ++low_only;
        if ((cases[i].c & 14) != cases[i].want) ++wrong;
        snprintf(line, sizeof(line), "cpu probe: %s: instruction %s, rewritten %s (want %s)", cases[i].what,
                 name(cases[i].raw), name(cases[i].c), name(cases[i].want));
        log(line);
    }
    snprintf(line, sizeof(line), "cpu probe: cntlzd 1 = %u (64-bit: 63); %d of 5 doubleword compares wrong, %d of 5 rewritten wrong",
             ch360_raw_cntlzd(one), low_only, wrong);
    log(line);
    return wrong;
}
