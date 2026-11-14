/* The console host boundary (C89): what the translated player calls
 * (ch360_imports.c turns its "ch360" wasm imports into these), and what a
 * platform layer implements: host_native.c on the PC (headless checks of
 * the translated C), xsdk/main.cpp on the Xbox 360.
 *
 * The frame's draw list (ch360_frame, below) is written by the glue in the
 * host's own byte order, its vertices already in the GPU's layout: from
 * ConsoleHost's draw_view/draw_item calls, or, with CH360_DRAW=managed
 * (a PC check), from the little-endian list ConsoleHost writes itself:
 *   u32 'CHF1', u32 views
 *   view: f32 x y w h, u32 clear, f32 r g b a, u32 items
 *   item: i32 texture (-1 none), u32 mode, f32 blend r g b a, u32 vertices
 *         vertex: f32 x y u v, u8 r g b a      (a triangle list; the
 *         colour already times the renderer colour for modes 0 and 5)
 * Positions are screen pixels, origin bottom-left (Unity's); uv has v = 0
 * at a texture's first stored row. Modes: 0 texel * colour, 1 screen,
 * 2 multiply, 3 overlay (blend colour = renderer colour), 5 SDF text,
 * 6 light ray (the item's extra textures and floats), 7 a sprite mask's
 * stencil write (item.stencil). */
#ifndef CH360_HOST_H
#define CH360_HOST_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct ch360_pad {
    unsigned short buttons; /* XInput bits */
    short lx, ly, rx, ry;
    unsigned char lt, rt;
} ch360_pad;

/* ---- the platform layer ---- */

/* Start of a frame: 0 ends the game loop. */
int ch360_plat_frame_begin(void);
/* The host's tallies since the last call (the player's per-scene summaries), up to
 * CH360_HOST_STATS u32s: frames, GPU-timed frames, GPU ms in all, render textures,
 * effects, screen, fence wait, present, host part (the frame's work before present),
 * texture loads, uploads (those times ms x 100), textures loaded, fill on the screen
 * and in render textures (screens x 100), draws, passes, audio underruns, texture MB
 * now, free MB now, slow host parts, textures loaded that weren't read ahead, textures
 * loaded that had been let go before. Zero where a host has no such thing. */
#define CH360_HOST_STATS 22
void ch360_plat_host_stats(unsigned* out, int n);
/* Fills *out for pad 0..3; 0 if it is not connected. */
int ch360_plat_pad(int index, ch360_pad* out);
/* A vertex as the GPU reads it (24 bytes): position in screen pixels from
 * the bottom left, uv, the vertex colour and the item's blend colour, 8 bits
 * each by address (r g b a). The shaders take texel * colour * blend for
 * modes 0 and 5; for the blend modes (1-3) blend.rgb is the renderer colour
 * and the alpha is colour.a * blend.a. */
typedef struct ch360_vertex {
    float x, y, u, v;
    unsigned char color[4], blend[4];
} ch360_vertex;

/* Render textures (what a camera or a blit drew this frame) as textures:
 * CH360_RT_BASE + the engine's number for one (Effects.TargetId). */
#define CH360_RT_BASE 0x40000000

/* An item: its texture (-1 none), mode, vertices (a triangle list) and their
 * screen box (x0 > x1 when not worked out: drawn wherever it lands).
 * Modes (Cuphead360.Engine Effects): 0 sprite, 1 screen, 2 multiply, 3
 * overlay, 5 SDF text, 6 light ray, 7 sprite mask; 8 the Knight's glow, 9 the
 * Bishop's darkness, 10 the blur sprite (premultiplied over); blits, one quad
 * replacing its target: 11 copy, 12 four-tap downsample, 13 seven-tap blur,
 * 14-16 the blur with gamma (plain, two-strip, black and white), 17 the film
 * grain and colour fringes, 18 the seven-tap blur with its alpha blurred; 19
 * Sprites/Color/Overlay (premultiplied over);
 * 20-34 the game's alpha masks, stencil masks and Unity's Screen and Hard
 * Light blend modes, each drawn in its passes (params 0 _Amount, or 1 for
 * Hard Light), 35 MonochromeNoBlack; 36-38 Honey, IceCubes and Unity's
 * Overlay blend mode (textures[0] the grab of the target behind them). */
