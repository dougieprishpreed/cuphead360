/* The translated player's "ch360" imports (managed ConsoleHost.cs) and its
 * start-up under the wasm runtime (Spire360's srt, WASI preview1). C89,
 * because the translated module's header is C. Guest pointers become host
 * pointers through srt_mem_ptr; multi-byte values in guest memory are
 * little-endian, which the platform layer reads with ch360_u32/ch360_f32. */
#include "w2c2_base.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "srt.h"
#include "srt_plat.h"
#include "player.h"
#include "ch360_host.h"

/* srt_internal.h's guest-memory helpers */
int srt_mem_ok(U32 ptr, U32 len);
U8* srt_mem_ptr(U32 ptr);

static m6_playerInstance instance;

/* The player's stdout: drained into the log each frame (and at the end), and
 * by the console's log thread during long frames (the pipe has a lock). */
static void drain_stdout(void) {
    char buf[4096];
    size_t n;
    while ((n = srt_stdout_read(buf, sizeof(buf), 0)) > 0) ch360_plat_log(buf, n);
}

void ch360_drain_output(void) {
    drain_stdout();
}

int ch360_frame_drain = 1;

static void stderr_sink(const char* text, size_t len, void* user) {
    (void)user;
    ch360_plat_log(text, len);
}

U32 i5_ch36011_frameX5Fbegin(wasmModuleInstance* inst) {
    (void)inst;
    if (ch360_frame_drain) drain_stdout();
    return (U32)ch360_plat_frame_begin();
}

U32 i5_ch3603_pad(wasmModuleInstance* inst, U32 index, U32 state) {
    ch360_pad p;
    U8* s;
    (void)inst;
    if (!srt_mem_ok(state, 16)) return 0;
    memset(&p, 0, sizeof(p));
    if (!ch360_plat_pad((int)index, &p)) return 0;
    s = srt_mem_ptr(state);
    s[0] = (U8)p.buttons; s[1] = (U8)(p.buttons >> 8);
    s[2] = (U8)p.lx; s[3] = (U8)((unsigned short)p.lx >> 8);
    s[4] = (U8)p.ly; s[5] = (U8)((unsigned short)p.ly >> 8);
    s[6] = (U8)p.rx; s[7] = (U8)((unsigned short)p.rx >> 8);
    s[8] = (U8)p.ry; s[9] = (U8)((unsigned short)p.ry >> 8);
    s[10] = p.lt; s[11] = p.rt;
    return 1;
}

void i5_ch3606_submit(wasmModuleInstance* inst, U32 data, U32 len) {
    (void)inst;
    if (srt_mem_ok(data, len)) ch360_plat_submit(srt_mem_ptr(data), (unsigned)len);
}

/* ---- the draw list, written here from ConsoleHost's views and items ----
 * draw_view(view): 9 slots, f32 x y w h, u32 clear, f32 r g b a (CHF1's view
 * header without its item count); draw_item(args): 26 slots (ConsoleHost.cs,
 * HostItem): i32 texture, u32 mode, f32 blend r g b a, u32 flags (1 vertex
 * colours as they are, 2 positions through the affine map, 4 clipped),
 * f32 alpha scale, then (address, count) of the triangles (i32), positions
 * (f32 x y), uvs (f32 u v) and colours (u8 r g b a), f32 A00 A01 B0 A10 A11
 * B1, f32 clip x0 y0 x1 y1; draw_end() submits. The CHF1 list this writes
 * is what ConsoleHost wrote itself before (CH360_DRAW=managed still does). */

static U8* draw_buf;
static unsigned draw_len, draw_cap, draw_views, draw_view_at, draw_items;

static int draw_room(unsigned more) {
    unsigned cap;
    U8* b;
    if (draw_len + more <= draw_cap) return 1;
    cap = draw_cap ? draw_cap : (1u << 20);
    while (cap < draw_len + more) cap *= 2;
    b = (U8*)realloc(draw_buf, cap);
    if (b == NULL) return 0;
    draw_buf = b;
    draw_cap = cap;
    return 1;
}

static U32 le32(const U8* p) {
    return (U32)p[0] | ((U32)p[1] << 8) | ((U32)p[2] << 16) | ((U32)p[3] << 24);
}
static float lef(const U8* p) {
    union { U32 u; float f; } v;
    v.u = le32(p);
    return v.f;
}
static void put32(U8* p, U32 v) {
    p[0] = (U8)v; p[1] = (U8)(v >> 8); p[2] = (U8)(v >> 16); p[3] = (U8)(v >> 24);
}
static void putf(U8* p, float f) {
    union { U32 u; float f; } v;
    v.f = f;
    put32(p, v.u);
}

