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

/* ---- the draw list, written here from ConsoleHost's views and items ----
 * draw_view(view): 9 slots, f32 x y w h, u32 clear, f32 r g b a; draw_item
 * (args): 26 slots (ConsoleHost.cs, HostItem): i32 texture, u32 mode, f32
 * blend r g b a, u32 flags (1 vertex colours as they are, 2 positions
 * through the affine map, 4 clipped), f32 alpha scale, then (address, count)
 * of the triangles (i32), positions (f32 x y), uvs (f32 u v) and colours (u8
 * r g b a), f32 A00 A01 B0 A10 A11 B1, f32 clip x0 y0 x1 y1, i32 two more
 * textures, f32 three floats (mode 6, the light ray); draw_end() hands the
 * frame to the platform layer (ch360_plat_draw).
 *
 * The vertices are written in the host's byte order and the GPU's layout
 * (ch360_vertex) so the host copies them as they are. On the console's CPU
 * every move between its integer and floating-point registers goes through
 * memory and stalls (load-hit-store), so uvs and colours are copied as
 * words and only a sprite's position (through its map) is arithmetic. */

static ch360_view* draw_views;
static ch360_item* draw_items;
static ch360_vertex* draw_verts;
static unsigned draw_view_count, draw_view_cap, draw_item_count, draw_item_cap, draw_vert_count, draw_vert_cap;
static double draw_build_ms;

static double draw_now_ms(void) {
    return (double)srt_plat_time_monotonic_ns() / 1e6;
}