typedef struct ch360_item {
    int texture;
    unsigned mode, first, count;
    float x0, y0, x1, y1;
    /* mode 6 (Cuphead/Sprites/LightRay): its shadow and sky glow textures
     * (-1 none) and params 0-2 its material's t, textureWidth, textureSpeed;
     * mode 17: textures[0] the film grain's overlay; modes 8-17: the effect's
     * values (Effects.cs) */
    int textures[2];
    float params[12];
    /* sprite masks: 1 adds one to the stencil, 2 takes one away (mode 7, the
     * mask's sprite: texel alpha at or above the blend alpha, no colour); 3
     * draws where the stencil is 1 or more, 4 where it is 0; 0 no stencil */
    unsigned stencil;
} ch360_item;

/* A view: its rect in its target's pixels (from the bottom left), clear (1
 * the colour, 2 depth and stencil, 4 go on in the target with what it held:
 * after a grab's copy) and colour, its items; the target: 0 the
 * screen, else a render texture's number (CH360_RT_BASE + it as a texture),
 * tw x th pixels, cleared with alpha a. */
typedef struct ch360_view {
    float x, y, w, h;
    unsigned clear;
    float r, g, b;
    unsigned first_item, items;
    unsigned target, tw, th;
    float a;
} ch360_view;

typedef struct ch360_frame {
    const ch360_view* views;
    unsigned view_count;
    const ch360_item* items;
    unsigned item_count;
    const ch360_vertex* vertices;
    unsigned vertex_count;
    double build_ms; /* the glue's time writing it */
    unsigned loading; /* the game's loading screen, its hourglass faded in (the host may draw it again while the game is held up):
                         0 if not, else 0x80000000 | the hourglass's cycle in ms << 16 | where it is in its cycle, ms */
} ch360_frame;

/* The frame's draw list (valid during the call). */
void ch360_plat_draw(const ch360_frame* frame);
/* The locked 30 (ConsoleHost.LockedThirty): whether the platform draws a frame in between this one and the next (on:
 * the game asks; 0: it doesn't, or it was turned off), and that frame: the one just drawn with each item's vertices
 * moved by shifts (x, y per item, in pixels: half the item's move since the last frame), drawn at the vblank between
 * this frame and the next. */
int ch360_plat_tween(int on);
void ch360_plat_draw_tween(const ch360_frame* frame, const float* shifts);
/* Texture id is the converted texture <key> (GameData/ctex/<key>.ctx). */
void ch360_plat_texture_file(int id, const char* key);
/* A level's page whose stored bytes the host keeps in its memory, not decoded (RenderList.FillAhead, under the
 * hourglass): drawn later, it decodes from there instead of the drive. */
void ch360_plat_texture_guess(int id);
/* Texture id is likely drawn soon: read it ahead if memory allows (never waited for). wanted: a sprite near
 * the view or a frame of a clip on screen (room is made from textures not drawn lately); else a guess (an
 * atlas the game loaded, the other pages of one drawn): only its stored bytes are kept, and only with room. */
void ch360_plat_texture_prefetch(int id, int wanted);
/* Textures being read or waiting to be (the loader's jobs and the read-ahead asked for): the boot's hold. */
int ch360_plat_textures_pending(void);
/* A new scene's read-ahead begins (1: what the last frames didn't draw counts as old, room made from it) or ends
 * (0: what it asked for is read). */