static void draw_close_view(void) {
    if (draw_views > 0) put32(draw_buf + draw_view_at, draw_items);
}

void i5_ch3609_drawX5Fview(wasmModuleInstance* inst, U32 view) {
    (void)inst;
    if (!srt_mem_ok(view, 36)) return;
    if (draw_len == 0) {
        if (!draw_room(8)) return;
        put32(draw_buf, 0x31464843u); /* 'CHF1' */
        put32(draw_buf + 4, 0);
        draw_len = 8;
        draw_views = 0;
    }
    if (!draw_room(40)) return;
    draw_close_view();
    memcpy(draw_buf + draw_len, srt_mem_ptr(view), 36);
    draw_view_at = draw_len + 36;
    put32(draw_buf + draw_view_at, 0);
    draw_len += 40;
    draw_views++;
    draw_items = 0;
}

typedef struct { float x, y, u, v, c[4]; } draw_vertex;

static void draw_put(const draw_vertex* v) {
    U8* p = draw_buf + draw_len;
    putf(p, v->x); putf(p + 4, v->y); putf(p + 8, v->u); putf(p + 12, v->v);
    p[16] = (U8)v->c[0]; p[17] = (U8)v->c[1]; p[18] = (U8)v->c[2]; p[19] = (U8)v->c[3];
    draw_len += 20;
}

/* One edge of the clip rectangle: keep where side * (coordinate - at) >= 0. */
static int draw_clip_edge(const draw_vertex* in, int n, draw_vertex* out, int axis, float at, float side) {
    int i, m = 0;
    for (i = 0; i < n; i++) {
        const draw_vertex* a = &in[i];
        const draw_vertex* b = &in[(i + 1) % n];
        float da = side * ((axis ? a->y : a->x) - at), db = side * ((axis ? b->y : b->x) - at);
        if (da >= 0) out[m++] = *a;
        if ((da >= 0) != (db >= 0)) {
            float t = da / (da - db);
            int k;
            draw_vertex* o = &out[m++];
            o->x = a->x + (b->x - a->x) * t;
            o->y = a->y + (b->y - a->y) * t;
            o->u = a->u + (b->u - a->u) * t;
            o->v = a->v + (b->v - a->v) * t;
            for (k = 0; k < 4; k++) o->c[k] = (float)(int)(a->c[k] + (b->c[k] - a->c[k]) * t + 0.5f);
        }
    }
    return m;
}

/* An item's corner i: position (through the affine map for sprites), uv, and
 * the colour ConsoleHost wrote (vertex colour times tint, or for the blend
 * modes the vertex colour with its alpha scaled). */
typedef struct {
    const U8 *tri, *pos, *uv, *col;
    U32 ntri, npos, nuv, ncol;
    int blend, affine;
    float tint[4], alpha, m[6];
} draw_item_in;

static void draw_corner(const draw_item_in* d, U32 i, draw_vertex* v) {
    static const U8 white[4] = { 255, 255, 255, 255 };
    int ki = (int)le32(d->tri + 4 * i), k;
    const U8* c;
    float x, y;
    if (ki < 0 || (U32)ki >= d->npos) ki = 0;
    x = lef(d->pos + 8 * ki);
    y = lef(d->pos + 8 * ki + 4);
    if (d->affine) {
        v->x = d->m[0] * x + d->m[1] * y + d->m[2];
        v->y = d->m[3] * x + d->m[4] * y + d->m[5];
    } else {
        v->x = x;
        v->y = y;
    }
    v->u = (U32)ki < d->nuv ? lef(d->uv + 8 * ki) : 0.0f;
    v->v = (U32)ki < d->nuv ? lef(d->uv + 8 * ki + 4) : 0.0f;
    c = (U32)ki < d->ncol ? d->col + 4 * ki : white;
    if (d->blend) {
        v->c[0] = c[0];
        v->c[1] = c[1];
        v->c[2] = c[2];
        v->c[3] = (float)(U8)(c[3] * d->alpha);
    } else {
        for (k = 0; k < 4; k++) {
            float f = c[k] * d->tint[k];
            v->c[k] = (float)(U8)(f < 0 ? 0 : f > 255 ? 255 : f);
        }
    }
}