static int draw_grow(void** p, unsigned* cap, unsigned need, size_t size) {
    unsigned c;
    void* b;
    if (need <= *cap) return 1;
    c = *cap ? *cap : 1024;
    while (c < need) c *= 2;
    b = realloc(*p, (size_t)c * size);
    if (b == NULL) return 0;
    *p = b;
    *cap = c;
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
static void set_bits(float* f, U32 bits) {
    memcpy(f, &bits, 4);
}
static U8 unorm8(float f) {
    return (U8)(f <= 0.0f ? 0 : f >= 1.0f ? 255 : (int)(f * 255.0f + 0.5f));
}

static void draw_view_add(const float* xywh, unsigned clear, float r, float g, float b) {
    ch360_view* v;
    if (!draw_grow((void**)&draw_views, &draw_view_cap, draw_view_count + 1, sizeof(ch360_view))) return;
    v = &draw_views[draw_view_count++];
    v->x = xywh[0];
    v->y = xywh[1];
    v->w = xywh[2];
    v->h = xywh[3];
    v->clear = clear;
    v->r = r;
    v->g = g;
    v->b = b;
    v->first_item = draw_item_count;
    v->items = 0;
}

/* The item's screen box, from the vertices just written (cached memory, no swaps). */
static void draw_item_box(ch360_item* it) {
    const ch360_vertex* v = draw_verts + it->first;
    float x0 = v->x, y0 = v->y, x1 = x0, y1 = y0;
    unsigned k;
    for (k = 1; k < it->count; k++) {
        float x = v[k].x, y = v[k].y;
        if (x < x0) x0 = x;
        if (x > x1) x1 = x;
        if (y < y0) y0 = y;
        if (y > y1) y1 = y;
    }
    it->x0 = x0;
    it->y0 = y0;
    it->x1 = x1;
    it->y1 = y1;
}

/* ---- textures: the platform's ids, and sprites at full size ----
 * The glue numbers the textures it hands the platform: the player's texture
 * ids as they come, then each full-size sprite page the first time an item
 * is moved onto it.
 *
 * The game's atlas pages are 4096 px, and at 720p their sprites are drawn at
 * 1 to 1.45 screen pixels a texel, so pages halved to fit the console's
 * memory (16 MB each at full size in DXT5) blurred them: the Root Pack's at
 * 2.7 pixels a texel. A scene draws a fraction of its pages (18 MB of the
 * 208 MB of pages the potato's fight touched), so tools/cluster_console.py
 * copies each sprite's rectangle at full size onto pages of its own
 * animation, and its table (ctexmap, in the texture pack) says where each
 * went. An item drawn from an atlas page moves onto the sprite's page: the
 * rectangle that holds its uv box, its uvs through that rectangle's scale and
 * offset. Light rays (mode 6) keep the page's own uvs (their shader reads two
 * more textures at them) and the halved page. */
typedef struct remap_page {
    unsigned hi, lo, first, count, last;
    /* REMAP_GRID x REMAP_GRID cells over the page's uv: the boxes each cell
     * meets, built on the page's first item. A scan of all its boxes per item
     * was 34 ms a frame on the console in the first run-and-gun level. */
    unsigned* grid;
} remap_page;
#define REMAP_GRID 8
typedef struct remap_box {
    float u0, v0, u1, v1, su, ou, sv, ov;
    unsigned target;
} remap_box;

int ch360_ctex_pack = -1;
static remap_page* rm_pages;
static remap_box* rm_boxes;
static unsigned* rm_names;
static char* rm_strings;
static int* rm_plat;
static unsigned rm_page_count, rm_box_count, rm_target_count, rm_misses;
static int* plat_of_player;   /* the player's texture id -> the platform's */
static int* page_of_plat;     /* the platform's id -> remap page + 1, 0 none */
static unsigned player_cap, plat_cap;
static int next_plat;

static int grow_ints(int** p, unsigned* cap, unsigned need, int fill) {
    unsigned c = *cap, i;
    int* b;
    if (need <= c) return 1;
    if (c == 0) c = 256;
    while (c < need) c *= 2;
    b = (int*)realloc(*p, c * sizeof(int));
    if (b == NULL) return 0;
    for (i = *cap; i < c; i++) b[i] = fill;
    *p = b;
    *cap = c;
    return 1;
}

static int new_plat(void) {
    if (!grow_ints(&page_of_plat, &plat_cap, (unsigned)next_plat + 1, 0)) return -1;
    page_of_plat[next_plat] = 0;
    return next_plat++;
}

static int plat_id(int player_id) {
    if (player_id < 0 || !grow_ints(&plat_of_player, &player_cap, (unsigned)player_id + 1, -1)) return -1;
    if (plat_of_player[player_id] < 0) plat_of_player[player_id] = new_plat();
    return plat_of_player[player_id];
}

/* The remap table: 'CHRM' u32 version (1) pages boxes targets string-bytes;
 * pages (u32 hash_hi hash_lo first count, by hash), boxes (f32 u0 v0 u1 v1
 * su ou sv ov, u32 target), targets (u32 offset of the page's key), keys. */
static void remap_load(void) {
    ch360_pack_entry e;
    unsigned char* d;
    unsigned i, need, sb;
    if (ch360_ctex_pack < 0 || !ch360_pack_find(ch360_ctex_pack, "ctexmap", 7, &e) || e.size < 24) return;
    d = (unsigned char*)malloc(e.size);
    if (d == NULL || ch360_pack_read(&e, 0, d, e.size) != 0 || le32(d) != 0x4d524843u || le32(d + 4) != 1) {
        free(d);
        return;
    }
    rm_page_count = le32(d + 8);
    rm_box_count = le32(d + 12);
    rm_target_count = le32(d + 16);
    sb = le32(d + 20);
    need = 24 + rm_page_count * 16 + rm_box_count * 36 + rm_target_count * 4 + sb;
    rm_pages = (remap_page*)malloc((rm_page_count + 1) * sizeof(remap_page));
    rm_boxes = (remap_box*)malloc((rm_box_count + 1) * sizeof(remap_box));
    rm_names = (unsigned*)malloc((rm_target_count + 1) * sizeof(unsigned));
    rm_plat = (int*)malloc((rm_target_count + 1) * sizeof(int));
    rm_strings = (char*)malloc(sb + 1);
    if (need > e.size || !rm_pages || !rm_boxes || !rm_names || !rm_plat || !rm_strings) {
        rm_page_count = rm_box_count = rm_target_count = 0;
        free(d);
        return;
    }
    for (i = 0; i < rm_page_count; i++) {
        const U8* p = d + 24 + i * 16;
        rm_pages[i].hi = le32(p);
        rm_pages[i].lo = le32(p + 4);
        rm_pages[i].first = le32(p + 8);
        rm_pages[i].count = le32(p + 12);
        rm_pages[i].last = 0;
        rm_pages[i].grid = NULL;
    }
    for (i = 0; i < rm_box_count; i++) {
        const U8* p = d + 24 + rm_page_count * 16 + i * 36;
        rm_boxes[i].u0 = lef(p);
        rm_boxes[i].v0 = lef(p + 4);
        rm_boxes[i].u1 = lef(p + 8);
        rm_boxes[i].v1 = lef(p + 12);
        rm_boxes[i].su = lef(p + 16);
        rm_boxes[i].ou = lef(p + 20);
        rm_boxes[i].sv = lef(p + 24);
        rm_boxes[i].ov = lef(p + 28);
        rm_boxes[i].target = le32(p + 32);
    }
    for (i = 0; i < rm_target_count; i++) {
        rm_names[i] = le32(d + 24 + rm_page_count * 16 + rm_box_count * 36 + i * 4);
        rm_plat[i] = -1;
    }
    memcpy(rm_strings, d + need - sb, sb);
    rm_strings[sb] = 0;
    free(d);
}

static int remap_page_of(const char* key, size_t len) {
    unsigned hi, lo, a = 0, b = rm_page_count;
    if (rm_page_count == 0) return -1;
    ch360_fnv64(key, len, &hi, &lo);
    while (a < b) {
        unsigned m = a + (b - a) / 2;
        if (rm_pages[m].hi < hi || (rm_pages[m].hi == hi && rm_pages[m].lo < lo)) a = m + 1;
        else b = m;
    }
    return a < rm_page_count && rm_pages[a].hi == hi && rm_pages[a].lo == lo ? (int)a : -1;
}

static int remap_cell(float u) {
    int c = (int)(u * (float)REMAP_GRID);
    return c < 0 ? 0 : c >= REMAP_GRID ? REMAP_GRID - 1 : c;
}

/* The page's grid: REMAP_GRID^2 + 1 starts (into the array itself), then the
 * page's box numbers by cell. NULL if there is no memory (a full scan then). */
static unsigned* remap_grid(const remap_page* p) {
    unsigned cursor[REMAP_GRID * REMAP_GRID + 1], cells = REMAP_GRID * REMAP_GRID, total = 0, k, c;
    unsigned* g;
    memset(cursor, 0, sizeof(cursor));
    for (k = 0; k < p->count; k++) {
        const remap_box* b = &rm_boxes[p->first + k];
        int x, y, x0 = remap_cell(b->u0), x1 = remap_cell(b->u1), y0 = remap_cell(b->v0), y1 = remap_cell(b->v1);
        for (y = y0; y <= y1; y++)
            for (x = x0; x <= x1; x++) cursor[y * REMAP_GRID + x]++;
    }
    for (c = 0; c < cells; c++) total += cursor[c];
    g = (unsigned*)malloc((cells + 1 + total) * sizeof(unsigned));
    if (g == NULL) return NULL;
    g[0] = cells + 1;
    for (c = 0; c < cells; c++) {
        g[c + 1] = g[c] + cursor[c];
        cursor[c] = g[c];
    }
    for (k = 0; k < p->count; k++) {
        const remap_box* b = &rm_boxes[p->first + k];
        int x, y, x0 = remap_cell(b->u0), x1 = remap_cell(b->u1), y0 = remap_cell(b->v0), y1 = remap_cell(b->v1);
        for (y = y0; y <= y1; y++)
            for (x = x0; x <= x1; x++) g[cursor[y * REMAP_GRID + x]++] = k;
    }
    return g;
}

/* Moves an item drawn from an atlas page onto its sprite's full-size page. */
static void remap_item(ch360_item* it) {
    remap_page* p;
    ch360_vertex* v;
    float u0, v0, u1, v1;
    unsigned k, n, from, to;
    int page;
    if (it->texture < 0 || (unsigned)it->texture >= plat_cap || it->mode == 6) return;
    page = page_of_plat[it->texture] - 1;
    if (page < 0) return;
    p = &rm_pages[page];
    if (p->count == 0) return;
    v = draw_verts + it->first;
    u0 = u1 = v->u;
    v0 = v1 = v->v;
    for (k = 1; k < it->count; k++) {
        if (v[k].u < u0) u0 = v[k].u;
        if (v[k].u > u1) u1 = v[k].u;
        if (v[k].v < v0) v0 = v[k].v;
        if (v[k].v > v1) v1 = v[k].v;
    }
    if (p->grid == NULL) p->grid = remap_grid(p);
    /* the box last matched, then those of the cell holding the uv box's corner
     * (a box that holds the uv box holds that corner), or all without a grid */
    if (p->grid != NULL) {
        unsigned c = (unsigned)(remap_cell(v0) * REMAP_GRID + remap_cell(u0));
        from = p->grid[c];
        to = p->grid[c + 1];
    } else {
        from = 0;
        to = p->count;
    }
    for (n = 0; n <= to - from; n++) {
        unsigned bi;
        const remap_box* b;
        if (n == 0) bi = p->first + p->last;
        else bi = p->first + (p->grid != NULL ? p->grid[from + n - 1] : from + n - 1);
        b = &rm_boxes[bi];
        if (u0 >= b->u0 && u1 <= b->u1 && v0 >= b->v0 && v1 <= b->v1) {
            int id = b->target < rm_target_count ? rm_plat[b->target] : -1;
            if (id < 0 && b->target < rm_target_count) {
                id = rm_plat[b->target] = new_plat();
                if (id >= 0) ch360_plat_texture_file(id, rm_strings + rm_names[b->target]);
            }
            if (id < 0) return;
            for (k = 0; k < it->count; k++) {
                v[k].u = v[k].u * b->su + b->ou;
                v[k].v = v[k].v * b->sv + b->ov;
            }
            it->texture = id;
            p->last = bi - p->first;
            return;
        }
    }
    rm_misses++;
}

unsigned ch360_remap_misses(void) {
    return rm_misses;
}

unsigned ch360_remap_counts(unsigned* boxes, unsigned* targets) {
    *boxes = rm_box_count;
    *targets = rm_target_count;
    return rm_page_count;
}

static void draw_item_done(ch360_item* it) {
    it->count = draw_vert_count - it->first;
    if (it->count == 0) {
        draw_item_count--; /* clipped away */
        return;
    }
    remap_item(it);
    draw_item_box(it);
    draw_views[draw_view_count - 1].items++;
}

void i5_ch3609_drawX5Fview(wasmModuleInstance* inst, U32 view) {
    const U8* p;
    float xywh[4];
    (void)inst;
    if (!srt_mem_ok(view, 36)) return;
    p = srt_mem_ptr(view);
    xywh[0] = lef(p);
    xywh[1] = lef(p + 4);
    xywh[2] = lef(p + 8);
    xywh[3] = lef(p + 12);
    draw_view_add(xywh, le32(p + 16), lef(p + 20), lef(p + 24), lef(p + 28));
}

/* A corner as floats, for clipping: position, uv, colour and blend 0-255. */
typedef struct { float x, y, u, v, c[4]; } draw_corner_f;

static int draw_clip_edge(const draw_corner_f* in, int n, draw_corner_f* out, int axis, float at, float side) {
    int i, m = 0;
    for (i = 0; i < n; i++) {
        const draw_corner_f* a = &in[i];
        const draw_corner_f* b = &in[(i + 1) % n];
        float da = side * ((axis ? a->y : a->x) - at), db = side * ((axis ? b->y : b->x) - at);
        if (da >= 0) out[m++] = *a;
        if ((da >= 0) != (db >= 0)) {
            float t = da / (da - db);
            int k;
            draw_corner_f* o = &out[m++];
            o->x = a->x + (b->x - a->x) * t;
            o->y = a->y + (b->y - a->y) * t;
            o->u = a->u + (b->u - a->u) * t;
            o->v = a->v + (b->v - a->v) * t;
            for (k = 0; k < 4; k++) o->c[k] = a->c[k] + (b->c[k] - a->c[k]) * t;
        }
    }
    return m;
}

void i5_ch3609_drawX5Fitem(wasmModuleInstance* inst, U32 args) {
    static const U8 white[4] = { 255, 255, 255, 255 };
    const U8 *a, *tri, *pos, *uv = NULL, *col = NULL;
    U32 flags, ntri, npos, nuv, ncol, i;
    float m[6], clip[4], tint[4];
    U8 blend[4];
    int k, mode, affine, clipped;
    ch360_item* it;
    double t0 = draw_now_ms();
    (void)inst;
    if (draw_view_count == 0 || !srt_mem_ok(args, 31 * 4)) return;
    a = srt_mem_ptr(args);
    flags = le32(a + 24);
    ntri = le32(a + 36);
    npos = le32(a + 44);
    nuv = le32(a + 52);
    ncol = le32(a + 60);
    if (ntri == 0 || npos == 0 || ntri > (1u << 24) || npos > (1u << 24) || nuv > (1u << 24) || ncol > (1u << 24)) return;
    if (!srt_mem_ok(le32(a + 32), ntri * 4) || !srt_mem_ok(le32(a + 40), npos * 8)) return;
    tri = srt_mem_ptr(le32(a + 32));
    pos = srt_mem_ptr(le32(a + 40));
    if (nuv > 0 && srt_mem_ok(le32(a + 48), nuv * 8)) uv = srt_mem_ptr(le32(a + 48));
    else nuv = 0;
    if (ncol > 0 && srt_mem_ok(le32(a + 56), ncol * 4)) col = srt_mem_ptr(le32(a + 56));
    else ncol = 0;
    mode = (int)le32(a + 4);
    affine = (flags & 2) != 0;
    clipped = (flags & 4) != 0;
    for (k = 0; k < 6; k++) m[k] = lef(a + 64 + 4 * k);
    for (k = 0; k < 4; k++) clip[k] = lef(a + 88 + 4 * k);
    for (k = 0; k < 4; k++) tint[k] = lef(a + 8 + 4 * k);
    if (clipped && (clip[2] <= clip[0] || clip[3] <= clip[1])) return; /* an empty mask hides it */
    /* The renderer colour: the shaders multiply it in (modes 0, 5), or take
     * its rgb as the blend colour and the alpha scale as its alpha (1-3). */
    blend[0] = unorm8(tint[0]);
    blend[1] = unorm8(tint[1]);
    blend[2] = unorm8(tint[2]);
    blend[3] = unorm8((flags & 1) ? lef(a + 28) : tint[3]);
    /* a clipped triangle is at most a heptagon: five triangles */
    if (!draw_grow((void**)&draw_verts, &draw_vert_cap, draw_vert_count + (clipped ? ntri / 3 * 15 : ntri), sizeof(ch360_vertex)) ||
        !draw_grow((void**)&draw_items, &draw_item_cap, draw_item_count + 1, sizeof(ch360_item)))
        return;
    it = &draw_items[draw_item_count++];
    it->texture = plat_id((int)le32(a));
    it->mode = (unsigned)mode;
    it->first = draw_vert_count;
    it->textures[0] = plat_id((int)le32(a + 104));
    it->textures[1] = plat_id((int)le32(a + 108));
    it->params[0] = lef(a + 112);
    it->params[1] = lef(a + 116);
    it->params[2] = lef(a + 120);
    it->params[3] = 0.0f;
    if (!clipped) {
        ch360_vertex* out = draw_verts + draw_vert_count;
        for (i = 0; i < ntri; i++, out++) {
            int ki = (int)le32(tri + 4 * i);
            const U8* pp;
            if (ki < 0 || (U32)ki >= npos) ki = 0;
            pp = pos + 8 * ki;
            if (affine) {
                float x = lef(pp), y = lef(pp + 4);
                out->x = m[0] * x + m[1] * y + m[2];
                out->y = m[3] * x + m[4] * y + m[5];
            } else {
                set_bits(&out->x, le32(pp));
                set_bits(&out->y, le32(pp + 4));
            }
            if ((U32)ki < nuv) {
                set_bits(&out->u, le32(uv + 8 * ki));
                set_bits(&out->v, le32(uv + 8 * ki + 4));
            } else {
                out->u = out->v = 0.0f;
            }
            memcpy(out->color, (U32)ki < ncol ? col + 4 * ki : white, 4);
            memcpy(out->blend, blend, 4);
        }
        draw_vert_count += ntri;
    } else {
        /* Sutherland-Hodgman against the mask's rectangle, then a fan. */
        for (i = 0; i + 2 < ntri; i += 3) {
            draw_corner_f poly[2][8];
            int j, n;
            for (j = 0; j < 3; j++) {
                draw_corner_f* c = &poly[1][j];
                int ki = (int)le32(tri + 4 * (i + j));
                const U8* cc;
                float x, y;
                if (ki < 0 || (U32)ki >= npos) ki = 0;
                x = lef(pos + 8 * ki);
                y = lef(pos + 8 * ki + 4);
                c->x = affine ? m[0] * x + m[1] * y + m[2] : x;
                c->y = affine ? m[3] * x + m[4] * y + m[5] : y;
                c->u = (U32)ki < nuv ? lef(uv + 8 * ki) : 0.0f;
                c->v = (U32)ki < nuv ? lef(uv + 8 * ki + 4) : 0.0f;
                cc = (U32)ki < ncol ? col + 4 * ki : white;
                for (k = 0; k < 4; k++) c->c[k] = cc[k];
            }
            n = draw_clip_edge(poly[1], 3, poly[0], 0, clip[0], 1.0f);
            n = draw_clip_edge(poly[0], n, poly[1], 0, clip[2], -1.0f);
            n = draw_clip_edge(poly[1], n, poly[0], 1, clip[1], 1.0f);
            n = draw_clip_edge(poly[0], n, poly[1], 1, clip[3], -1.0f);
            for (j = 1; j + 1 < n; j++) {
                const draw_corner_f* tri3[3];
                int q;
                tri3[0] = &poly[1][0];
                tri3[1] = &poly[1][j];
                tri3[2] = &poly[1][j + 1];
                for (q = 0; q < 3; q++) {
                    ch360_vertex* out = &draw_verts[draw_vert_count++];
                    out->x = tri3[q]->x;
                    out->y = tri3[q]->y;
                    out->u = tri3[q]->u;
                    out->v = tri3[q]->v;
                    for (k = 0; k < 4; k++) out->color[k] = (U8)(tri3[q]->c[k] + 0.5f);
                    memcpy(out->blend, blend, 4);
                }
            }
        }
    }
    draw_item_done(it);
    draw_build_ms += draw_now_ms() - t0;
}

void i5_ch3608_drawX5Fend(wasmModuleInstance* inst) {
    ch360_frame f;
    (void)inst;
    f.views = draw_views;
    f.view_count = draw_view_count;
    f.items = draw_items;
    f.item_count = draw_item_count;
    f.vertices = draw_verts;
    f.vertex_count = draw_vert_count;
    f.build_ms = draw_build_ms;
    ch360_plat_draw(&f);
    draw_view_count = draw_item_count = draw_vert_count = 0;
    draw_build_ms = 0;
}

/* The draw list ConsoleHost writes itself (CH360_DRAW=managed, a PC check),
 * read into the same frame: its colours already carry the renderer colour
 * for modes 0 and 5 and their alpha scale for 1-3, so the blend colour is
 * white there and the renderer colour's rgb for the blend modes. */
void i5_ch3606_submit(wasmModuleInstance* inst, U32 data, U32 len) {
    const U8* d;
    unsigned p = 8, v, i, views;
    (void)inst;
    if (!srt_mem_ok(data, len) || len < 8) return;
    d = srt_mem_ptr(data);
    if (le32(d) != 0x31464843u) return;
    views = le32(d + 4);
    for (v = 0; v < views && p + 40 <= len; v++) {
        float xywh[4];
        unsigned items;
        xywh[0] = lef(d + p);
        xywh[1] = lef(d + p + 4);
        xywh[2] = lef(d + p + 8);
        xywh[3] = lef(d + p + 12);
        draw_view_add(xywh, le32(d + p + 16), lef(d + p + 20), lef(d + p + 24), lef(d + p + 28));
        items = le32(d + p + 36);
        p += 40;
        for (i = 0; i < items && p + 28 <= len; i++) {
            unsigned n = le32(d + p + 24), mode = le32(d + p + 4), k;
            U8 blend[4];
            ch360_item* it;
            if (p + 28 + n * 20 > len) return;
            if (mode >= 1 && mode <= 3) {
                blend[0] = unorm8(lef(d + p + 8));
                blend[1] = unorm8(lef(d + p + 12));
                blend[2] = unorm8(lef(d + p + 16));
                blend[3] = 255;
            } else {
                memset(blend, 255, 4);
            }
            if (!draw_grow((void**)&draw_verts, &draw_vert_cap, draw_vert_count + n, sizeof(ch360_vertex)) ||
                !draw_grow((void**)&draw_items, &draw_item_cap, draw_item_count + 1, sizeof(ch360_item)))
                return;
            it = &draw_items[draw_item_count++];
            it->texture = plat_id((int)le32(d + p));
            it->mode = mode;
            it->first = draw_vert_count;
            it->textures[0] = it->textures[1] = -1;
            memset(it->params, 0, sizeof(it->params));
            for (k = 0; k < n; k++) {
                const U8* s = d + p + 28 + k * 20;
                ch360_vertex* out = &draw_verts[draw_vert_count++];
                out->x = lef(s);
                out->y = lef(s + 4);
                out->u = lef(s + 8);
                out->v = lef(s + 12);
                memcpy(out->color, s + 16, 4);
                memcpy(out->blend, blend, 4);
            }
            draw_item_done(it);
            p += 28 + n * 20;
        }
    }
    i5_ch3608_drawX5Fend(inst);
}

void i5_ch36012_textureX5Ffile(wasmModuleInstance* inst, U32 id, U32 key, U32 len) {
    char name[512];
    (void)inst;
    if (len >= sizeof(name) || !srt_mem_ok(key, len)) return;
    memcpy(name, srt_mem_ptr(key), len);
    name[len] = 0;
    {
        int p = plat_id((int)id), page = remap_page_of(name, len);
        if (p < 0) return;
        page_of_plat[p] = page + 1;
        ch360_plat_texture_file(p, name);
    }
}

void i5_ch36014_textureX5Fpixels(wasmModuleInstance* inst, U32 id, U32 w, U32 h, U32 bgra) {
    (void)inst;
    if (w == 0 || h == 0 || w > 8192 || h > 8192 || !srt_mem_ok(bgra, w * h * 4)) return;
    {
        int p = plat_id((int)id);
        if (p < 0) return;
        page_of_plat[p] = 0;
        ch360_plat_texture_pixels(p, (int)w, (int)h, srt_mem_ptr(bgra));
    }
}

void i5_ch3605_audio(wasmModuleInstance* inst, U32 samples, U32 frames) {
    (void)inst;
    if (frames == 0 || frames > 48000 || !srt_mem_ok(samples, frames * 8)) return;
    ch360_plat_audio(srt_mem_ptr(samples), (int)frames);
}

static void run_start(void* p) {
    m6_playerExport6_X5Fstart((m6_playerInstance*)p);
}

int ch360_staged_names = 0;

void ch360_staged_name(const char* key, size_t len, char* out) {
    static const char hex[] = "0123456789ABCDEF";
    unsigned hi, lo;
    int k;
    ch360_fnv64(key, len, &hi, &lo);
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
     * over the native bot run against 3.4 s (round 10). */
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
    /* after the module's memory (xsdk's heap takes the free address space at its first growth) */
    ch360_ctex_pack = ch360_pack_open(root_dir, "CTEX");
    remap_load();
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