void ch360_plat_textures_scene(int begin);
/* Textures the last frame drew that weren't loaded yet (it waited for them): the level hold's end. */
int ch360_plat_textures_waited(void);
/* Texture id's pixels (run-time textures: font atlases), BGRA rows. */
void ch360_plat_texture_pixels(int id, int width, int height, const unsigned char* bgra);
/* Mixed audio: frames of 48 kHz stereo, interleaved little-endian floats
 * (unclamped; the platform clamps where it hands them on). */
void ch360_plat_audio(const unsigned char* f32le, int frames);
/* The same from the host's own mixer (ch360_ogg.c): native floats. */
void ch360_plat_audio_native(const float* lr, int frames);
/* Mixed frames the platform holds that haven't been played yet (-1: no output that plays in real time):
 * the player mixes enough each frame to keep a little ahead (ConsoleHost.AudioLead). */
int ch360_plat_audio_queued(void);
/* The player's stdout and stderr. */
void ch360_plat_log(const char* text, size_t len);

/* ---- the glue ---- */

/* Instantiates the translated player under the wasm runtime with root_dir
 * mounted as "/", then runs its main on a guest thread (stack_mb of native
 * stack, Xbox 360 hardware thread hw_thread or -1) and waits for it. The
 * platform layer's functions are called from that thread. Returns the
 * exit code (>= 1000: a trap). */
int ch360_run(const char* root_dir, int argc, const char* const* argv, unsigned memory_mb, unsigned stack_mb,
              int hw_thread);
/* ch360_run in two steps: the runtime and the module's memory first (on the
 * console before anything else allocates: xsdk's heap takes the free
 * address space at its first growth), then the guest thread. */
int ch360_prepare(const char* root_dir, int argc, const char* const* argv, unsigned memory_mb);
int ch360_start(unsigned stack_mb, int hw_thread);
/* The folder the guest's "/" is (ch360_ogg.c opens clips under it). */
void ch360_ogg_root(const char* root_dir);
/* The audio's memory: decoded clips, their KB and peak KB, open and kept compressed KB, stream buffer KB,
 * open decoders (ch360_ogg.c). */
void ch360_audio_memory(unsigned out[7]);
/* The platform's audio thread, its output running low before the game's next mix: the last mix's voices
 * mixed on for frames more, straight to ch360_plat_audio_native; the frames, or 0 (the game is mixing,
 * or nothing plays). */
int ch360_mix_extend(int frames);
/* All the frames ch360_mix_extend has mixed on. */
unsigned long long ch360_mix_carried(void);
/* Staged names (tools/stage_console.py): on the console, converted textures
 * and clips are <folder>/XX/YYYYYYYY.CTX/.OGG, XX the top byte and YYYYYYYY
 * the low word of FNV-1a 64 of the file's key, so no folder holds thousands
 * of long names (FAT32 scans a folder from the start for every open). The
 * console host sets ch360_staged_names; the PC reads the loose files. */
extern int ch360_staged_names;
/* How much a console run measures (the console host sets them from files next to default.xex). By
 * default the player times its phases (CH360_PROFILE=1) and the host keeps its tallies; per-script
 * timing and the host's per-texture and slow-part lines cost the twenty-eighth run a third of its
 * hitches (quiet 17.7 per 1,000 play frames against 26.5). profile.txt: all of it (CH360_PROFILE=2,
 * ch360_verbose); quiet.txt: the phases off too (CH360_PROFILE=0); nolog.txt: as quiet, and nothing
 * written to the log after its first lines (ch360_nolog: to judge by feel what logging at all costs). */
extern int ch360_quiet, ch360_verbose, ch360_nolog;
/* "XX/YYYYYYYY" and a NUL (12 bytes) for the len bytes of key. */
void ch360_staged_name(const char* key, size_t len, char* out);
/* FNV-1a 64 of key as two words. */
void ch360_fnv64(const char* key, size_t len, unsigned* hi, unsigned* lo);

/* GameData packs (ch360_pack.c, tools/stage_console.py): <root>/GameData/
 * <name>0.PAK, 1, ... opened once, entries found by key and read at any
 * offset (LZ4 entries decoded). Guest thread only. */