void i5_ch3609_drawX5Fitem(wasmModuleInstance* inst, U32 args) {
    const U8* a;
    draw_item_in d;
    U32 flags, i, start;
    float clip[4];
    int k, clipped;
    (void)inst;
    if (draw_views == 0 || !srt_mem_ok(args, 26 * 4)) return;
    a = srt_mem_ptr(args);
    flags = le32(a + 24);
    d.ntri = le32(a + 36);
    d.npos = le32(a + 44);
    d.nuv = le32(a + 52);
    d.ncol = le32(a + 60);
    if (d.ntri == 0 || d.npos == 0 || d.ntri > (1u << 24) || d.npos > (1u << 24) || d.nuv > (1u << 24) ||
        d.ncol > (1u << 24))
        return;
    if (!srt_mem_ok(le32(a + 32), d.ntri * 4) || !srt_mem_ok(le32(a + 40), d.npos * 8)) return;
    d.tri = srt_mem_ptr(le32(a + 32));
    d.pos = srt_mem_ptr(le32(a + 40));
    d.uv = d.nuv > 0 && srt_mem_ok(le32(a + 48), d.nuv * 8) ? srt_mem_ptr(le32(a + 48)) : NULL;
    if (d.uv == NULL) d.nuv = 0;
    d.col = d.ncol > 0 && srt_mem_ok(le32(a + 56), d.ncol * 4) ? srt_mem_ptr(le32(a + 56)) : NULL;
    if (d.col == NULL) d.ncol = 0;
    d.blend = (flags & 1) != 0;
    d.affine = (flags & 2) != 0;
    clipped = (flags & 4) != 0;
    for (k = 0; k < 4; k++) d.tint[k] = lef(a + 8 + 4 * k);
    d.alpha = lef(a + 28);
    for (k = 0; k < 6; k++) d.m[k] = lef(a + 64 + 4 * k);
    for (k = 0; k < 4; k++) clip[k] = lef(a + 88 + 4 * k);
    if (clipped && (clip[2] <= clip[0] || clip[3] <= clip[1])) return; /* an empty mask hides it */
    /* a clipped triangle is at most a heptagon: five triangles */
    if (!draw_room(28 + (clipped ? d.ntri / 3 * 15 : d.ntri) * 20)) return;
    start = draw_len;
    memcpy(draw_buf + draw_len, a, 24); /* texture, mode, blend colour */
    draw_len += 28;
    if (!clipped) {
        for (i = 0; i < d.ntri; i++) {
            draw_vertex v;
            draw_corner(&d, i, &v);
            draw_put(&v);
        }
    } else {
        /* Sutherland-Hodgman against the mask's rectangle, then a fan. */
        for (i = 0; i + 2 < d.ntri; i += 3) {
            draw_vertex poly[2][8];
            int j, n;
            for (j = 0; j < 3; j++) draw_corner(&d, i + j, &poly[1][j]);
            n = draw_clip_edge(poly[1], 3, poly[0], 0, clip[0], 1.0f);
            n = draw_clip_edge(poly[0], n, poly[1], 0, clip[2], -1.0f);
            n = draw_clip_edge(poly[1], n, poly[0], 1, clip[1], 1.0f);
            n = draw_clip_edge(poly[0], n, poly[1], 1, clip[3], -1.0f);
            for (j = 1; j + 1 < n; j++) {
                draw_put(&poly[1][0]);
                draw_put(&poly[1][j]);
                draw_put(&poly[1][j + 1]);
            }
        }
    }
    if (draw_len == start + 28) {
        draw_len = start; /* clipped away */
        return;
    }
    put32(draw_buf + start + 24, (draw_len - start - 28) / 20);
    draw_items++;
}

void i5_ch3608_drawX5Fend(wasmModuleInstance* inst) {
    (void)inst;
    if (draw_len == 0) {
        if (!draw_room(8)) return;
        put32(draw_buf, 0x31464843u);
        put32(draw_buf + 4, 0);
        draw_len = 8;
        draw_views = 0;
    }
    draw_close_view();
    put32(draw_buf + 4, draw_views);
    ch360_plat_submit(draw_buf, draw_len);
    draw_len = 0;
    draw_views = 0;
}

void i5_ch36012_textureX5Ffile(wasmModuleInstance* inst, U32 id, U32 key, U32 len) {
    char name[512];
    (void)inst;
    if (len >= sizeof(name) || !srt_mem_ok(key, len)) return;
    memcpy(name, srt_mem_ptr(key), len);
    name[len] = 0;
    ch360_plat_texture_file((int)id, name);
}

void i5_ch36014_textureX5Fpixels(wasmModuleInstance* inst, U32 id, U32 w, U32 h, U32 bgra) {
    (void)inst;
    if (w == 0 || h == 0 || w > 8192 || h > 8192 || !srt_mem_ok(bgra, w * h * 4)) return;
    ch360_plat_texture_pixels((int)id, (int)w, (int)h, srt_mem_ptr(bgra));
}

