/* The console host boundary (C89): what the translated player calls
 * (ch360_imports.c turns its "ch360" wasm imports into these), and what a
 * platform layer implements: host_native.c on the PC (headless checks of
 * the translated C), xsdk/main.cpp on the Xbox 360.
 *
 * The draw list is the managed ConsoleHost's format (little-endian):
 *   u32 'CHF1', u32 views
 *   view: f32 x y w h, u32 clear, f32 r g b a, u32 items
 *   item: i32 texture (-1 none), u32 mode, f32 blend r g b a, u32 vertices
 *         vertex: f32 x y u v, u8 r g b a      (a triangle list)
 * Positions are screen pixels, origin bottom-left (Unity's); uv has v = 0
 * at a texture's first stored row. Modes: 0 texel * colour, 1 screen,
 * 2 multiply, 3 overlay (blend colour = renderer colour), 5 SDF text. */
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
/* Fills *out for pad 0..3; 0 if it is not connected. */
int ch360_plat_pad(int index, ch360_pad* out);
/* The frame's draw list (bytes in guest memory, valid during the call). */
void ch360_plat_submit(const unsigned char* data, unsigned len);
/* Texture id is the converted texture <key> (GameData/ctex/<key>.ctx). */
void ch360_plat_texture_file(int id, const char* key);
/* Texture id's pixels (run-time textures: font atlases), BGRA rows. */
void ch360_plat_texture_pixels(int id, int width, int height, const unsigned char* bgra);
/* Mixed audio: frames of 48 kHz stereo 16-bit little-endian samples. */
void ch360_plat_audio(const unsigned char* s16le, int frames);
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
/* The player's linear memory, in KB (committed as it grows). */
unsigned ch360_memory_kb(void);
/* Moves the player's buffered stdout into the log now. One thread drains:
 * each frame unless the host clears ch360_frame_drain and drains itself. */
void ch360_drain_output(void);
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
