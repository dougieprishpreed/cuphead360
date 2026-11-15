/* The translated player's "ch360" imports (managed ConsoleHost.cs) and its
 * start-up under the wasm runtime (Spire360's srt, WASI preview1). C89,
 * because the translated module's header is C. Guest pointers become host
 * pointers through srt_mem_ptr; multi-byte values in guest memory are
 * little-endian, which the platform layer reads with ch360_u32/ch360_f32. */
#include <math.h>
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

void i5_ch36010_hostX5Fstats(wasmModuleInstance* inst, U32 out, U32 n) {
    unsigned v[CH360_HOST_STATS];
    unsigned i, k = n > CH360_HOST_STATS ? CH360_HOST_STATS : n;
    U8* s;
    (void)inst;
    if (k == 0 || !srt_mem_ok(out, k * 4)) return;
    memset(v, 0, sizeof(v));
    ch360_plat_host_stats(v, (int)k);
    s = srt_mem_ptr(out);
    for (i = 0; i < k; i++) {
        s[i * 4] = (U8)v[i]; s[i * 4 + 1] = (U8)(v[i] >> 8); s[i * 4 + 2] = (U8)(v[i] >> 16); s[i * 4 + 3] = (U8)(v[i] >> 24);
    }
}

/* A line from the 64-bit check (Test64.cs) straight to the host's log, past the game's console output,
 * which the console's test build compiles -Os like the code under test. */