void i5_ch3605_audio(wasmModuleInstance* inst, U32 samples, U32 frames) {
    (void)inst;
    if (frames == 0 || frames > 48000 || !srt_mem_ok(samples, frames * 4)) return;
    ch360_plat_audio(srt_mem_ptr(samples), (int)frames);
}

static void run_start(void* p) {
    m6_playerExport6_X5Fstart((m6_playerInstance*)p);
}

int ch360_staged_names = 0;

void ch360_staged_name(const char* key, size_t len, char* out) {
    static const char hex[] = "0123456789ABCDEF";
    unsigned hi = 0xcbf29ce4u, lo = 0x84222325u; /* FNV-1a 64 as two words (C89) */
    size_t i;
    int k;
    for (i = 0; i < len; i++) {
        unsigned a, b, c, d, t;
        lo ^= (unsigned char)key[i];
        /* times 0x100000001b3: lo * 0x1b3 with carry, plus lo << 40 */
        a = (lo & 0xffffu) * 0x1b3u;
        b = (lo >> 16) * 0x1b3u + (a >> 16);
        c = hi * 0x1b3u + (b >> 16);
        d = c + (lo << 8);
        t = (a & 0xffffu) | (b << 16);
        lo = t;
        hi = d;
    }
    out[0] = hex[(hi >> 28) & 15];
    out[1] = hex[(hi >> 24) & 15];
    out[2] = '/';
    for (k = 0; k < 8; k++) out[3 + k] = hex[(lo >> (28 - 4 * k)) & 15];
    out[11] = 0;
}

int ch360_prepare(const char* root_dir, int argc, const char* const* argv, unsigned memory_mb) {
    /* The GC keeps its own small gen0 budget (frequent, cheap gen0 GCs) and
     * conserve-memory mode, so the linear memory, which never shrinks, stays
     * close to the live heap. An 8 MB gen0 budget (DOTNET_GCgen0size=800000;
     * hex, no 0x) made every GC a gen1 and half of them gen2: 16.4 s of pauses
     * over the native bot run against 3.4 s (docs/VERIFICATION.md, round 10). */
    static const char* const env[3] = { "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1", "DOTNET_gcServer=0", "DOTNET_GCConserveMemory=7" };
    srt_config cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.root_dir = root_dir;
    ch360_ogg_root(root_dir);
    cfg.argc = argc;
    cfg.argv = argv;
    cfg.envc = 3;
    cfg.envp = env;
#ifndef __XSDK__
    {
        /* PC checks: CH360_GUEST_ENV="A=1 B=2" replaces the GC settings above
         * (the first two entries stay), for trying budgets without a rebuild. */
        static char buf[512];
        static const char* list[16];
        const char* over = getenv("CH360_GUEST_ENV");
        if (over != NULL && strlen(over) < sizeof(buf)) {
            int n = 2;
            char* tok;
            strcpy(buf, over);
            list[0] = env[0];
            list[1] = env[1];
            for (tok = strtok(buf, " "); tok != NULL && n < 16; tok = strtok(NULL, " ")) list[n++] = tok;
            cfg.envc = n;
            cfg.envp = list;
        }
    }
#endif
    cfg.memory_limit = (srt_u64)memory_mb * 1024 * 1024;
    cfg.stderr_sink = stderr_sink;
    if (srt_init(&cfg) != 0) {
        static const char msg[] = "ch360: wasm runtime set-up failed\n";
        ch360_plat_log(msg, sizeof(msg) - 1);
        return 2;
    }
    m6_playerInstantiate(&instance, NULL);
    srt_set_memory(m6_playerExport6_memory(&instance));
    return 0;
}

int ch360_start(unsigned stack_mb, int hw_thread) {
    int code;
    /* The game loop is the guest's main, on a thread of its own with a
     * large native stack (the translated code recurses deeply); it calls
     * the imports above each frame, so the platform layer's renderer and
     * pads are used from that one thread. */
    if (srt_guest_start(run_start, &instance, (size_t)stack_mb * 1024 * 1024, hw_thread) != 0) {
        static const char msg[] = "ch360: guest thread failed\n";
        ch360_plat_log(msg, sizeof(msg) - 1);
        return 3;
    }
    code = srt_guest_join();
    drain_stdout();
    return code;
}

int ch360_run(const char* root_dir, int argc, const char* const* argv, unsigned memory_mb, unsigned stack_mb,
              int hw_thread) {
    int rc = ch360_prepare(root_dir, argc, argv, memory_mb);
    return rc != 0 ? rc : ch360_start(stack_mb, hw_thread);
}

unsigned ch360_memory_kb(void) {
    wasmMemory* m = m6_playerExport6_memory(&instance);
    return m ? (unsigned)(m->size / 1024) : 0;
}