typedef struct ch360_pack_entry {
    int file;
    unsigned offset, stored, size;
} ch360_pack_entry;
/* The pack (>= 0), or -1 when there is no <name>0.PAK. */
int ch360_pack_open(const char* root, const char* name);
/* 1 and *out for the entry with this key, 0 if there is none. */
int ch360_pack_find(int pack, const char* key, size_t len, ch360_pack_entry* out);
/* A reader's own buffers and chunk cache: one per thread that reads packs (the
 * game thread uses the default one through ch360_pack_read; the host's texture
 * loader thread has its own). Packs are only ever read from one thread each. */
typedef struct ch360_pack_reader ch360_pack_reader;
ch360_pack_reader* ch360_pack_reader_new(void);
int ch360_pack_read_with(ch360_pack_reader* r, const ch360_pack_entry* e, unsigned from, void* dst, unsigned n);
/* The texture loader's reads through ch360_pack_read_with: drive time, bytes, decode time (a clock the host sets). */
extern double (*ch360_pack_clock)(void);
extern double ch360_pack_read_ms, ch360_pack_decode_ms;
extern unsigned long long ch360_pack_read_bytes;
/* The entry's bytes as stored (e->stored of them: its LZ4 chunks and their table) into dst; 0 on success. */
int ch360_pack_read_stored(const ch360_pack_entry* e, void* dst);
/* Bytes from..from+n of the entry out of a copy of its stored bytes (ch360_pack_read_stored) in memory,
 * the reader's chunk buffer for a chunk taken in part; 0 on success. */
int ch360_pack_decode_stored(ch360_pack_reader* r, const ch360_pack_entry* e, const unsigned char* stored, unsigned from, void* dst, unsigned n);
/* Bytes [from, from + n) of the entry into dst: 0, or -1 on a bad read. */
int ch360_pack_read(const ch360_pack_entry* e, unsigned from, void* dst, unsigned n);
int ch360_lz4_decode(const unsigned char* src, unsigned srclen, unsigned char* dst, unsigned dstlen);
/* The module's data segments (ch360_pack.c): the file they are read from at start (default
 * <root>/GameData/PLAYER.DAT, set by ch360_prepare unless the host named one), segment index's stored bytes
 * decoded into dst (0, or -1 with a log line), and the file closed once the module is made. */
void ch360_player_data_at(const char* path);
void ch360_player_data_default(const char* root);
int ch360_player_data(unsigned index, unsigned stored, unsigned fnv, unsigned char* dst, unsigned size);
void ch360_player_data_close(void);
/* The textures' pack, opened by ch360_prepare when the root has CTEX0.PAK
 * (-1 otherwise: the PC reads GameData/ctex's loose files). */
extern int ch360_ctex_pack;
/* Items drawn from an atlas page that no sprite rectangle held (kept on the halved page). */
unsigned ch360_remap_misses(void);
/* The sprite table's atlas pages (and its sprites' boxes, sprite pages). */
unsigned ch360_remap_counts(unsigned* boxes, unsigned* targets);
/* The player's linear memory, in KB (committed as it grows). */
unsigned ch360_memory_kb(void);
/* Moves the player's buffered stdout into the log now. One thread drains:
 * each frame unless the host clears ch360_frame_drain and drains itself. */
void ch360_drain_output(void);
/* What the clip reader's thread is doing (the heartbeat's, a stall's report). */
extern volatile const char* ch360_clip_reader_stage;
extern int ch360_frame_drain;

/* Little-endian readers for the draw list. */
static unsigned ch360_u32(const unsigned char* p) {
    return (unsigned)p[0] | ((unsigned)p[1] << 8) | ((unsigned)p[2] << 16) | ((unsigned)p[3] << 24);
}
static float ch360_f32(const unsigned char* p) {
    union { unsigned u; float f; } v;
    v.u = ch360_u32(p);
    return v.f;
}

#ifdef __cplusplus
}
#endif

#endif