void i5_ch3608_logX5Fline(wasmModuleInstance* inst, U32 text, U32 len) {
    (void)inst;
    if (len == 0 || !srt_mem_ok(text, len)) return;
    ch360_plat_log((const char*)srt_mem_ptr(text), len);
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
 * draw_view(view): 13 slots, f32 x y w h, u32 clear, f32 r g b a, u32 target
 * (0 the screen, else a render texture's number), u32 its width and height,
 * f32 its clear alpha; draw_item
 * (args): 44 slots (ConsoleHost.cs, HostItem): i32 texture, u32 mode, f32
 * blend r g b a, u32 flags (1 vertex colours as they are, 2 positions
 * through the affine map, 4 clipped), f32 alpha scale, then (address, count)
 * of the triangles (i32), positions (f32 x y), uvs (f32 u v) and colours (u8
 * r g b a), f32 A00 A01 B0 A10 A11 B1, f32 clip x0 y0 x1 y1, i32 two more
 * textures, f32 three floats (mode 6, the light ray), u32 the sprite masks'
 * stencil (ch360_item.stencil), f32 twelve effect values (modes 8-17);
 * textures at CH360_RT_BASE and up are render textures, passed as they are;
 * draw_end() hands the
 * frame to the platform layer (ch360_plat_draw).
 *
 * The vertices are written in the host's byte order and the GPU's layout
 * (ch360_vertex) so the host copies them as they are. On the console's CPU
 * every move between its integer and floating-point registers goes through
 * memory and stalls (load-hit-store), so uvs and colours are copied as
 * words and only a sprite's position (through its map) is arithmetic. */

static ch360_view* draw_views;
static ch360_item* draw_items;
/* The frame drawn in between (the locked 30): each item's move since the last frame, matched by its key (its
 * renderer's id, and which of that renderer's items it is) through its map's offset (B0, B1): per item, x and y. */
static float* draw_shift;
static unsigned draw_shift_cap;
static int draw_tween;
typedef struct { U32 key, gen; float b0, b1; } tween_slot;
enum { TWEEN_SLOTS = 8192 };
static tween_slot tween_tab[2][TWEEN_SLOTS];
static U32 tween_gen[2] = { 1, 1 };
static unsigned tween_cur;
/* A move larger than this (pixels) is a jump, not a move: not carried on (a teleport, a respawn, a new camera). */
#define TWEEN_MAX_MOVE 96.0f
static void tween_item(unsigned idx, U32 key, int affine, float b0, float b1);
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
static U8 unorm8(float f) {
    return (U8)(f <= 0.0f ? 0 : f >= 1.0f ? 255 : (int)(f * 255.0f + 0.5f));
}

static void draw_view_add(const float* xywh, unsigned clear, float r, float g, float b, unsigned target, unsigned tw, unsigned th, float a) {
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
    v->target = target;
    v->tw = tw;
    v->th = th;
    v->a = a;
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
    unsigned prefetched;   /* its sprite pages were asked for on a first draw */
} remap_page;
#define REMAP_GRID 8
typedef struct remap_box {
    float u0, v0, u1, v1, su, ou, sv, ov;
    unsigned target;
    /* version 2: 8 x 8 cells over the box (bit 8 * row + column, rows along v), set where its sprite page holds the
     * sprite's texels; all set: the whole box (version 1, or a sprite copied whole). */
    unsigned mask_lo, mask_hi;
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

/* Each platform id's name as the host was given it (the learned read-ahead records drawn pages by name). */
static const char** plat_names;
static unsigned plat_names_cap;

static void name_plat(int id, const char* name, int copy) {
    if (id >= 0) {
        if ((unsigned)id >= plat_names_cap) {
            unsigned c = plat_names_cap ? plat_names_cap : 256;
            const char** b;
            while (c <= (unsigned)id) c *= 2;
            b = (const char**)realloc((void*)plat_names, c * sizeof(const char*));
            if (b != NULL) {
                memset((void*)(b + plat_names_cap), 0, (c - plat_names_cap) * sizeof(const char*));
                plat_names = b;
                plat_names_cap = c;
            }
        }
        if ((unsigned)id < plat_names_cap) {
            if (copy) {
                size_t n = strlen(name);
                char* d = (char*)malloc(n + 1);
                if (d != NULL) memcpy(d, name, n + 1);
                plat_names[id] = d;
            } else {
                plat_names[id] = name;
            }
        }
    }
    ch360_plat_texture_file(id, name);
}

static int plat_id(int player_id) {
    if (player_id < 0 || !grow_ints(&plat_of_player, &player_cap, (unsigned)player_id + 1, -1)) return -1;
    if (plat_of_player[player_id] < 0) plat_of_player[player_id] = new_plat();
    return plat_of_player[player_id];
}

/* The remap table: 'CHRM' u32 version (1 or 2) pages boxes targets string-bytes;
 * pages (u32 hash_hi hash_lo first count, by hash), boxes (f32 u0 v0 u1 v1
 * su ou sv ov, u32 target; version 2: u32 mask_lo mask_hi), targets (u32 offset of the page's key), keys.
 * Version 2 is for a layout that copies onto the sprite pages only the blocks the sprites' triangles were seen to read
 * (the PC sweep's CH360_TRACE_COVER; tools/cluster_console.py doesn't make it: packed by shape, Glumstone's pages came
 * out only 7% smaller, 2026-11-14); a draw whose vertices leave those blocks (a quad over a sprite seen only as its
 * mesh) would stay on the atlas page (remap_fits). Version 1's boxes are whole: the check returns at once. */
static void remap_load(void) {
    ch360_pack_entry e;
    unsigned char* d;
    unsigned i, need, sb, bs, version;
    if (ch360_ctex_pack < 0 || !ch360_pack_find(ch360_ctex_pack, "ctexmap", 7, &e) || e.size < 24) return;
    d = (unsigned char*)malloc(e.size);
    if (d == NULL || ch360_pack_read(&e, 0, d, e.size) != 0 || le32(d) != 0x4d524843u || (le32(d + 4) != 1 && le32(d + 4) != 2)) {
        free(d);
        return;
    }
    version = le32(d + 4);
    bs = version == 2 ? 44u : 36u;
    rm_page_count = le32(d + 8);
    rm_box_count = le32(d + 12);
    rm_target_count = le32(d + 16);
    sb = le32(d + 20);
    need = 24 + rm_page_count * 16 + rm_box_count * bs + rm_target_count * 4 + sb;
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
        rm_pages[i].prefetched = 0;
    }
    for (i = 0; i < rm_box_count; i++) {
        const U8* p = d + 24 + rm_page_count * 16 + i * bs;
        rm_boxes[i].u0 = lef(p);
        rm_boxes[i].v0 = lef(p + 4);
        rm_boxes[i].u1 = lef(p + 8);
        rm_boxes[i].v1 = lef(p + 12);
        rm_boxes[i].su = lef(p + 16);
        rm_boxes[i].ou = lef(p + 20);
        rm_boxes[i].sv = lef(p + 24);
        rm_boxes[i].ov = lef(p + 28);
        rm_boxes[i].target = le32(p + 32);
        rm_boxes[i].mask_lo = version == 2 ? le32(p + 36) : 0xFFFFFFFFu;
        rm_boxes[i].mask_hi = version == 2 ? le32(p + 40) : 0xFFFFFFFFu;
    }
    for (i = 0; i < rm_target_count; i++) {
        rm_names[i] = le32(d + 24 + rm_page_count * 16 + rm_box_count * bs + i * 4);
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

/* The sprite pages cut from an atlas page, read ahead (ch360_plat_texture_prefetch
 * decides by memory) rather than when their first frame is drawn (a frame then
 * waits for them): once when one of them is first drawn (mostly the same
 * character's other animations), and whenever the game loads the page's atlas
 * (the player's texture_prefetch). */
static void remap_prefetch(remap_page* p, int always) {
    unsigned k;
    if (p->prefetched && !always) return;
    p->prefetched = 1;
    for (k = 0; k < p->count; k++) {
        unsigned t = rm_boxes[p->first + k].target;
        if (t >= rm_target_count) continue;
        if (rm_plat[t] < 0) {
            int id = rm_plat[t] = new_plat();
            if (id < 0) return;
            name_plat(id, rm_strings + rm_names[t], 0);
        }
        ch360_plat_texture_prefetch(rm_plat[t], 0);
    }
}

/* The item's atlas page, if its sprites have pages of their own (else NULL). */
static remap_page* remap_page_for(const ch360_item* it) {
    int page;
    /* the light ray reads two more textures at its uvs; the Knight's glow and
     * the Bishop's darkness sample render textures */
    if (it->texture < 0 || (unsigned)it->texture >= plat_cap || it->mode == 6 || it->mode == 8 || it->mode == 9) return NULL;
    page = page_of_plat[it->texture] - 1;
    if (page < 0 || rm_pages[page].count == 0) return NULL;
    return &rm_pages[page];
}

/* Moves an item whose uvs span the box (u0, v0)-(u1, v1) onto the sprite page
 * holding that box: the item's texture and its taps' offsets; the box found
 * maps its uvs (NULL: none holds it, the item stays on the atlas page). */
/* The index in rm_boxes of the page's box holding (u0, v0)-(u1, v1), or -1. */
static int remap_find(remap_page* p, float u0, float v0, float u1, float v1) {
    unsigned n, from, to;
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
            p->last = bi - p->first;
            return (int)bi;
        }
    }
    return -1;
}

/* ---- what of each atlas page the sprites' triangles read (CH360_TRACE_COVER, the PC host) ----
 * Per source atlas page a 1024 x 1024 grid over its uv (4 texels a cell on a 4096 page: one DXT block), a cell set
 * where any triangle drawn from the page's sprites, widened by a cell, meets it: the texels a draw can sample
 * (bilinear reads half a texel beyond a triangle). tools/cluster_console.py (--cover) copies only those blocks of
 * each sprite: transparent space around and inside irregular shapes was 37-85% of Glumstone's pages. Each sprite mesh
 * once (a table of the meshes seen); written at exit (ch360_cover_write): per page u32 hi, lo, then 1024 * 1024 / 8
 * bytes; then u32 0xFFFFFFFF, the count of sprite boxes drawn and each as u32 hi, lo and f32 u0, v0, u1, v1. */
#define COVER_N 1024u
const char* ch360_cover_path;
static unsigned char** cover_bits;     /* per remap page */
static unsigned* cover_seen;
static unsigned cover_seen_cap, cover_seen_n;

static int cover_new_mesh(unsigned key) {
    unsigned h, i;
    if (key == 0) key = 1;
    if (cover_seen_n * 2 >= cover_seen_cap) {
        unsigned cap = cover_seen_cap ? cover_seen_cap * 2 : 65536, *b = (unsigned*)calloc(cap, sizeof(unsigned));
        if (b == NULL) return 1;
        for (i = 0; i < cover_seen_cap; i++)
            if (cover_seen[i]) {
                for (h = cover_seen[i] & (cap - 1); b[h]; h = (h + 1) & (cap - 1)) {}
                b[h] = cover_seen[i];
            }
        free(cover_seen);
        cover_seen = b;
        cover_seen_cap = cap;
    }
    for (h = key & (cover_seen_cap - 1); cover_seen[h]; h = (h + 1) & (cover_seen_cap - 1))
        if (cover_seen[h] == key) return 0;
    cover_seen[h] = key;
    cover_seen_n++;
    return 1;
}

static void cover_tri(unsigned char* bits, const float* a, const float* b, const float* c) {
    float x[3] = { a[0] * COVER_N, b[0] * COVER_N, c[0] * COVER_N }, y[3] = { a[1] * COVER_N, b[1] * COVER_N, c[1] * COVER_N };
    float area = (x[1] - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (y[1] - y[0]), nx[3], ny[3], off[3], d[3];
    int x0, x1, y0, y1, cx, cy, e;
    if (area > -1e-9f && area < 1e-9f) return;   /* draws nothing */
    for (e = 0; e < 3; e++) {
        int f = (e + 1) % 3;
        /* inward normal of the edge (e, f) whatever the winding */
        nx[e] = area > 0 ? -(y[f] - y[e]) : (y[f] - y[e]);
        ny[e] = area > 0 ? (x[f] - x[e]) : -(x[f] - x[e]);
        d[e] = nx[e] * x[e] + ny[e] * y[e];
        /* a cell's half extent projected, and one cell more */
        off[e] = 0.5f * (fabsf(nx[e]) + fabsf(ny[e])) + sqrtf(nx[e] * nx[e] + ny[e] * ny[e]);
    }
    x0 = (int)floorf(fminf(x[0], fminf(x[1], x[2]))) - 2;
    x1 = (int)floorf(fmaxf(x[0], fmaxf(x[1], x[2]))) + 2;
    y0 = (int)floorf(fminf(y[0], fminf(y[1], y[2]))) - 2;
    y1 = (int)floorf(fmaxf(y[0], fmaxf(y[1], y[2]))) + 2;
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 > (int)COVER_N - 1) x1 = (int)COVER_N - 1;
    if (y1 > (int)COVER_N - 1) y1 = (int)COVER_N - 1;
    for (cy = y0; cy <= y1; cy++)
        for (cx = x0; cx <= x1; cx++) {
            float px = cx + 0.5f, py = cy + 0.5f;
            if (nx[0] * px + ny[0] * py - d[0] >= -off[0] && nx[1] * px + ny[1] * py - d[1] >= -off[1] &&
                nx[2] * px + ny[2] * py - d[2] >= -off[2])
                bits[(cy * COVER_N + cx) >> 3] |= (unsigned char)(1u << (cx & 7));
        }
}

static unsigned char* cover_box_drawn;   /* per box: drawn */

/* An item about to be drawn from an atlas page: all its mesh's triangles (before any culling or clipping), in the
 * page's uv, onto the page's grid; the sprite box that holds the mesh marked drawn. Each mesh once. */
static void cover_mesh(const ch360_item* it, const U8* tri, U32 ntri, const U8* uv, U32 nuv) {
    remap_page* p;
    float u0 = 3.0e38f, v0 = 3.0e38f, u1 = -3.0e38f, v1 = -3.0e38f;
    unsigned k, key = 2166136261u, pi;
    int bi;
    if (ch360_cover_path == NULL || nuv == 0 || (p = remap_page_for(it)) == NULL) return;
    for (k = 0; k < nuv; k++) {
        float u = lef(uv + 8 * k), v = lef(uv + 8 * k + 4);
        if (u < u0) u0 = u;
        if (u > u1) u1 = u;
        if (v < v0) v0 = v;
        if (v > v1) v1 = v;
        key = (key ^ le32(uv + 8 * k)) * 16777619u;
        key = (key ^ le32(uv + 8 * k + 4)) * 16777619u;
    }
    if ((bi = remap_find(p, u0, v0, u1, v1)) < 0) return;
    key = (key ^ (unsigned)bi) * 16777619u;
    key = (key ^ ntri) * 16777619u;
    if (!cover_new_mesh(key)) return;
    pi = (unsigned)(p - rm_pages);
    if (cover_bits == NULL) cover_bits = (unsigned char**)calloc(rm_page_count, sizeof(unsigned char*));
    if (cover_box_drawn == NULL) cover_box_drawn = (unsigned char*)calloc(rm_box_count, 1);
    if (cover_bits == NULL || cover_box_drawn == NULL || pi >= rm_page_count) return;
    if (cover_bits[pi] == NULL && (cover_bits[pi] = (unsigned char*)calloc(COVER_N * COVER_N / 8, 1)) == NULL) return;
    cover_box_drawn[bi] = 1;
    for (k = 0; k + 2 < ntri; k += 3) {
        float t[3][2];
        unsigned q;
        for (q = 0; q < 3; q++) {
            U32 i = le32(tri + 4 * (k + q));
            if (i >= nuv) i = 0;
            t[q][0] = lef(uv + 8 * i);
            t[q][1] = lef(uv + 8 * i + 4);
        }
        cover_tri(cover_bits[pi], t[0], t[1], t[2]);
    }
}

void ch360_cover_write(void) {
    FILE* f;
    unsigned i, n = 0;
    if (ch360_cover_path == NULL || cover_bits == NULL || (f = fopen(ch360_cover_path, "wb")) == NULL) return;
    for (i = 0; i < rm_page_count; i++)
        if (cover_bits[i] != NULL) {
            fwrite(&rm_pages[i].hi, 4, 1, f);
            fwrite(&rm_pages[i].lo, 4, 1, f);
            fwrite(cover_bits[i], 1, COVER_N * COVER_N / 8, f);
            n++;
        }
    {
        unsigned boxes = 0, mark = 0xFFFFFFFFu;
        for (i = 0; i < rm_box_count; i++) boxes += cover_box_drawn != NULL && cover_box_drawn[i];
        fwrite(&mark, 4, 1, f);
        fwrite(&boxes, 4, 1, f);
        for (i = 0; i < rm_page_count; i++) {
            unsigned k;
            for (k = rm_pages[i].first; k < rm_pages[i].first + rm_pages[i].count && k < rm_box_count; k++)
                if (cover_box_drawn != NULL && cover_box_drawn[k]) {
                    fwrite(&rm_pages[i].hi, 4, 1, f);
                    fwrite(&rm_pages[i].lo, 4, 1, f);
                    fwrite(&rm_boxes[k].u0, 4, 4, f);
                }
        }
        fclose(f);
        printf("[host] cover: %u atlas pages, %u sprite meshes, %u sprites drawn, written to %s\n", n, cover_seen_n, boxes, ch360_cover_path);
    }
}

/* A box's sprite page's id, given to the host the first time (its siblings read ahead then). */
static int remap_target_id(remap_page* p, const remap_box* b) {
    int id = b->target < rm_target_count ? rm_plat[b->target] : -1;
    if (id < 0 && b->target < rm_target_count) {
        id = rm_plat[b->target] = new_plat();
        if (id >= 0) {
            name_plat(id, rm_strings + rm_names[b->target], 0);
            remap_prefetch(p, 0);
        }
    }
    return id;
}

/* CH360_TRACE_MISSES=n (the PC host): the first n items whose uv box no sprite box holds, logged. */
int rm_trace_misses;

/* Every vertex (n uvs, stride floats apart) in a cell of the box its sprite page holds. */
static unsigned rm_guarded;
static int remap_fits(const remap_box* b, const float* uv, unsigned n, unsigned stride) {
    float fu, fv;
    unsigned k;
    if ((b->mask_lo & b->mask_hi) == 0xFFFFFFFFu) return 1;
    fu = 8.0f / (b->u1 - b->u0);
    fv = 8.0f / (b->v1 - b->v0);
    for (k = 0; k < n; k++, uv += stride) {
        int cu = (int)((uv[0] - b->u0) * fu), cv = (int)((uv[1] - b->v0) * fv), bit;
        if (cu < 0) cu = 0;
        if (cu > 7) cu = 7;
        if (cv < 0) cv = 0;
        if (cv > 7) cv = 7;
        bit = cv * 8 + cu;
        if (!((bit < 32 ? b->mask_lo >> bit : b->mask_hi >> (bit - 32)) & 1u)) return 0;
    }
    return 1;
}

unsigned ch360_remap_guarded(void) {
    return rm_guarded;
}

static const remap_box* remap_lookup(remap_page* p, ch360_item* it, float u0, float v0, float u1, float v1,
                                     const float* uv, unsigned n, unsigned stride) {
    int bi = remap_find(p, u0, v0, u1, v1), id;
    const remap_box* b;
    if (bi < 0) {
        rm_misses++;
        if (rm_trace_misses > 0 && it->texture >= 0 && (unsigned)it->texture < plat_names_cap && plat_names[it->texture] != NULL) {
            char line[512];
            int n;
            rm_trace_misses--;
            n = snprintf(line, sizeof(line), "REMAP miss: %s uv %.5f %.5f - %.5f %.5f, mode %u, %u vertices\n",
                         plat_names[it->texture], u0, v0, u1, v1, it->mode, it->count);
            if (n > 0) ch360_plat_log(line, (size_t)(n < (int)sizeof(line) ? n : (int)sizeof(line) - 1));
        }
        return NULL;
    }
    b = &rm_boxes[bi];
    if (!remap_fits(b, uv, n, stride)) {
        /* drawn in a way the sweep never saw: the atlas page (halved) rather than a neighbour's texels */
        rm_guarded++;
        if (rm_guarded <= 8) {
            char line[512];
            int m = snprintf(line, sizeof(line), "REMAP guard: an item of %u vertices (mode %u) on %s reaches beyond its sprite's copied texels; drawn from the atlas page\n",
                             n, it->mode, it->texture >= 0 && (unsigned)it->texture < plat_names_cap && plat_names[it->texture] ? plat_names[it->texture] : "?");
            if (m > 0) ch360_plat_log(line, (size_t)(m < (int)sizeof(line) ? m : (int)sizeof(line) - 1));
        }
        return NULL;
    }
    id = remap_target_id(p, b);
    if (id < 0) return NULL;
    if (it->mode == 10 || it->mode == 35) { /* their taps' offsets are in the page's uvs */
        it->params[8] *= b->su;
        it->params[9] *= b->sv;
    }
    it->texture = id;
    return b;
}

/* Moves an item drawn from an atlas page onto its sprite's full-size page. */
static void remap_item(ch360_item* it) {
    remap_page* p = remap_page_for(it);
    const remap_box* b;
    ch360_vertex* v;
    float u0, v0, u1, v1;
    unsigned k;
    if (p == NULL) return;
    v = draw_verts + it->first;
    u0 = u1 = v->u;
    v0 = v1 = v->v;
    for (k = 1; k < it->count; k++) {
        if (v[k].u < u0) u0 = v[k].u;
        if (v[k].u > u1) u1 = v[k].u;
        if (v[k].v < v0) v0 = v[k].v;
        if (v[k].v > v1) v1 = v[k].v;
    }
    b = remap_lookup(p, it, u0, v0, u1, v1, &draw_verts[it->first].u, it->count, (unsigned)(sizeof(ch360_vertex) / sizeof(float)));
    if (b == NULL) return;
    for (k = 0; k < it->count; k++) {
        v[k].u = v[k].u * b->su + b->ou;
        v[k].v = v[k].v * b->sv + b->ov;
    }
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
    if (it->mode == 0 && it->stencil != 1 && it->stencil != 2) {
        /* A sprite wholly transparent draws nothing (premultiplied: the shader's
         * alpha is the vertex's times the blend's): the loading screen's
         * hourglass stays at alpha 0 all game long and kept its 3.6 MB page. */
        const ch360_vertex* v = draw_verts + it->first;
        unsigned k;
        for (k = 0; k < it->count && (v[k].color[3] == 0 || v[k].blend[3] == 0); k++) {}
        if (k == it->count) {
            draw_vert_count = it->first;
            draw_item_count--;
            return;
        }
    }
    remap_item(it);
    draw_item_box(it);
    draw_views[draw_view_count - 1].items++;
}

/* A texture as the platform knows it: the player's ids numbered for it,
 * render textures as they are. */
static int draw_texture(int id) {
    return id >= CH360_RT_BASE ? id : plat_id(id);
}

void i5_ch3609_drawX5Fview(wasmModuleInstance* inst, U32 view) {
    const U8* p;
    float xywh[4];
    (void)inst;
    if (!srt_mem_ok(view, 52)) return;
    p = srt_mem_ptr(view);
    xywh[0] = lef(p);
    xywh[1] = lef(p + 4);
    xywh[2] = lef(p + 8);
    xywh[3] = lef(p + 12);
    draw_view_add(xywh, le32(p + 16), lef(p + 20), lef(p + 24), lef(p + 28), le32(p + 36), le32(p + 40), le32(p + 44), lef(p + 48));
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

/* An item's arrays as words in native order: its positions (then, mapped, the
 * screen's), then its uvs (then the sprite page's). */
static U32* draw_words;
static unsigned draw_words_cap;

/* An unclipped item's triangle list. Each array is swapped into words once
 * and each vertex mapped once, and the list is written as plain words: the
 * corner-by-corner loop moved each swapped word to the float unit through
 * the stack, a load-hit-store stall per value, about 200 ns a corner on the
 * console (10 ms a frame for Forest Follies' 45,000: its backgrounds' meshes
 * have 700-1,100 triangles each). The screen box and the sprite page's uv box
 * come from the vertices rather than the corners (a sprite's mesh uses all
 * of them). */
/* Corners (three a triangle) from which a mesh's off-screen triangles are left out. */
#define DRAW_CULL_CORNERS 3000u

static void draw_item_unclipped(ch360_item* it, const U8* tri, U32 ntri, const U8* pos, U32 npos, const U8* uv, U32 nuv,
                                const U8* col, U32 ncol, const float* m, const U8* blend) {
    U32 *sp, *su, *o, i, bw;
    float *fp, *fu, x0, y0, x1, y1;
    remap_page* p;
    /* the triangles index the vertices only: uvs and colours past them are never drawn */
    if (nuv > npos) nuv = npos;
    if (ncol > npos) ncol = npos;
    if (it->mode == 0 && it->stencil != 1 && it->stencil != 2) {
        /* A sprite wholly transparent draws nothing (premultiplied: the shader's
         * alpha is the vertex's times the blend's): the loading screen's
         * hourglass stays at alpha 0 all game long and kept its 3.6 MB page. */
        int seen = blend[3] != 0 && ncol < npos;   /* vertices without a colour are white */
        for (i = 0; !seen && blend[3] != 0 && i < ncol; i++) seen = col[4 * i + 3] != 0;
        if (!seen) {
            draw_item_count--;
            return;
        }
    }
    if (!draw_grow((void**)&draw_words, &draw_words_cap, 2 * (npos + nuv), sizeof(U32))) {
        draw_item_count--;
        return;
    }
    sp = draw_words;
    su = draw_words + 2 * npos;
    fp = (float*)sp;
    fu = (float*)su;
    for (i = 0; i < 2 * npos; i++) sp[i] = le32(pos + 4 * i);
    for (i = 0; i < 2 * nuv; i++) su[i] = le32(uv + 4 * i);
    x0 = y0 = 3.0e38f;
    x1 = y1 = -3.0e38f;
    for (i = 0; i < npos; i++) {
        float x = fp[2 * i], y = fp[2 * i + 1];
        if (m != NULL) {
            float sx = m[0] * x + m[1] * y + m[2];
            float sy = m[3] * x + m[4] * y + m[5];
            fp[2 * i] = x = sx;
            fp[2 * i + 1] = y = sy;
        }
        if (x < x0) x0 = x;
        if (x > x1) x1 = x;
        if (y < y0) y0 = y;
        if (y > y1) y1 = y;
    }
    p = remap_page_for(it);
    if (p != NULL) {
        const remap_box* b;
        float u0 = nuv < npos ? 0.0f : 3.0e38f, v0 = u0, u1 = nuv < npos ? 0.0f : -3.0e38f, v1 = u1;
        for (i = 0; i < nuv; i++) {
            float u = fu[2 * i], v = fu[2 * i + 1];
            if (u < u0) u0 = u;
            if (u > u1) u1 = u;
            if (v < v0) v0 = v;
            if (v > v1) v1 = v;
        }
        b = remap_lookup(p, it, u0, v0, u1, v1, fu, nuv, 2);
        if (b != NULL)
            for (i = 0; i < nuv; i++) {
                fu[2 * i] = fu[2 * i] * b->su + b->ou;
                fu[2 * i + 1] = fu[2 * i + 1] * b->sv + b->ov;
            }
    }
    memcpy(&bw, blend, 4);
    o = (U32*)(draw_verts + draw_vert_count);
    if (ntri >= DRAW_CULL_CORNERS && it->stencil == 0) {
        /* A mesh this large (Hilda Berg's star layers, wider than the screen): the triangles wholly off one
         * side of the view are left out. */
        const ch360_view* vw = &draw_views[draw_view_count - 1];
        float vx0 = vw->x - 1.0f, vy0 = vw->y - 1.0f, vx1 = vw->x + vw->w + 1.0f, vy1 = vw->y + vw->h + 1.0f;
        U32 kept = 0, t;
        for (t = 0; t + 2 < ntri; t += 3) {
            U32 q, kk[3];
            float ax, ay, bx, by, cx, cy;
            for (q = 0; q < 3; q++) {
                kk[q] = le32(tri + 4 * (t + q));
                if (kk[q] >= npos) kk[q] = 0;
            }
            ax = fp[2 * kk[0]]; ay = fp[2 * kk[0] + 1];
            bx = fp[2 * kk[1]]; by = fp[2 * kk[1] + 1];
            cx = fp[2 * kk[2]]; cy = fp[2 * kk[2] + 1];
            if ((ax < vx0 && bx < vx0 && cx < vx0) || (ax > vx1 && bx > vx1 && cx > vx1) ||
                (ay < vy0 && by < vy0 && cy < vy0) || (ay > vy1 && by > vy1 && cy > vy1))
                continue;
            for (q = 0; q < 3; q++, o += 6) {
                U32 k = kk[q], c = 0xFFFFFFFFu;
                o[0] = sp[2 * k];
                o[1] = sp[2 * k + 1];
                if (k < nuv) {
                    o[2] = su[2 * k];
                    o[3] = su[2 * k + 1];
                } else {
                    o[2] = o[3] = 0;
                }
                if (k < ncol) memcpy(&c, col + 4 * k, 4);
                o[4] = c;
                o[5] = bw;
            }
            kept += 3;
        }
        ntri = kept;
    } else
    for (i = 0; i < ntri; i++, o += 6) {
        U32 k = le32(tri + 4 * i), c = 0xFFFFFFFFu;
        if (k >= npos) k = 0;
        o[0] = sp[2 * k];
        o[1] = sp[2 * k + 1];
        if (k < nuv) {
            o[2] = su[2 * k];
            o[3] = su[2 * k + 1];
        } else {
            o[2] = o[3] = 0;
        }
        if (k < ncol) memcpy(&c, col + 4 * k, 4);
        o[4] = c;
        o[5] = bw;
    }
    draw_vert_count += ntri;
    it->count = ntri;
    it->x0 = x0;
    it->y0 = y0;
    it->x1 = x1;
    it->y1 = y1;
    draw_views[draw_view_count - 1].items++;
}

void i5_ch3609_drawX5Fitem(wasmModuleInstance* inst, U32 args) {
    static const U8 white[4] = { 255, 255, 255, 255 };
    const U8 *a, *tri, *pos, *uv = NULL, *col = NULL;
    U32 flags, ntri, npos, nuv, ncol, i;
    float m[6], clip[4], tint[4];
    U8 blend[4];
    int k, mode, affine, clipped;
    ch360_item* it;
    U32 key, kept_from = draw_item_count;
    double t0 = draw_now_ms();
    (void)inst;
    if (draw_view_count == 0 || !srt_mem_ok(args, 45 * 4)) return;
    a = srt_mem_ptr(args);
    key = le32(a + 176);
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
    it->texture = draw_texture((int)le32(a));
    it->mode = (unsigned)mode;
    it->first = draw_vert_count;
    it->textures[0] = draw_texture((int)le32(a + 104));
    it->textures[1] = draw_texture((int)le32(a + 108));
    for (k = 0; k < 12; k++) it->params[k] = mode == 6 ? (k < 3 ? lef(a + 112 + 4 * k) : 0.0f) : lef(a + 128 + 4 * k);
    it->stencil = le32(a + 124);
    if (ch360_cover_path != NULL) cover_mesh(it, tri, ntri, uv, nuv < npos ? nuv : npos);
    if (!clipped) {
        draw_item_unclipped(it, tri, ntri, pos, npos, uv, nuv, col, ncol, affine ? m : NULL, blend);
        if (draw_item_count > kept_from) tween_item(draw_item_count - 1, key, affine, m[2], m[5]);
        draw_build_ms += draw_now_ms() - t0;
        return;
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
    if (draw_item_count > kept_from) tween_item(draw_item_count - 1, key, affine, m[2], m[5]);
    draw_build_ms += draw_now_ms() - t0;
}

/* frame_tween(on): this frame of the game's has a frame drawn after it, in between (1 if the platform does). */
U32 i5_ch36011_frameX5Ftween(wasmModuleInstance* inst, U32 on) {
    (void)inst;
    draw_tween = ch360_plat_tween((int)on);
    return (U32)draw_tween;
}

static tween_slot* tween_find(unsigned t, U32 key, int insert) {
    U32 i = (key * 2654435761u) >> 19, probe;   /* the top 13 bits: TWEEN_SLOTS */
    for (probe = 0; probe < 64; probe++, i = (i + 1) & (TWEEN_SLOTS - 1)) {
        tween_slot* s = &tween_tab[t][i];
        if (s->gen != tween_gen[t]) {
            if (!insert) return NULL;
            s->gen = tween_gen[t];
            s->key = key;
            return s;
        }
        if (s->key == key) return s;
    }
    return NULL;
}

/* An item just kept (index idx) with key and map offset (b0, b1): its move since the last frame's same item. */
static void tween_item(unsigned idx, U32 key, int affine, float b0, float b1) {
    float dx = 0.0f, dy = 0.0f;
    if (!draw_grow((void**)&draw_shift, &draw_shift_cap, idx + 1, 2 * sizeof(float))) return;
    if (key != 0 && affine) {
        U32 n = 0, k = key * 64u;
        tween_slot *s, *was;
        while (n < 63 && tween_find(tween_cur, k + n, 0) != NULL) n++;   /* the n-th of this renderer's items */
        s = tween_find(tween_cur, k + n, 1);
        if (s != NULL) {
            s->b0 = b0;
            s->b1 = b1;
        }
        was = tween_find(tween_cur ^ 1u, k + n, 0);
        if (was != NULL) {
            dx = b0 - was->b0;
            dy = b1 - was->b1;
            if (dx * dx + dy * dy > TWEEN_MAX_MOVE * TWEEN_MAX_MOVE) dx = dy = 0.0f;
        }
    }
    draw_shift[2 * idx] = dx;
    draw_shift[2 * idx + 1] = dy;
}

/* ConsoleHost: this frame is the loading screen with its hourglass faded in (0 if not; else
 * 0x80000000 | the hourglass's cycle in ms << 16 | where it is in it, ms: ch360_frame.loading). */
static unsigned draw_loading;
void i5_ch36014_loadingX5Fscreen(wasmModuleInstance* inst, U32 on) {
    (void)inst;
    draw_loading = on;
}

/* ---- learned read-ahead ----
 * Each scene's textures in the order its plays first drew them (play frames from its start): <root>/learned/<scene>.txt,
 * written as the scene ends, merged with what was there (a page's earliest frame kept); at first GameData/learned/
 * (the PC's bot plays, tools/learned_lists.py). While the scene plays, the LEARN_AHEAD pages after the furthest one it
 * has drawn are asked for: a boss's next form, a spawn's first frames, the run-and-gun level's next stretch came with no
 * read-ahead, each first draw a 50-300 ms wait on the console. tools/texture_cache_sim.py, learned from one PC play and
 * tried on another: Forest Follies 12 -> 4 frames late, Treetop 10 -> 4, Hilda Berg 58 -> 41 (more pages ahead
 * cost more than they saved). */
#ifdef _WIN32
#include <direct.h>
#define learn_mkdir(p) _mkdir(p)
#else
#include <sys/stat.h>
#define learn_mkdir(p) mkdir(p, 0777)
#endif
#define LEARN_AHEAD 4
/* With room (the platform's read-ahead not held back by memory lately) twice as far: tools/texture_cache_sim.py with the
 * console's measured loader (15.8 MB/s, the forty-second run), Hilda Berg learned from one play and tried on another:
 * 14 -> 9 frames late at 150 MB of room, 10 -> 4 at 170; Forest Follies and Treetop the same or one fewer. Tight (110 MB),
 * eight cost more than they saved (Forest Follies 10 -> 15). */
#define LEARN_AHEAD_ROOM 8
#define LEARN_MAX 4096
typedef struct learned_page {
    unsigned frame;
    char* key;
    int id;
} learned_page;
static learned_page learned[LEARN_MAX];
static unsigned learned_n, learned_loaded;   /* entries, those read at the scene's start (in frame order) */
static int* plat_learned;                    /* platform id -> learned index + 1 */
static unsigned plat_learned_cap;
static unsigned char* plat_seen;             /* drawn in this visit */
static unsigned plat_seen_cap;
static unsigned learn_frame, learn_dirty, learn_asks;
static int learn_far = -1;
static char learn_scene[128];
static char learn_root[512];
static unsigned* rm_by_name;
static unsigned rm_by_name_size;

static unsigned learn_hash(const char* k) {
    unsigned h = 2166136261u;
    while (*k) h = (h ^ (unsigned char)*k++) * 16777619u;
    return h;
}

/* The platform id of a texture by its name: a sprite page (named to the host now if it wasn't) or one already named;
 * -1 if neither. */
static int learn_id_of(const char* key) {
    unsigned h, t, i;
    if (rm_target_count > 0 && rm_by_name == NULL) {
        unsigned size = 1;
        while (size < rm_target_count * 2 + 2) size <<= 1;
        rm_by_name = (unsigned*)calloc(size, sizeof(unsigned));
        if (rm_by_name != NULL) {
            rm_by_name_size = size;
            for (i = 0; i < rm_target_count; i++) {
                h = learn_hash(rm_strings + rm_names[i]) & (size - 1);
                while (rm_by_name[h]) h = (h + 1) & (size - 1);
                rm_by_name[h] = i + 1;
            }
        }
    }
    if (rm_by_name != NULL)
        for (h = learn_hash(key) & (rm_by_name_size - 1); (t = rm_by_name[h]) != 0; h = (h + 1) & (rm_by_name_size - 1))
            if (strcmp(rm_strings + rm_names[t - 1], key) == 0) {
                int id = rm_plat[t - 1];
                if (id < 0) {
                    id = rm_plat[t - 1] = new_plat();
                    if (id >= 0) name_plat(id, rm_strings + rm_names[t - 1], 0);
                }
                return id;
            }
    for (i = 0; i < plat_names_cap; i++)
        if (plat_names[i] != NULL && strcmp(plat_names[i], key) == 0) return (int)i;
    return -1;
}

static int learn_grow(void** p, unsigned* cap, unsigned need, size_t size) {
    unsigned c = *cap ? *cap : 256;
    void* b;
    if (need <= *cap) return 1;
    while (c < need) c *= 2;
    b = realloc(*p, (size_t)c * size);
    if (b == NULL) return 0;
    memset((char*)b + (size_t)*cap * size, 0, (size_t)(c - *cap) * size);
    *p = b;
    *cap = c;
    return 1;
}

static int learn_by_frame(const void* a, const void* b) {
    unsigned fa = ((const learned_page*)a)->frame, fb = ((const learned_page*)b)->frame;
    return fa < fb ? -1 : fa > fb;
}

/* The scene's list written (if this visit taught it anything), then forgotten. */
static void learn_save(void) {
    char path[768];
    FILE* f;
    unsigned i;
    if (learn_scene[0] && learn_dirty && learned_n > 0) {
        qsort(learned, learned_n, sizeof(learned[0]), learn_by_frame);
        snprintf(path, sizeof(path), "%s/learned", learn_root);
        learn_mkdir(path);
        snprintf(path, sizeof(path), "%s/learned/%s.txt", learn_root, learn_scene);
        f = fopen(path, "wb");
        if (f != NULL) {
            for (i = 0; i < learned_n; i++) fprintf(f, "%u %s\n", learned[i].frame, learned[i].key);
            fclose(f);
        }
    }
    for (i = 0; i < learned_n; i++) {
        free(learned[i].key);
        if (learned[i].id >= 0 && (unsigned)learned[i].id < plat_learned_cap) plat_learned[learned[i].id] = 0;
    }
    learned_n = learned_loaded = 0;
    if (plat_seen != NULL) memset(plat_seen, 0, plat_seen_cap);
    learn_scene[0] = 0;
    learn_dirty = 0;
}

static void learn_add(unsigned frame, const char* key, int id) {
    size_t n;
    if (learned_n >= LEARN_MAX) return;
    n = strlen(key);
    learned[learned_n].key = (char*)malloc(n + 1);
    if (learned[learned_n].key == NULL) return;
    memcpy(learned[learned_n].key, key, n + 1);
    learned[learned_n].frame = frame;
    learned[learned_n].id = id;
    if (id >= 0 && learn_grow((void**)&plat_learned, &plat_learned_cap, (unsigned)id + 1, sizeof(int))) plat_learned[id] = (int)learned_n + 1;
    learned_n++;
}

/* A scene begins (SceneLoad's activation): the last one's list saved, this one's read. */
static void learn_begin(const char* scene) {
    char path[768], line[512];
    FILE* f;
    unsigned frame;
    learn_save();
    learn_frame = 0;
    learn_far = -1;
    learn_asks = 0;
    if (scene == NULL || !scene[0] || strcmp(scene, "scene_load_helper") == 0 || strlen(scene) >= sizeof(learn_scene)) return;
    strcpy(learn_scene, scene);
    snprintf(path, sizeof(path), "%s/learned/%s.txt", learn_root, scene);
    f = fopen(path, "rb");
    if (f == NULL) {
        snprintf(path, sizeof(path), "%s/GameData/learned/%s.txt", learn_root, scene);
        f = fopen(path, "rb");
    }
    if (f != NULL) {
        while (fgets(line, sizeof(line), f) != NULL) {
            char* sp = strchr(line, ' ');
            size_t n;
            if (sp == NULL) continue;
            *sp = 0;
            frame = (unsigned)strtoul(line, NULL, 10);
            n = strlen(sp + 1);
            while (n > 0 && (sp[n] == '\n' || sp[n] == '\r')) sp[n--] = 0;
            if (sp[1]) learn_add(frame, sp + 1, learn_id_of(sp + 1));
        }
        fclose(f);
    }
    learned_loaded = learned_n;
}

/* A game frame drawn (not the loading screen): what it drew first in this visit learned, the pages ahead asked for. */
static void learn_drawn(void) {
    unsigned i, k, ahead;
    if (!learn_scene[0]) return;
    for (i = 0; i < draw_item_count; i++) {
        int id = draw_items[i].texture, n;
        if (id < 0 || (unsigned)id >= plat_names_cap || plat_names[id] == NULL) continue;
        if (!learn_grow((void**)&plat_seen, &plat_seen_cap, (unsigned)id + 1, 1)) continue;
        n = (unsigned)id < plat_learned_cap ? plat_learned[id] - 1 : -1;
        if (!plat_seen[id]) {
            plat_seen[id] = 1;
            if (n < 0) {
                learn_add(learn_frame, plat_names[id], id);
                learn_dirty = 1;
            } else if (learned[n].frame > learn_frame) {
                learned[n].frame = learn_frame;
                learn_dirty = 1;
            }
        }
        if (n >= 0 && (unsigned)n < learned_loaded && n > learn_far) learn_far = n;
    }
    ahead = ch360_plat_texture_tight() ? LEARN_AHEAD : LEARN_AHEAD_ROOM;
    for (k = (unsigned)(learn_far + 1); k < learned_loaded && k <= (unsigned)learn_far + ahead; k++)
        if (learned[k].id >= 0) {
            ch360_plat_texture_prefetch(learned[k].id, 1);
            learn_asks++;
        }
    learn_frame++;
}

/* A scene activated (SceneLoad, ConsoleHost): its learned read-ahead begins. */
void i5_ch36014_texturesX5Flevel(wasmModuleInstance* inst, U32 name, U32 len) {
    char scene[128];
    (void)inst;
    if (len == 0 || len >= sizeof(scene) || !srt_mem_ok(name, len)) {
        learn_begin(NULL);
        return;
    }
    memcpy(scene, srt_mem_ptr(name), len);
    scene[len] = 0;
    learn_begin(scene);
}

/* A whole file the game wrote (its save: Platform.FileWriter), at a guest path under the root: handed to the platform,
 * which writes it without holding the frame up where it can. */
/* (1 taken; 0 not: the game then writes it itself through the runtime's files.) The game joins its folder and file
 * with both separators ("Cuphead\/cuphead_player_data..."): one forward slash between names here. */
U32 i5_ch36016_fileX5FwriteX5Flater(wasmModuleInstance* inst, U32 path, U32 path_len, U32 data, U32 len) {
    char host[768];
    size_t r = strlen(learn_root);
    (void)inst;
    if (path_len == 0 || r + path_len + 2 > sizeof(host) || !srt_mem_ok(path, path_len) || (len > 0 && !srt_mem_ok(data, len))) return 0;
    memcpy(host, learn_root, r);
    if (r > 0 && host[r - 1] != '/' && host[r - 1] != '\\') host[r++] = '/';
    {
        const U8* p = srt_mem_ptr(path);
        U32 k;
        int sep = 1;   /* the root ends with one */
        for (k = 0; k < path_len; k++) {
            int s = p[k] == '/' || p[k] == '\\';
            if (s && sep) continue;
            host[r++] = s ? '/' : (char)p[k];
            sep = s;
        }
        host[r] = 0;
    }
    return ch360_plat_file_write(host, len > 0 ? srt_mem_ptr(data) : (const U8*)"", len) ? 1u : 0u;
}

/* ---- image effects over nothing, folded ----
 * A camera that draws nothing into a render texture cleared to transparent black, and image effects after it that
 * only average texels (BlurOptimized's downsample, its blur passes, a copy), leave transparent black in every target
 * they write; a sprite drawing that texture premultiplied over adds nothing. Glumstone's GeeseCamera did all of it
 * every frame of the fight, the geese in view in about one frame in twenty-five: its camera's clear, six full-screen
 * passes and a full-screen sprite (11 passes a frame, 14 ms of the console's GPU, the forty-first run). Here, per frame
 * and in order:
 *   - a pass that averages a target known to be transparent black becomes a clear of its own target (or nothing, if
 *     that is transparent black already);
 *   - a sprite that samples such a target, premultiplied over without a stencil, is left out;
 *   - a view that only clears a target, which a later view of the frame overwrites (a clear of at least the same
 *     buffers over all of it, or a pass over all of it) before anything reads it, is left out.
 * The picture is the same: every texel any kept draw reads holds what it held before. ch360_fold_effects = 0 (the
 * PC's CH360_FOLD_EFFECTS=0, the console's nofold.txt) leaves every view as the game made it. */
#define FOLD_RTS 64
int ch360_fold_effects = 1;
static ch360_view* fold_views;
static unsigned fold_views_cap;
static unsigned fold_dropped, fold_cleared, fold_items, fold_frames;

unsigned ch360_fold_counts(unsigned* cleared, unsigned* items, unsigned* frames) {
    if (cleared != NULL) *cleared = fold_cleared;
    if (items != NULL) *items = fold_items;
    if (frames != NULL) *frames = fold_frames;
    return fold_dropped;
}

static int fold_rt(int texture) {
    return texture >= CH360_RT_BASE && texture < CH360_RT_BASE + FOLD_RTS ? texture - CH360_RT_BASE : -1;
}

static int fold_whole(const ch360_view* v) {
    return v->target != 0 && v->x <= 0.0f && v->y <= 0.0f && v->x + v->w >= (float)v->tw && v->y + v->h >= (float)v->th;
}

/* An item over all of the view's target (a blit's one quad). */
static int fold_covers(const ch360_item* it, const ch360_view* v) {
    return it->x0 <= 0.0f && it->y0 <= 0.0f && it->x1 >= (float)v->tw && it->y1 >= (float)v->th;
}

static int fold_averages(unsigned mode) {
    return mode == 11u || mode == 12u || mode == 13u || mode == 18u;   /* Copy, Down4, Blur7, Blur7Alpha (Effects.cs) */
}

/* The frame's views as the platform gets them (draw_views itself when nothing folds). */
static const ch360_view* fold_frame(unsigned* count) {
    unsigned char zero[FOLD_RTS], read_since[FOLD_RTS];
    int writer[FOLD_RTS];            /* the kept view that last wrote the target, -1 none this frame */
    unsigned writer_bits[FOLD_RTS];  /* what it wrote: its clear bits, 1 for a pass over all of it */
    unsigned char writer_clear_only[FOLD_RTS];
    unsigned v, i, kept = 0, any = 0;
    *count = draw_view_count;
    if (!ch360_fold_effects || draw_view_count == 0) return draw_views;
    if (!draw_grow((void**)&fold_views, &fold_views_cap, draw_view_count, sizeof(ch360_view))) return draw_views;
    memset(zero, 0, sizeof(zero));
    memset(read_since, 0, sizeof(read_since));
    memset(writer_clear_only, 0, sizeof(writer_clear_only));
    for (i = 0; i < FOLD_RTS; i++) writer[i] = -1;
    for (v = 0; v < draw_view_count; v++) {
        ch360_view* w = &fold_views[kept];
        const ch360_item* first;
        int t, s;
        unsigned bits = 0;
        *w = draw_views[v];
        t = w->target != 0 && w->target < FOLD_RTS ? (int)w->target : -1;
        first = w->items > 0 && w->first_item < draw_item_count ? &draw_items[w->first_item] : NULL;
        /* a pass that averages a target known to be transparent black */
        if (t >= 0 && w->items == 1 && first != NULL && fold_whole(w) && !(w->clear & 4u) && fold_averages(first->mode) && fold_covers(first, w) &&
            first->textures[0] < 0 && first->textures[1] < 0 && first->stencil == 0 && (s = fold_rt(first->texture)) >= 0 && zero[s]) {
            any = 1;
            if (zero[t]) {
                fold_dropped++;
                continue;                       /* already what it would write */
            }
            w->clear = 1u;
            w->r = w->g = w->b = w->a = 0.0f;
            w->items = 0;
            fold_cleared++;
            bits = 1u;
        } else {
            /* what its items read (a sprite over a transparent black target: left out) */
            unsigned end = w->first_item + w->items;
            for (i = w->first_item; i < end && i < draw_item_count; i++) {
                ch360_item* it = &draw_items[i];
                int r0 = fold_rt(it->texture), r1 = fold_rt(it->textures[0]), r2 = fold_rt(it->textures[1]);
                if (r0 >= 0 && zero[r0] && it->mode == 0u && it->stencil == 0u && r1 < 0 && r2 < 0 && it->count > 0) {
                    it->count = 0;              /* the platform skips an item without vertices */
                    fold_items++;
                    any = 1;
                    continue;
                }
                if (r0 >= 0) read_since[r0] = 1;
                if (r1 >= 0) read_since[r1] = 1;
                if (r2 >= 0) read_since[r2] = 1;
            }
            if (t >= 0) {
                if (w->clear & 4u) read_since[t] = 1;   /* goes on from what the target held */
                if (fold_whole(w) && !(w->clear & 4u)) {
                    if (w->clear & 1u) bits = w->clear & 3u;
                    else if (w->items == 1 && first != NULL && first->mode >= 11u && first->mode <= 18u && fold_covers(first, w)) bits = 1u;   /* a blit replaces it */
                }
            }
        }
        if (t >= 0) {
            /* the last clear-only writer, overwritten before anything read it */
            if (bits != 0 && writer[t] >= 0 && writer_clear_only[t] && !read_since[t] && (writer_bits[t] & ~bits) == 0) {
                unsigned k;
                for (k = (unsigned)writer[t]; k + 1 < kept + 1; k++) fold_views[k] = fold_views[k + 1];
                for (i = 0; i < FOLD_RTS; i++)
                    if (writer[i] > writer[t]) writer[i]--;
                kept--;
                w = &fold_views[kept];
                fold_dropped++;
                any = 1;
            }
            if (bits != 0 || w->items > 0 || (w->clear & 1u)) {
                int clear_only = w->items == 0 && (w->clear & 1u) && fold_whole(w) && !(w->clear & 4u);
                zero[t] = (unsigned char)(clear_only && w->r == 0.0f && w->g == 0.0f && w->b == 0.0f && w->a == 0.0f);
                writer[t] = (int)kept;
                writer_bits[t] = bits ? bits : (w->clear & 3u);
                writer_clear_only[t] = (unsigned char)clear_only;
                read_since[t] = 0;
            }
        }
        kept++;
    }
    if (any) fold_frames++;
    *count = kept;
    return fold_views;
}

void i5_ch3608_drawX5Fend(wasmModuleInstance* inst) {
    ch360_frame f;
    (void)inst;
    f.loading = draw_loading;
    f.views = fold_frame(&f.view_count);
    f.items = draw_items;
    f.item_count = draw_item_count;
    f.vertices = draw_verts;
    f.vertex_count = draw_vert_count;
    f.build_ms = draw_build_ms;
    ch360_plat_draw(&f);
    if (f.loading == 0) learn_drawn();
    /* the shifts cover every item (the submit path's items have none: none moved) */
    if (draw_tween && f.loading == 0 && draw_grow((void**)&draw_shift, &draw_shift_cap, draw_item_count + 1, 2 * sizeof(float))) {
        unsigned i;
        for (i = 0; i < draw_item_count; i++) {   /* half a frame's move: the vblank between this frame and the next */
            draw_shift[2 * i] *= 0.5f;
            draw_shift[2 * i + 1] *= 0.5f;
        }
        ch360_plat_draw_tween(&f, draw_shift);
    }
    if (draw_shift != NULL && draw_shift_cap > 0) memset(draw_shift, 0, (size_t)draw_shift_cap * 2 * sizeof(float));
    tween_cur ^= 1u;
    tween_gen[tween_cur]++;
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
        draw_view_add(xywh, le32(d + p + 16), lef(d + p + 20), lef(d + p + 24), lef(d + p + 28), 0, 0, 0, 1.0f);
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
            it->stencil = 0;
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
        name_plat(p, name, 1);
    }
}

/* An atlas the game loaded: its pages (their sprite pages) read ahead. */
void i5_ch36016_textureX5Fprefetch(wasmModuleInstance* inst, U32 id) {
    int p, page;
    (void)inst;
    p = plat_id((int)id);
    if (p < 0) return;
    page = page_of_plat[p] - 1;
    if (page >= 0 && rm_pages[page].count > 0) remap_prefetch(&rm_pages[page], 1);
    else ch360_plat_texture_prefetch(p, 0);
}

/* The mixed frames the host's output still has to play (-1: none that plays in real time). */
U32 i5_ch36012_audioX5Fqueued(wasmModuleInstance* inst) {
    (void)inst;
    return (U32)ch360_plat_audio_queued();
}

/* The textures the last frame waited for (ConsoleHost's level hold ends at none). */
U32 i5_ch36015_texturesX5Fwaited(wasmModuleInstance* inst) {
    (void)inst;
    return (U32)ch360_plat_textures_waited();
}

/* The textures still coming (ConsoleHost's boot hold waits for none). */
U32 i5_ch36016_texturesX5Fpending(wasmModuleInstance* inst) {
    (void)inst;
    return (U32)ch360_plat_textures_pending();
}

/* A new scene's read-ahead begins (1) or ends (0): RenderList.ReadAheadLoad. */
void i5_ch36014_texturesX5Fscene(wasmModuleInstance* inst, U32 begin) {
    (void)inst;
    ch360_plat_textures_scene((int)begin);
}

/* A sprite the game expects to draw soon (near the view, a frame of a clip on screen): the page holding its
 * uv box (u0, v0)-(u1, v1) on its atlas page read ahead (the host decides by memory), not all the atlas
 * page's sprite pages. */
void i5_ch36013_textureX5Fahead(wasmModuleInstance* inst, U32 id, F32 u0, F32 v0, F32 u1, F32 v1) {
    int p, page, bi;
    (void)inst;
    p = plat_id((int)id);
    if (p < 0) return;
    page = page_of_plat[p] - 1;
    if (page < 0 || rm_pages[page].count == 0) {
        ch360_plat_texture_prefetch(p, 1);
        return;
    }
    bi = remap_find(&rm_pages[page], u0, v0, u1, v1);
    if (bi >= 0) {
        int t = remap_target_id(&rm_pages[page], &rm_boxes[bi]);
        if (t >= 0) ch360_plat_texture_prefetch(t, 1);
    }
}

/* A level's sprite (RenderList.FillAhead, under the hourglass): the stored bytes of the page holding its uv box kept
 * by the host. The page is named without its siblings' guesses (remap_target_id's), which would fill the host's
 * block with every page of every atlas the level touches. */
void i5_ch36013_textureX5Fguess(wasmModuleInstance* inst, U32 id, F32 u0, F32 v0, F32 u1, F32 v1) {
    int p, page, bi;
    (void)inst;
    p = plat_id((int)id);
    if (p < 0) return;
    page = page_of_plat[p] - 1;
    if (page < 0 || rm_pages[page].count == 0) {
        ch360_plat_texture_guess(p);
        return;
    }
    bi = remap_find(&rm_pages[page], u0, v0, u1, v1);
    if (bi >= 0) {
        const remap_box* b = &rm_boxes[bi];
        int t = b->target < rm_target_count ? rm_plat[b->target] : -1;
        if (t < 0 && b->target < rm_target_count) {
            t = rm_plat[b->target] = new_plat();
            if (t >= 0) name_plat(t, rm_strings + rm_names[b->target], 0);
        }
        if (t >= 0) ch360_plat_texture_guess(t);
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
int ch360_quiet = 0, ch360_verbose = 0, ch360_nolog = 0;

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
    /* The GC keeps its own small gen0 budget (frequent, cheap gen0 GCs): an
     * 8 MB gen0 budget (DOTNET_GCgen0size=800000; hex, no 0x) made every GC a
     * gen1 and half of them gen2: 16.4 s of pauses over the native bot run
     * against 3.4 s (round 10). The parsed-tree cache is small (4 MB
     * estimated): its boxed values were most of the heap's objects, which
     * every gen2 marks and moves. With that, conserve-memory mode (7 until
     * round 19) only added gen2s: off, the Forest Follies run had 10 gen2s
     * against 20, a third less pause, and its linear memory peaked 16 MB
     * lower (round 19). A 1 MB gen0 budget (2026-11-05, with assets
     * unloaded): the console route of nine scene loads (host_native) ran 476
     * gen0 GCs against 1,809 and paused 5.4 s against 5.9 (the map's visits
     * 160-259 ms against 168-337); 4 MB added gen2 work to the loads. The
     * large object heap compacted in full collections (GCLOHCompact: the
     * converted files' bytes are large objects, loaded and let go each scene):
     * the long route's linear memory peaked at 184 MB against 200, pauses as
     * before; smaller segments (GCSegmentSize 4-12 MB) saved 16-40 MB more but
     * doubled the gen2s and their pause (2026-11-05). A hard limit (GCHeapHardLimit
     * 160 MB) or kept segments (GCRetainVM): the nineteenth run's route peaked at
     * 199-200 or 215-216 MB from run to run with either setting and without
     * (scene builds sliced by the clock), so neither is set (2026-11-05).
     * The converted files: none held whole (files under 2 MB were, 15-21 MB
     * of the heap in every scene) and 4 MB of their blocks cached (16 MB): on
     * the PC route the objects read in play were the same (71 against 67),
     * the drive's block reads in play 10 against 8 beyond the level's first
     * frame (which the level hold covers); loads read 18% more objects. The
     * heap census (2026-11-09) had 24-29 MB of file bytes and index and 7.5-8.5
     * MB of blocks on the map, of a 64-76 MB heap. */
    static const char* const env[9] = { "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1", "DOTNET_gcServer=0", "DOTNET_GCConserveMemory=0",
                                        "CH360_TREE_BUDGET_MB=4", "DOTNET_GCgen0size=100000", "DOTNET_GCLOHCompact=1",
                                        "CH360_WHOLE_FILE_KB=0", "CH360_BLOCK_BUDGET_MB=4",
                                        "CH360_PROFILE=1" };   /* the last: 0 in a quiet run, 2 in a profile run */
    srt_config cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.root_dir = root_dir;
    snprintf(learn_root, sizeof(learn_root), "%s", root_dir ? root_dir : ".");
    ch360_ogg_root(root_dir);
    cfg.argc = argc;
    cfg.argv = argv;
    cfg.envc = (int)(sizeof(env) / sizeof(env[0]));
    cfg.envp = env;
    if (ch360_quiet || ch360_verbose) {   /* (set after env: the twenty-ninth build set it before, and env won) */
        static const char* list[9];
        memcpy(list, env, sizeof(env));
        list[8] = ch360_quiet ? "CH360_PROFILE=0" : "CH360_PROFILE=2";
        cfg.envp = list;
    }
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
    ch360_player_data_default(root_dir);
    m6_playerInstantiate(&instance, NULL);
    ch360_player_data_close();
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
    learn_save();   /* the scene played last: its learned list written too (a PC run that ends in a level) */
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

/* ---- the collector's memory ----
 * The .NET collector reserves its heap in blocks (16 MB each for small objects, large ones and pinned ones at start,
 * more as the heap grows) and commits them piece by piece; in the wasm build "reserve" is a malloc of the whole block
 * (linear memory grown for all of it), "commit" does nothing and "decommit" zeroes. On the console every grown page
 * is physical memory, so the player held the blocks whole (20 -> 68 MB before its first object) and every load's
 * peak for good: 152 MB by the forty-first run's fifth level with a 56 MB heap in play, and the textures had what was
 * left (Glumstone's pages read again 2,500 times in a fight).
 * tools/build_managed.py (hook_gc_memory) points the translated GCToOSInterface::VirtualReserve, VirtualCommit,
 * VirtualDecommit and VirtualRelease here: a reserved block's pages are given back to the platform until the
 * collector commits them, and again when it decommits them. Pages are GC_PAGE (64 KB: wasm's, and no smaller than
 * the console's); a piece of a page the collector decommits is zeroed as before. A block released is whole again
 * first (malloc's lists write in it), or kept here for the collector's next. ch360_gc_memory_off (the console's nodecommit.txt, CH360_GC_DECOMMIT=0 on the
 * PC) leaves everything as it was. */
#define GC_PAGE 65536u
#define GC_BLOCKS 256
int ch360_gc_memory_off;
static unsigned char* gc_out;            /* a page of linear memory is given back to the platform */
static unsigned gc_out_pages, gc_out_cap;
static struct { U32 p, n; int kept; } gc_block[GC_BLOCKS];   /* kept: let go by the collector, held for its next */
static unsigned gc_reserved_kb, gc_kept_kb, gc_fails;

unsigned ch360_gc_memory(unsigned* reserved_kb, unsigned* fails) {
    if (reserved_kb != NULL) *reserved_kb = gc_reserved_kb;
    if (fails != NULL) *fails = gc_fails;
    return gc_out_pages * (GC_PAGE / 1024u);
}

static int gc_map(U32 end) {
    unsigned need = (unsigned)(((U64)end + GC_PAGE - 1) / GC_PAGE);
    if (need > gc_out_cap) {
        unsigned cap = gc_out_cap ? gc_out_cap : 4096;
        unsigned char* b;
        while (cap < need) cap *= 2;
        b = (unsigned char*)realloc(gc_out, cap);
        if (b == NULL) return 0;
        memset(b + gc_out_cap, 0, cap - gc_out_cap);
        gc_out = b;
        gc_out_cap = cap;
    }
    return 1;
}

/* Pages wholly inside [p, p + n) given back. */
static void gc_give(U32 p, U32 n) {
    U32 a = (p + GC_PAGE - 1) / GC_PAGE, b = (U32)(((U64)p + n) / GC_PAGE), k, run;
    if (ch360_gc_memory_off || b <= a || !gc_map(b * GC_PAGE)) return;
    for (k = a; k < b; k = run) {
        for (run = k; run < b && !gc_out[run]; run++) {}
        if (run > k) {
            if (ch360_plat_memory_decommit(srt_mem_ptr(k * GC_PAGE), (size_t)(run - k) * GC_PAGE) == 0) {
                memset(gc_out + k, 1, run - k);
                gc_out_pages += run - k;
            } else {
                gc_fails++;
                memset(srt_mem_ptr(k * GC_PAGE), 0, (size_t)(run - k) * GC_PAGE);   /* as before */
            }
        }
        for (; run < b && gc_out[run]; run++) {}
    }
}

/* Every page [p, p + n) touches is the player's again (zeroed by the platform). 0 if the platform has none. */
static int gc_take(U32 p, U32 n) {
    U32 a = p / GC_PAGE, b = (U32)(((U64)p + n + GC_PAGE - 1) / GC_PAGE), k, run;
    if (n == 0 || gc_out == NULL) return 1;
    if (b > gc_out_cap) b = gc_out_cap;
    for (k = a; k < b; k = run) {
        for (run = k; run < b && gc_out[run]; run++) {}
        if (run > k) {
            if (ch360_plat_memory_commit(srt_mem_ptr(k * GC_PAGE), (size_t)(run - k) * GC_PAGE) != 0) {
                gc_fails++;
                return 0;
            }
            memset(gc_out + k, 0, run - k);
            gc_out_pages -= run - k;
        }
        for (; run < b && !gc_out[run]; run++) {}
    }
    return 1;
}

void ch360_gc_reserved(wasmModuleInstance* inst, U32 p, U32 n) {
    unsigned i;
    (void)inst;
    if (p == 0 || n == 0) return;
    for (i = 0; i < GC_BLOCKS && gc_block[i].p != 0; i++) {}
    if (i == GC_BLOCKS) return;   /* not tracked: stays whole */
    gc_block[i].p = p;
    gc_block[i].n = n;
    gc_reserved_kb += n / 1024u;
    gc_give(p, n);
}

U32 ch360_gc_commit(wasmModuleInstance* inst, U32 p, U32 n) {
    (void)inst;
    return gc_take(p, n) ? 1u : 0u;
}

U32 ch360_gc_decommit(wasmModuleInstance* inst, U32 p, U32 n) {
    U32 a, b, end;
    unsigned i;
    (void)inst;
    if (n == 0) return 1;
    end = p + n;
    /* only inside a block this file tracks: the rest is zeroed, as it was */
    for (i = 0; i < GC_BLOCKS && !(gc_block[i].p != 0 && !gc_block[i].kept && p >= gc_block[i].p && end <= gc_block[i].p + gc_block[i].n); i++) {}
    a = (p + GC_PAGE - 1) / GC_PAGE * GC_PAGE;
    b = end / GC_PAGE * GC_PAGE;
    if (i == GC_BLOCKS || ch360_gc_memory_off || b <= a) {
        memset(srt_mem_ptr(p), 0, n);
        return 1;
    }
    if (a > p) memset(srt_mem_ptr(p), 0, a - p);
    if (end > b) memset(srt_mem_ptr(b), 0, end - b);
    gc_give(a, b - a);
    return 1;
}

/* A block the collector lets go: kept here with its pages given back, for its next reserve of that size (1), where
 * free() would write its lists in it and hold it whole until something mallocs it again. One that doesn't lie on
 * whole pages is made whole and freed as before (0). */
U32 ch360_gc_release(wasmModuleInstance* inst, U32 p, U32 n) {
    unsigned i;
    (void)inst;
    (void)n;
    for (i = 0; i < GC_BLOCKS; i++)
        if (gc_block[i].p == p && p != 0 && !gc_block[i].kept) {
            if (!ch360_gc_memory_off && p % GC_PAGE == 0 && gc_block[i].n % GC_PAGE == 0) {
                gc_give(p, gc_block[i].n);
                gc_block[i].kept = 1;
                gc_reserved_kb -= gc_block[i].n / 1024u;
                gc_kept_kb += gc_block[i].n / 1024u;
                return 1;
            }
            if (!gc_take(gc_block[i].p, gc_block[i].n)) {
                static const char msg[] = "memory: a released block's pages could not be committed again";
                ch360_plat_log(msg, sizeof(msg) - 1);
                abort();
            }
            gc_reserved_kb -= gc_block[i].n / 1024u;
            gc_block[i].p = gc_block[i].n = 0;
            return 0;
        }
    return 0;
}

/* A kept block of n bytes for the collector's reserve (aligned as it asks), or 0. */
U32 ch360_gc_reuse(wasmModuleInstance* inst, U32 n, U32 align) {
    unsigned i;
    (void)inst;
    if (align < GC_PAGE) align = GC_PAGE;
    for (i = 0; i < GC_BLOCKS; i++)
        if (gc_block[i].kept && gc_block[i].n == n && gc_block[i].p % align == 0) {
            gc_block[i].kept = 0;
            gc_reserved_kb += n / 1024u;
            gc_kept_kb -= n / 1024u;
            gc_give(gc_block[i].p, n);   /* all of it still out (a failed give left some in) */
            return gc_block[i].p;
        }
    return 0;
}
