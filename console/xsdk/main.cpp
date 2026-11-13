// Cuphead360 on the open xSDK (xsdk + vulkan4xenon): the console host. The
// player (the user's own game code and the engine, translated to C) runs on a
// guest thread and calls the platform layer below each frame (ch360_host.h):
// the pads, the frame's draw list, textures and the mixed audio.
//
//   game:\default.xex
//   game:\GameData\objects\  converted object files (tools/convert_objects.py)
//   game:\GameData\ctex\     console textures (tools/convert_console.py)
//   game:\GameData\audio\    clips (tools/convert_audio.py)
//   game:\GameData\fonts\    arial.ttf (the PC's own, copied by the staging step)
//   game:\save\              the game's saves and settings
//   game:\cuphead360.txt     log
//   game:\cuphead360_test.xex, cuphead360_test.txt: the 64-bit test build (CH360_TEST64) and its log
//
// Pad: the game's own controls. Back writes the log now; hold Back + Start
// to quit to the dashboard.
#include <fcntl.h>
#include <malloc.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

#include <algorithm>
#include <deque>
#include <map>
#include <new>
#include <string>
#include <vector>

#include <vulkan/vulkan.h>
#include <vulkan4xenon.h>
#include <xsdk/audio.h>
#include <xsdk/kernel.h>
#include <xsdk/present.h>
#include <xsdk/thread.h>

#include "../ch360_host.h"
extern "C" {
#include "ch360_shaders.h"
}

#undef VK_NULL_HANDLE
#define VK_NULL_HANDLE 0

#ifndef CH360_GIT_COMMIT
#define CH360_GIT_COMMIT "unknown"
#endif

namespace {

// ------------------------------------------------------------ log

pthread_mutex_t g_log_lock = PTHREAD_MUTEX_INITIALIZER;
std::string g_log_queue;
FILE* g_log_file = NULL;
volatile bool g_log_thread = false;

// What the guest thread is doing, for the heartbeat: "game" while the
// player's own code runs, otherwise the host call or runtime wait it is in.
volatile const char* g_stage = "start";
// The other threads', for the heartbeat and a stall's report: the replay thread's (the loading screen kept moving),
// the texture loader's (the texture it reads), the clip reader's (ch360_ogg.c).
volatile const char* g_replay_stage = "start";
// The every-600-frames report (ch360_plat_frame_begin takes the game's figures, the log's thread writes it: host_report).
volatile bool g_report_due = false;
unsigned g_report_frames = 0, g_report_slow = 0;
double g_report_logic_ms = 0, g_report_worst_ms = 0;
void host_report();
char g_texload_what[80] = "start";
extern pthread_mutex_t g_gpu_lock;   // the renderer's (below)
volatile unsigned g_frames_seen = 0;
volatile bool g_player_started = false;
// The runtime's file calls (srt_plat_*, wrapped below): counts, bytes, time.
volatile unsigned g_io_opens = 0, g_io_reads = 0, g_io_seeks = 0;
volatile unsigned long long g_io_bytes = 0;
volatile double g_io_ms = 0;

volatile size_t g_texture_bytes = 0;   // the renderer's, for the heartbeat
volatile bool g_log_loading = false;    // the last frame the game drew was the loading screen (record_loading)
unsigned g_log_dropped = 0;             // lines left out past LOG_QUEUE_MAX
// Lines queued at most: past it they are left out (counted) rather than written during play.
const size_t LOG_QUEUE_MAX = 4u << 20;
// The test build (CH360_TEST64: rarely-run code -Os, Test64.cs logging every result, then stopping before the
// saves) keeps its own log beside the play build's, written as each line comes.
#ifdef CH360_TEST64
#define CH360_LOG_PATH "game:/" CH360_TEST_NAME ".txt"
#else
#define CH360_LOG_PATH "game:/cuphead360.txt"
#endif

void flush_log(bool sync = false) {
    std::string lines;
    pthread_mutex_lock(&g_log_lock);
    lines.swap(g_log_queue);
    if (g_log_dropped) {
        char note[80];
        int n = snprintf(note, sizeof(note), "log: %u lines left out (the queue was full during play)\n", g_log_dropped);
        if (n > 0) lines.append(note, (size_t)n);
        g_log_dropped = 0;
    }
    pthread_mutex_unlock(&g_log_lock);
    if (lines.empty()) return;
    if (!g_log_file) g_log_file = fopen(CH360_LOG_PATH, "ab");
    if (g_log_file) {
        fwrite(lines.data(), 1, lines.size(), g_log_file);
        fflush(g_log_file);
        // On the drive even if the console freezes next (no fault handler).
        if (sync) fsync(fileno(g_log_file));
    }
}

void logf(const char* fmt, ...);
double now_ms();
void memory_mb(unsigned* free_mb, unsigned* total_mb);
extern "C" void srt_get_stats(void* out);

// The USB drive is slow per write, and the texture loader and the clip reader read from it: lines queue in
// memory and a thread writes them only while nothing plays: the loading screen up, or no frame finished for
// half a second (a load behind the black, or a hang: what led up to a freeze still reaches the drive), and
// at quit (Back + Start); synced at most every 30 s. Past LOG_QUEUE_MAX queued, lines are left out instead. Written every 2 s whatever the
// game did (and every 100 ms, synced twice a second, before the twenty-second run), each write and sync sat
// in the drive's one queue ahead of the pages and sounds the frames waited for: the logo and play now write
// nothing. Every 2 s for the first two minutes, then every 10 s, it logs a heartbeat: the frame count and
// where the guest thread is, so a stall shows its place, and a log that simply ends means the whole
// console froze.
void* log_writer(void*) {
    xsdk_thread_move(5);
    double start = now_ms(), frame_at = start, synced_at = start;
    unsigned frames_at = 0;
    for (unsigned tick = 1;; ++tick) {
        struct timespec ts = { 0, 100000000 };
        nanosleep(&ts, NULL);
        unsigned s = (unsigned)((now_ms() - start) / 1000.0);
        bool beat = tick % (s < 120 ? 20 : 100) == 0;
        // The player's output during a long frame (otherwise drained at the next frame).
        if (g_player_started) ch360_drain_output();
        if (g_report_due) host_report();
        if (beat) {
            struct { unsigned long long memory, capacity, stdout_bytes, wasi_calls; } st;
            srt_get_stats(&st);
            unsigned free_mb, total_mb;
            memory_mb(&free_mb, &total_mb);
            logf("alive %u s: frame %u, %s; %u MB free, player %u MB, textures %u MB; wasi calls %u; files: %u opens, %u reads, "
                 "%u MB in %u ms, %u seeks; replay: %s; texture loader: %s; clip reader: %s",
                 s, g_frames_seen, (const char*)g_stage, free_mb, ch360_memory_kb() / 1024, (unsigned)(g_texture_bytes >> 20),
                 (unsigned)st.wasi_calls, g_io_opens, g_io_reads, (unsigned)(g_io_bytes >> 20), (unsigned)g_io_ms, g_io_seeks,
                 (const char*)g_replay_stage, g_texload_what, (const char*)ch360_clip_reader_stage);
        }
#ifdef CH360_TEST64
        // A test build ends itself: one stuck in a probe before its first frame (a heap walk that never ended,
        // hw33) left the console to be switched off, Back + Start being read only between frames.
        if (s >= 20) {
            logf("test build: still running after 20 s, quitting");
            flush_log(true);
            _exit(0);
        }
#endif
        double now = now_ms();
        if (g_frames_seen != frames_at) {
            frames_at = g_frames_seen;
            frame_at = now;
        }
        bool idle = g_log_loading || now - frame_at > 500;
        // No frame for 2 s: where every thread is, every 2 s until frames come again (the thirty-seventh run
        // locked up on the way to the map and its log ended with nothing after the load's start).
        bool stalled = now - frame_at > 2000;
        if (tick % 20 == 0 && stalled) {
            unsigned free_mb, total_mb;
            memory_mb(&free_mb, &total_mb);
            bool gpu_free = pthread_mutex_trylock(&g_gpu_lock) == 0;
            if (gpu_free) pthread_mutex_unlock(&g_gpu_lock);
            logf("STALL: no frame for %u ms (frame %u); game thread: %s; replay thread: %s; texture loader: %s; clip reader: %s; "
                 "renderer lock %s; %u MB free", (unsigned)(now - frame_at), g_frames_seen, (const char*)g_stage,
                 (const char*)g_replay_stage, g_texload_what, (const char*)ch360_clip_reader_stage, gpu_free ? "free" : "held",
                 free_mb);
        }
        if (tick % 20 == 0 && idle) {
            // Synced every 2 s while loading or stalled: written but not synced, the lines before a lock-up were lost
            // with the power (30 s between syncs before); in play nothing is written.
            bool sync = now - synced_at > ((stalled || g_log_loading) ? 1900 : 30000);
            if (sync) synced_at = now;
            flush_log(sync);
        }
    }
    return NULL;
}

struct Stage {
    const char* prev;
    explicit Stage(const char* s) : prev((const char*)g_stage) { g_stage = s; }
    ~Stage() { g_stage = prev; }
};

void log_text(const char* text, size_t len) {
    if (ch360_nolog) return;
    pthread_mutex_lock(&g_log_lock);
    if (g_log_queue.size() + len > LOG_QUEUE_MAX) ++g_log_dropped;
    else g_log_queue.append(text, len);
    pthread_mutex_unlock(&g_log_lock);
    if (!g_log_thread) flush_log();
}

void logf(const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(buf, sizeof(buf) - 2, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n > (int)sizeof(buf) - 2) n = sizeof(buf) - 2;
    buf[n++] = '\n';
    log_text(buf, (size_t)n);
}

double now_ms() {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec * 1000.0 + (double)ts.tv_nsec / 1e6;
}

void memory_mb(unsigned* free_mb, unsigned* total_mb) {
    MM_QUERY_STATISTICS_RESULT st;
    memset(&st, 0, sizeof(st));
    st.size = sizeof(st);
    if (!NT_SUCCESS(MmQueryStatistics(&st))) {
        *free_mb = *total_mb = 0;
        return;
    }
    *free_mb = (unsigned)(((unsigned long long)st.title.available_pages * 4096) >> 20);
    *total_mb = (unsigned)(((unsigned long long)st.total_physical_pages * 4096) >> 20);
}

// ------------------------------------------------------------ renderer

// GCC turns small copies into 8-byte stores, which fault in the GPU's
// write-combined memory unless aligned; xsdk's memcpy keeps to what that
// memory takes (Spire360's lesson), so call it, never inline it.
void* (*volatile g_memcpy)(void*, const void*, size_t) = memcpy;

const int W = 1280, H = 720, FRAMES = 2;
const VkFormat TARGET_FORMAT = VK_FORMAT_B8G8R8A8_UNORM;
// Vertices a frame (two frames in flight): 174,000 at 24 bytes; frames draw up to
// ~16,000 since hidden UI is skipped (8 MB a frame held 12 MB more than needed), but Hilda Berg's
// night sky (Stars_Night: a mesh of ~14,000 triangles round each star layer's stars) drew ~150,000,
// and the 87,000 of a 2 MB ring left out everything after it (the twenty-second run: only the sky
// and the clouds). The glue also drops the off-screen triangles of meshes that large.
const VkDeviceSize VERTEX_RING = 4 * 1024 * 1024;
// Evict least recently used textures above this: the XEX image is ~100 MB and
// the player's linear memory reaches ~220 MB in a level, of 512 MB. A new
// texture first makes room from textures not drawn lately, so the set stays
// near the budget across a scene change (the map's first frame draws 68 MB
// while the title's and slot select's 53 MB are still loaded). With sprites at
// full size the map draws ~91 MB; memory (MEMORY_FLOOR_MB) was what held the
// set lower in the twelfth run, so the budget is only a little above that.
// Raised to 144 MB once the player's memory stopped creeping (the scenes' scratch lists and the asset table
// let go of what they held): free memory above the floor is the limit again, not this cap, which the
// twenty-fifth run's levels sat at (111 MB) while their pages came on demand. 192 MB since the XEX image
// gave back ~18 MB (the module's data segments read from GameData/PLAYER.DAT, the memory base loaded once a
// function): the room goes to the textures, whose pages read again in play were the run and gun levels'
// hitches (Forest Follies 248 on the thirtieth run, at 122 MB with 25 free), and the cap would have taken it.
const size_t TEXTURE_BUDGET = 192u << 20;
// Uploads go through one staging buffer, run (submitted and waited) when it
// fills: one buffer per texture until the frame ended doubled a scene's first
// frame in memory, and the fifth run froze there.
const size_t STAGING_RING = 8u << 20;
// Free memory kept for the player's linear memory (it grows during scene
// loads), the kernel and the driver: textures are not loaded into it.
const unsigned MEMORY_FLOOR_MB = 24;
// Read-ahead makes room from textures not drawn for this many frames (behind the camera, a phase gone by),
// never from what the screen showed lately; a sprite page is 4 MB at most (tools/cluster_console.py).
const unsigned AHEAD_AGE = 180;
// Frames younger a texture counts for each time it was read again, up to RELOAD_CAP times (Renderer::release_unused).
// Six before the thirty-ninth run: a page read again twice counted 24 s younger than it was, and in Forest Follies
// (305 read again) the pages behind the camera stayed too young to make room for the read-ahead, which waited in
// 3,954 frames while the frames' own loads took the memory. Within reach a page is asked for again every 30 game
// frames (RenderList.Ahead), which keeps it young without the credit.
const unsigned RELOAD_CREDIT = 240;
const unsigned RELOAD_CAP = 2;
const size_t PAGE_MAX = 4u << 20;
// Pages' stored bytes (LZ4, a fifth to a quarter of their size) kept in memory: a page evicted and drawn
// again, or read ahead, decodes without the drive (USB reads ran at 9 MB/s: 50-80 ms a page, from memory
// ~24 ms a 4 MB page). Off. Each a malloc of its own, newlib's heap never gave back what it grew to (the
// twenty-second and twenty-third runs: ~35 MB more of the console's memory not counted anywhere, textures
// squeezed to 41 MB and refused). From 2 MB slabs given back whole when empty (the twenty-seventh run),
// the copies kept longest (of pages let go) pinned nearly every slab: 15 MB of copies held 60 MB of slabs,
// the textures fell to 54-69 MB (the map after Hilda Berg at 37 fps, pages refused and drawn without: the
// smoke puffs flickered, the Frogs' bouncing balls went missing). Back only with copies that can move.
// Back since the fortieth run in one block made at start (STORED_ARENA: its own free list, nothing outside it
// fragmented by them, and nothing given back to the console by letting them go). A level's every page is asked for
// under the hourglass (RenderList.FillAhead: texture_guess), read into it and kept through the level (scene_kept),
// and every page read from the drive leaves its copy there too while there is room (least recently used go).
// The thirty-ninth run's fights waited on pages first drawn with no read-ahead (Hilda Berg 107 loaded on demand,
// 250 read again; 63 of her 76 hitches); tools/texture_cache_sim.py over a PC trace of her fight: hitches 66 -> 35
// with 32 MB of the room here (Forest Follies' at the locked 30 12 -> 1, Treetop's 10 -> 0).
// Off (0) since tools/texture_cache_sim.py over the PC traces: the fill's pages were too far from what the fights draw,
// and its room taken from the decoded pages cost more waits than it saved (Forest Follies 21 -> 45 frames late at
// the console's room, Hilda Berg 71 -> 112); the code stays for a fill that knows what a level draws.
const size_t STORED_ARENA = 0;
// The loader's buffers (a job's decoded texture until it's copied to staging) come from one block made at
// start, not malloc: a malloc of up to 4 MB a texture load left newlib's heap at its highest.
const size_t LOAD_ARENA = 12u << 20;


enum { MODE_SPRITE, MODE_SCREEN, MODE_MULTIPLY, MODE_OVERLAY, MODE_SDF, MODE_COLOROVERLAY, MODE_LIGHTRAY,
       MODE_KNIGHT, MODE_DARK, MODE_BLURSPRITE, MODE_COPY, MODE_DOWN4, MODE_BLUR7, MODE_BLUR7G, MODE_TWOSTRIP, MODE_BW,
       MODE_GRAIN, MODE_BLUR7A, MODES };
// The effects (items' modes 8-17, Cuphead360.Engine's Effects): a second
// texture and twelve floats; the blits (from MODE_COPY) replace their target.
const int FIRST_FX = MODE_KNIGHT, FIRST_BLIT = MODE_COPY;
// An item's mode -> its pipeline's (7, the sprite masks, has pipelines of its own).
const signed char PIPE_MODE[20] = { MODE_SPRITE, MODE_SCREEN, MODE_MULTIPLY, MODE_OVERLAY, MODE_SPRITE, MODE_SDF,
                                    MODE_LIGHTRAY, MODE_SPRITE, MODE_KNIGHT, MODE_DARK, MODE_BLURSPRITE, MODE_COPY,
                                    MODE_DOWN4, MODE_BLUR7, MODE_BLUR7G, MODE_TWOSTRIP, MODE_BW, MODE_GRAIN, MODE_BLUR7A,
                                    MODE_COLOROVERLAY };
// Sprite masks (ch360_item.stencil): a pipeline per mode for drawing where the
// stencil is 1 or more (inside) or 0 (outside); the masks' own two (add, remove).
enum { STENCIL_NONE, STENCIL_INSIDE, STENCIL_OUTSIDE, STENCIL_TESTS, STENCIL_ADD = STENCIL_TESTS, STENCIL_REMOVE,
       STENCIL_WRITE, STENCIL_EQUAL, STENCIL_GREATER };   // the game's stencil masks: write a value / draw where it is
                                                          // that value / under it
// Blend states (Unity's factors and ops) of the game's blend and mask shaders' passes.
enum { BLEND_PREMUL, BLEND_REPLACE, BLEND_MASK_WRITE, BLEND_MASKED0, BLEND_MASKED1, BLEND_REV_DA, BLEND_ADD_DA,
       BLEND_REV_IDA, BLEND_ADD_IDA, BLEND_NO_COLOR, BLEND_MUL, BLEND_ADD_LIT, BLEND_REV_SUB };
// Their passes' fragment programs.
enum { PF_SPRITE, PF_INV, PF_STRAIGHT, PF_SQ, PF_A, PF_CLEAR, PF_SCREEN, PF_STENCIL, PF_BLENDK, PF_BLENDM, PF_BLENDN,
       PF_MONO, PF_BAND, PF_CAPPED, PF_HONEY, PF_ICE, PF_GRABOVL, PASS_FRAGS };
struct FxPass {
    signed char frag, blend, stencil;
    unsigned char ref;
    float lo, hi;   // the pass's own p2.xy (an alpha band, a cap) when not both 0
};
struct FxPasses {
    int n;
    FxPass p[6];
};
// Items' modes 20-38 (Effects.cs): each drawn in its passes, as Unity draws
// every pass of a sprite's shader (StencilMaskedMultiReversed's last pass
// draws nothing and is left out).
const FxPasses PASSES[19] = {
    { 1, { { PF_INV, BLEND_MASK_WRITE, STENCIL_NONE, 0 } } },                                                  // 20 AlphaMask
    { 2, { { PF_INV, BLEND_MASKED0, STENCIL_NONE, 0 }, { PF_STRAIGHT, BLEND_MASKED1, STENCIL_NONE, 0 } } },    // 21 AlphaMasked
    { 2, { { PF_SQ, BLEND_REV_IDA, STENCIL_NONE, 0 }, { PF_SPRITE, BLEND_ADD_IDA, STENCIL_NONE, 0 } } },       // 22 ..NonDestructive
    { 2, { { PF_SQ, BLEND_REV_DA, STENCIL_NONE, 0 }, { PF_SPRITE, BLEND_ADD_DA, STENCIL_NONE, 0 } } },         // 23 ..Reversed
    { 2, { { PF_SQ, BLEND_REV_IDA, STENCIL_NONE, 0 }, { PF_SCREEN, BLEND_ADD_IDA, STENCIL_NONE, 0 } } },       // 24 ..Screen
    { 2, { { PF_SQ, BLEND_REV_DA, STENCIL_NONE, 0 }, { PF_SCREEN, BLEND_ADD_DA, STENCIL_NONE, 0 } } },         // 25 ..ReversedScreen
    { 2, { { PF_A, BLEND_REV_DA, STENCIL_NONE, 0 }, { PF_SPRITE, BLEND_ADD_DA, STENCIL_NONE, 0 } } },          // 26 ..ReversedGlyphs
    { 1, { { PF_CLEAR, BLEND_MASK_WRITE, STENCIL_NONE, 0 } } },                                                // 27 AlphaMaskClearer
    { 1, { { PF_STENCIL, BLEND_NO_COLOR, STENCIL_WRITE, 5 } } },                                               // 28 StencilMask
    { 1, { { PF_SPRITE, BLEND_PREMUL, STENCIL_EQUAL, 5 } } },                                                  // 29 StencilMasked
    { 6, { { PF_BAND, BLEND_NO_COLOR, STENCIL_WRITE, 9, 0.4f, 0.5f }, { PF_BAND, BLEND_NO_COLOR, STENCIL_WRITE, 10, 0.5f, 0.6f },
           { PF_BAND, BLEND_NO_COLOR, STENCIL_WRITE, 11, 0.6f, 0.7f }, { PF_BAND, BLEND_NO_COLOR, STENCIL_WRITE, 12, 0.7f, 0.8f },
           { PF_BAND, BLEND_NO_COLOR, STENCIL_WRITE, 13, 0.8f, 0.9f },
           { PF_BAND, BLEND_NO_COLOR, STENCIL_WRITE, 14, 0.8999f, 2.0f } } },                                    // 30 StencilMaskMulti
    { 6, { { PF_CAPPED, BLEND_PREMUL, STENCIL_GREATER, 9, 1.0f, 0 }, { PF_CAPPED, BLEND_PREMUL, STENCIL_EQUAL, 9, 0.5f, 0 },
           { PF_CAPPED, BLEND_PREMUL, STENCIL_EQUAL, 10, 0.4f, 0 }, { PF_CAPPED, BLEND_PREMUL, STENCIL_EQUAL, 11, 0.3f, 0 },
           { PF_CAPPED, BLEND_PREMUL, STENCIL_EQUAL, 12, 0.2f, 0 },
           { PF_CAPPED, BLEND_PREMUL, STENCIL_EQUAL, 13, 0.1f, 0 } } },                                          // 31 ..MaskedMultiReversed
    { 1, { { PF_BAND, BLEND_NO_COLOR, STENCIL_WRITE, 0, -1.0f, 2.0f } } },                                     // 32 StencilMaskMultiClearer
    { 2, { { PF_BLENDK, BLEND_MUL, STENCIL_NONE, 0 }, { PF_BLENDM, BLEND_ADD_LIT, STENCIL_NONE, 0 } } },       // 33 Blend Modes/Screen
    { 3, { { PF_BLENDK, BLEND_MUL, STENCIL_NONE, 0 }, { PF_BLENDM, BLEND_ADD_LIT, STENCIL_NONE, 0 },
           { PF_BLENDN, BLEND_REV_SUB, STENCIL_NONE, 0 } } },                                                  // 34 Hard Light
    { 1, { { PF_MONO, BLEND_PREMUL, STENCIL_NONE, 0 } } },                                                     // 35 MonochromeNoBlack
    { 1, { { PF_HONEY, BLEND_REPLACE, STENCIL_NONE, 0 } } },                                                   // 36 Honey
    { 1, { { PF_ICE, BLEND_PREMUL, STENCIL_NONE, 0 } } },                                                      // 37 IceCubes
    { 1, { { PF_GRABOVL, BLEND_PREMUL, STENCIL_NONE, 0 } } },                                                  // 38 Blend Modes/Overlay
};
const VkFormat DEPTH_FORMAT = VK_FORMAT_D24_UNORM_S8_UINT;

// ------------------------------------------------------------ the texture loader thread
// Reading and decoding a texture from the pack took 8 ms (median) to 478 ms on the frame that first drew
// it: most of the fifteenth run's slow frames. The loader thread (hardware thread 2) does that part; the
// game thread asks (load_packed), and at the next frames' start takes what is done (take_loads): the image,
// staging, the copy. Until then the texture's items draw without it. The loader allocates nothing (newlib's
// malloc isn't known to be thread safe here): the game thread makes each job and its buffer, sized from the
// pack's table, and the jobs pass through fixed rings; it has its own pack reader (ch360_pack_reader_new);
// it reads only the texture pack, whose file nothing else reads after start-up.
struct LoadJob {
    int id;
    std::string key;
    ch360_pack_entry e;
    unsigned char* data;   // the texture's bytes after the 28-byte header (NULL: its stored bytes only)
    unsigned char* stored; // the entry's stored bytes: the texture's own copy, or read into here (fresh)
    bool fresh;            // stored is this job's to read (and the texture's to keep after)
    unsigned n, fmt, w, h;
    size_t bytes;          // the job's buffers
    int state;             // 0 read, 1 bad header, 2 bad read
    bool pre;              // a prefetch (nothing drew it yet)
    double queued, read_ms;
};
const unsigned LOAD_RING = 64;

// The loader's arena: free ranges by offset, first fit, merged when given back (main thread only).
unsigned char* g_arena = NULL;
std::vector<std::pair<size_t, size_t> > g_arena_free;   // (offset, size), by offset

unsigned char* arena_take(size_t n) {
    n = (n + 127) & ~(size_t)127;
    for (size_t i = 0; i < g_arena_free.size(); ++i)
        if (g_arena_free[i].second >= n) {
            unsigned char* p = g_arena + g_arena_free[i].first;
            g_arena_free[i].first += n;
            g_arena_free[i].second -= n;
            if (g_arena_free[i].second == 0) g_arena_free.erase(g_arena_free.begin() + (long)i);
            return p;
        }
    return NULL;
}

// A buffer back to the arena, or free() when it came from malloc (none fitted).
// The stored bytes' block (STORED_ARENA), made once at start: first fit by offset, neighbours joined when given back.
unsigned char* g_stored_base = NULL;
std::vector<std::pair<size_t, size_t> > g_stored_free;   // (offset, size), by offset
unsigned char* stored_take(size_t n) {
    if (!g_stored_base) return NULL;
    n = (n + 127) & ~(size_t)127;
    for (size_t i = 0; i < g_stored_free.size(); ++i)
        if (g_stored_free[i].second >= n) {
            unsigned char* p = g_stored_base + g_stored_free[i].first;
            g_stored_free[i].first += n;
            g_stored_free[i].second -= n;
            if (g_stored_free[i].second == 0) g_stored_free.erase(g_stored_free.begin() + (long)i);
            return p;
        }
    return NULL;
}
void stored_give(unsigned char* p, size_t n) {
    if (!p || !g_stored_base || p < g_stored_base || p >= g_stored_base + STORED_ARENA) return;
    n = (n + 127) & ~(size_t)127;
    size_t off = (size_t)(p - g_stored_base), i = 0;
    while (i < g_stored_free.size() && g_stored_free[i].first < off) ++i;
    g_stored_free.insert(g_stored_free.begin() + (long)i, std::make_pair(off, n));
    if (i + 1 < g_stored_free.size() && g_stored_free[i].first + g_stored_free[i].second == g_stored_free[i + 1].first) {
        g_stored_free[i].second += g_stored_free[i + 1].second;
        g_stored_free.erase(g_stored_free.begin() + (long)i + 1);
    }
    if (i > 0 && g_stored_free[i - 1].first + g_stored_free[i - 1].second == g_stored_free[i].first) {
        g_stored_free[i - 1].second += g_stored_free[i].second;
        g_stored_free.erase(g_stored_free.begin() + (long)i);
    }
}

void arena_give(unsigned char* p, size_t n) {
    if (!p) return;
    if (!g_arena || p < g_arena || p >= g_arena + LOAD_ARENA) {
        free(p);
        return;
    }
    n = (n + 127) & ~(size_t)127;
    size_t off = (size_t)(p - g_arena), i = 0;
    while (i < g_arena_free.size() && g_arena_free[i].first < off) ++i;
    g_arena_free.insert(g_arena_free.begin() + (long)i, std::make_pair(off, n));
    if (i + 1 < g_arena_free.size() && g_arena_free[i].first + g_arena_free[i].second == g_arena_free[i + 1].first) {
        g_arena_free[i].second += g_arena_free[i + 1].second;
        g_arena_free.erase(g_arena_free.begin() + (long)i + 1);
    }
    if (i > 0 && g_arena_free[i - 1].first + g_arena_free[i - 1].second == g_arena_free[i].first) {
        g_arena_free[i - 1].second += g_arena_free[i].second;
        g_arena_free.erase(g_arena_free.begin() + (long)i);
    }
}
LoadJob* g_load_in[LOAD_RING];    // textures items draw this frame
LoadJob* g_pre_in[LOAD_RING];     // prefetches (read only when no drawn one waits)
LoadJob* g_load_out[LOAD_RING];
unsigned g_load_in_head, g_load_in_tail, g_pre_in_head, g_pre_in_tail, g_load_out_head, g_load_out_tail;   // under g_load_lock
pthread_mutex_t g_load_lock = PTHREAD_MUTEX_INITIALIZER;
pthread_cond_t g_load_cond = PTHREAD_COND_INITIALIZER;   // a job queued
pthread_cond_t g_done_cond = PTHREAD_COND_INITIALIZER;   // a job done
ch360_pack_reader* g_load_reader = NULL;
bool g_loader = false;

double now_ms();

void* texture_loader(void*) {
    xsdk_thread_move(2);
    for (;;) {
        pthread_mutex_lock(&g_load_lock);
        snprintf(g_texload_what, sizeof(g_texload_what), "waiting for a job");
        while (g_load_in_head == g_load_in_tail && g_pre_in_head == g_pre_in_tail) pthread_cond_wait(&g_load_cond, &g_load_lock);
        LoadJob* j;
        if (g_load_in_head != g_load_in_tail) j = g_load_in[g_load_in_head++ % LOAD_RING];
        else j = g_pre_in[g_pre_in_head++ % LOAD_RING];
        pthread_mutex_unlock(&g_load_lock);
        if (!j) continue;   // a prefetch moved to the drawn ring
        snprintf(g_texload_what, sizeof(g_texload_what), "reading %.64s", j->key.c_str());
        double t0 = now_ms();
        unsigned char head[28];
        j->state = 1;
        if (j->stored && j->fresh && ch360_pack_read_stored(&j->e, j->stored) != 0) j->state = 2;
        else if (j->stored && !j->data) j->state = 0;   // its stored bytes only
        else if (j->stored ? ch360_pack_decode_stored(g_load_reader, &j->e, j->stored, 0, head, sizeof(head)) == 0
                           : ch360_pack_read_with(g_load_reader, &j->e, 0, head, sizeof(head)) == 0) {
            if (ch360_u32(head) == 0x31585443u) {
                j->fmt = ch360_u32(head + 4);
                j->w = ch360_u32(head + 8);
                j->h = ch360_u32(head + 12);
                unsigned n = ch360_u32(head + 24);
                if (j->w && j->h && j->w <= 8192 && j->h <= 8192 && n == j->n)
                    j->state = (j->stored ? ch360_pack_decode_stored(g_load_reader, &j->e, j->stored, 28, j->data, j->n)
                                          : ch360_pack_read_with(g_load_reader, &j->e, 28, j->data, j->n)) == 0 ? 0 : 2;
            }
        }
        j->read_ms = now_ms() - t0;
        pthread_mutex_lock(&g_load_lock);
        g_load_out[g_load_out_tail % LOAD_RING] = j;   // never fuller than the in rings: at most LOAD_RING jobs exist
        ++g_load_out_tail;
        pthread_cond_signal(&g_done_cond);
        pthread_mutex_unlock(&g_load_lock);
    }
    return NULL;
}

struct Tex {
    VkImage image;
    VkDeviceMemory mem;
    VkImageView view;
    VkDescriptorSet set;
    int w, h;
    VkFormat format;
    size_t bytes;
    unsigned last_used;
    unsigned retry_at;   // after running out of memory: the frame to try loading again
    std::string key;     // a converted texture (loaded on first use); empty: run-time pixels
    bool failed;
    bool pinned;         // kept through evictions (the film grain's frames)
    bool loading;        // a job for it is with the loader thread
    bool prefetching;    // that job is a prefetch
    bool pre_wanted;     // waiting in the prefetch backlog
    bool unused_pre;     // prefetched and not drawn yet: the first to make room
    bool soft_wanted;    // waiting in the guessed read-ahead's backlog
    bool evicted;        // let go for room since its last load
    unsigned char reloads;   // loads after being let go (release_unused keeps the often reloaded longer)
    unsigned char* stored;   // the pack entry's stored bytes, kept (STORED_ARENA)
    unsigned stored_n, stored_used;
    bool scene_kept;         // the scene's own (asked for by its load): its stored bytes never let go for another page's
    unsigned waited_frame;   // the last frame that waited for it (each wait counted once: WaitCauses)
    Tex() : image(VK_NULL_HANDLE), mem(VK_NULL_HANDLE), view(VK_NULL_HANDLE), set(VK_NULL_HANDLE), w(0), h(0),
            format(VK_FORMAT_UNDEFINED), bytes(0), last_used(0), retry_at(0), failed(false), pinned(false), loading(false), prefetching(false),
            pre_wanted(false), unused_pre(false), soft_wanted(false), evicted(false), reloads(0), stored(NULL), stored_n(0), stored_used(0),
            scene_kept(false), waited_frame(0) {}
};

struct Garbage {
    VkImage image;
    VkDeviceMemory mem;
    VkImageView view;
    VkDescriptorSet set;
    VkFramebuffer fb;   // a render texture's
};

// GPU time stamps: slot 0 as the frame's work starts, then one as each run of
// passes of one kind ends (vulkan4xenon writes the GPU clock once the GPU is idle).
const unsigned QSLOTS = 16;
enum { GPU_RT, GPU_FX, GPU_SCREEN, GPU_KINDS };

struct Frame {
    VkCommandBuffer cmd;
    VkFence fence;
    VkSemaphore acquired, rendered;
    std::vector<Garbage> dead;
    VkQueryPool queries;   // its own pool: resetting a pool waits for the submission that last wrote it
    unsigned query_count;
    unsigned char query_kind[QSLOTS];
};

// The host's side of frames since the player last asked (ch360_plat_host_stats):
// times in ms, fill in screens of pixels.
struct HostStats {
    double frames, gpu_frames, gpu_ms, gpu_kind[GPU_KINDS], fence_ms, present_ms, host_ms, loads_ms, uploads_ms;
    double textures, fill_screen, fill_offscreen, draws, passes, slow, demand, again;
};

class Renderer {
public:
    bool init();
    void frame(const ch360_frame* f);
    void texture_file(int id, const char* key);
    void texture_pixels(int id, int w, int h, const unsigned char* bgra);
    size_t texture_bytes() const { return texture_bytes_; }
    // Frees textures not drawn lately for the player's memory (the commit wrap).
    size_t stored_bytes() const { return stored_bytes_; }
    // The player's memory grows: textures not drawn lately go (the stored bytes' block gives the console nothing back).
    size_t make_room(size_t bytes) { return release_unused(bytes); }
    double gpu_wait_ms, present_ms;
    double host_ms_[8];
    HostStats stats_;
    // Why frames waited for textures since the last scene summary (logged beside it: ch360_plat_host_stats): each
    // texture a frame waited for, by what the read-ahead had done about it; and the frames the read-ahead couldn't
    // make room (memory) with the least free memory then.
    struct WaitCauses { unsigned never, queued, reading, let_go, held_frames, held_low_mb, from_memory, from_drive; } waits_;
    unsigned held_frame_;
    double host_gpu_[GPU_KINDS + 1], host_fill_[2];   // the HOST line's window: GPU ms by kind and in all, fill
    unsigned host_gpu_frames_, host_passes_, host_slow_, host_slow_logged_;
    double ts_period_ms_;
    unsigned host_draws_, host_items_;
    unsigned long long host_vertices_;
    unsigned dense_verts_, dense_frame_;   // the window's frame with the most vertices (over 15,000)
    char dense_top_[320];                  // and the textures drawing most of them
    unsigned uploads, evictions;

private:
    bool vk(VkResult r, const char* what) {
        if (r == VK_SUCCESS) return true;
        logf("vk: %s failed (%d)", what, (int)r);
        return false;
    }
    uint32_t memory_type(uint32_t bits, VkMemoryPropertyFlags want);
    bool make_buffer(VkBuffer* buf, VkDeviceMemory* mem, void** map, VkDeviceSize size, VkBufferUsageFlags usage);
    VkShaderModule module(const uint32_t* code, unsigned bytes);
    VkPipeline make_pipeline(VkShaderModule frag, VkPipelineLayout layout, int stencil, int blend = BLEND_PREMUL, int ref = 1);
    VkPipeline pass_pipeline(const FxPass& p);
    VkPipeline pipeline(int mode, int stencil);
    bool make_image(Tex& t, VkFormat format, int w, int h,
                    VkImageUsageFlags usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT);
    unsigned char* staging(size_t bytes, VkBuffer* buf, VkDeviceSize* off);
    void record_copy(Tex& t, VkBuffer buf, VkDeviceSize off);
    void flush_uploads();
    bool upload(Tex& t, VkFormat format, int w, int h, const unsigned char* data, size_t bytes);
    void destroy(Tex& t, bool now);
    void load(int id, Tex& t);
    void load_packed(int id, Tex& t);
    void take_loads(bool all = false);
    void finish_waits();
    bool queue_load(int id, Tex& t, bool pre, bool stored_only = false);
    void feed_prefetch();
    size_t release_stored(size_t want);
    unsigned char* stored_room(size_t n);
    void forget_stored(Tex& t);
    unsigned fill_asked_, fill_kept_, fill_dropped_;   // a level's fill (Renderer::fill): pages asked, kept, left without room
    bool fill_open_;
    std::deque<int> pre_backlog_;   // prefetches asked for, queued as the loader and memory have room
    std::deque<int> soft_backlog_;  // guessed read-ahead: stored bytes only, while there is room
    size_t stored_bytes_;           // the textures' kept stored bytes
    std::vector<LoadJob*> taken_;   // done jobs past this frame's upload budget
    std::vector<int> waiting_;      // textures this frame's items draw that the loader still has
    unsigned last_waited_;          // how many there were, the last frame
    unsigned load_jobs_;            // jobs made and not yet taken back
    size_t load_bytes_, pre_bytes_; // their buffers', the prefetches' of them
    double wait_ms_;
    bool hold_feed_;                // a scene's read-ahead being asked for: the backlog fed at its end
public:
    void start_loader();
    void prefetch(int id, bool wanted);
    void fill(int id);
    void scene_read_ahead(bool begin);
    void take_asks();
    void held_ahead(unsigned free_mb) {
        if (held_frame_ == frame_no_) return;
        held_frame_ = frame_no_;
        ++waits_.held_frames;
        if (waits_.held_low_mb == 0 || free_mb < waits_.held_low_mb) waits_.held_low_mb = free_mb;
    }
    // A level's fill counts: its load waits for it too (up to its eight seconds).
    int pending() const { return (int)(load_jobs_ + pre_backlog_.size() + soft_backlog_.size()); }
    // Textures the last frame drew and waited for (the level hold ends when a frame waits for none).
    int waited() const { return (int)last_waited_; }
private:
    void evict();
    size_t release_unused(size_t want);
    size_t release_aged(size_t want, unsigned age);
    Tex* tex(int id);
    void load_for(int id);

    // Render textures: what a camera or a blit draws this frame (a render
    // pass of its own, resolved out of eDRAM into the image), sampled by
    // later passes. By the engine's number; freed when not drawn for 300
    // frames.
    struct Target {
        Tex tex;
        VkFramebuffer fb;
        unsigned last_used;
        Target() : fb(VK_NULL_HANDLE), last_used(0) {}
    };
    struct DepthBuf {
        VkImage image;
        VkDeviceMemory mem;
        VkImageView view;
    };
    Target* target(unsigned id, int w, int h);
    Tex* sampled(int id);
    VkImageView depth_for(int w, int h);
    void destroy_target(Target& t);
    void free_targets();

    VkInstance instance_;
    VkPhysicalDevice phys_;
    VkPhysicalDeviceMemoryProperties mem_props_;
    VkDevice dev_;
    VkQueue queue_;
    uint32_t family_;
    VkSurfaceKHR surface_;
    VkSwapchainKHR swapchain_;
    std::vector<VkImage> images_;
    std::vector<VkImageView> views_;
    std::vector<VkFramebuffer> fbs_;
    VkRenderPass pass_;
    VkRenderPass pass_load_;   // the screen again after a render texture's pass (kept, not cleared)
    VkRenderPass rt_pass_;     // a render texture: cleared, then sampled
    VkRenderPass rt_pass_load_;   // a render texture gone on in after a grab's copy (what it held drawn back in)
    std::map<unsigned, Target> targets_;
    std::map<std::pair<int, int>, DepthBuf> depths_;   // render textures' depth and stencil by size but the screen's
    VkCommandPool pool_;
    VkCommandBuffer upload_cmd_;
    VkFence upload_fence_;
    VkDescriptorSetLayout set_layout_;
    VkDescriptorPool desc_pool_;
    VkPipelineLayout pipe_layout_;
    VkPipelineLayout ray_layout_;   // the light ray: three textures, its floats for the fragment stage
    VkPipelineLayout fx_layout_;    // the effects: two textures, twelve floats for the fragment stage
    VkShaderModule vert_;
    VkShaderModule frags_[MODES], mask_frag_, pass_frags_[PASS_FRAGS];
    std::map<unsigned, VkPipeline> pass_pipes_;   // by fragment program, blend, stencil and reference
    VkPipeline pipes_[MODES][STENCIL_TESTS];   // made on first use but STENCIL_NONE's
    VkPipeline mask_pipes_[2];
    VkImage depth_image_;
    VkDeviceMemory depth_mem_;
    VkImageView depth_view_;
    VkSampler sampler_;
    VkBuffer vbuf_;
    VkDeviceMemory vmem_;
    unsigned char* vmap_;
    Frame frames_[FRAMES];
    int frame_;
    unsigned frame_no_;
    std::vector<Tex> textures_;
    Tex white_;
    size_t texture_bytes_;
    bool upload_open_;
    VkBuffer ring_buf_;
    VkDeviceMemory ring_mem_;
    unsigned char* ring_map_;
    size_t ring_used_;
    std::vector<std::pair<VkBuffer, VkDeviceMemory> > staging_;   // uploads larger than the ring
    std::vector<unsigned char> bounce_;                            // file reads, copied on into staging
    bool staging_cached_;                                          // staging in the CPU's cached memory
    std::vector<unsigned char> on_view_;                           // per item of the frame's list
    bool overflow_logged_;
public:
    unsigned ring_drops;   // items left out for the vertex ring
private:
    unsigned loads_logged_;
};

uint32_t Renderer::memory_type(uint32_t bits, VkMemoryPropertyFlags want) {
    for (uint32_t i = 0; i < mem_props_.memoryTypeCount; ++i)
        if ((bits & (1u << i)) && (mem_props_.memoryTypes[i].propertyFlags & want) == want) return i;
    for (uint32_t i = 0; i < mem_props_.memoryTypeCount; ++i)
        if (bits & (1u << i)) return i;
    return 0;
}

bool Renderer::make_buffer(VkBuffer* buf, VkDeviceMemory* mem, void** map, VkDeviceSize size, VkBufferUsageFlags usage) {
    VkBufferCreateInfo ci;
    memset(&ci, 0, sizeof(ci));
    ci.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    ci.size = size;
    ci.usage = usage;
    ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (!vk(vkCreateBuffer(dev_, &ci, NULL, buf), "vkCreateBuffer")) return false;
    VkMemoryRequirements req;
    vkGetBufferMemoryRequirements(dev_, *buf, &req);
    VkMemoryAllocateInfo ai;
    memset(&ai, 0, sizeof(ai));
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    // Buffers only copied from (staging) take the CPU's cached memory where
    // there is one: vulkan4xenon copies DXT data into images on the CPU, and
    // reading the GPU's write-combined memory back ran at 18.5 MB/s
    // (vulkan4xenon's transfer.c), ~220 ms of every 4 MB atlas in the eighth run.
    const VkMemoryPropertyFlags visible = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
    bool cached = false;
    ai.memoryTypeIndex = memory_type(req.memoryTypeBits, visible);
    if (usage == VK_BUFFER_USAGE_TRANSFER_SRC_BIT)
        for (uint32_t i = 0; i < mem_props_.memoryTypeCount; ++i)
            if ((req.memoryTypeBits & (1u << i)) &&
                (mem_props_.memoryTypes[i].propertyFlags & (visible | VK_MEMORY_PROPERTY_HOST_CACHED_BIT)) ==
                    (visible | VK_MEMORY_PROPERTY_HOST_CACHED_BIT)) {
                ai.memoryTypeIndex = i;
                cached = true;
                break;
            }
    if (usage == VK_BUFFER_USAGE_TRANSFER_SRC_BIT) staging_cached_ = cached;
    if (!vk(vkAllocateMemory(dev_, &ai, NULL, mem), "vkAllocateMemory (buffer)")) {
        vkDestroyBuffer(dev_, *buf, NULL);
        return false;
    }
    vkBindBufferMemory(dev_, *buf, *mem, 0);
    return vk(vkMapMemory(dev_, *mem, 0, VK_WHOLE_SIZE, 0, map), "vkMapMemory");
}

VkShaderModule Renderer::module(const uint32_t* code, unsigned bytes) {
    VkShaderModuleCreateInfo ci;
    memset(&ci, 0, sizeof(ci));
    ci.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
    ci.codeSize = bytes;
    ci.pCode = code;
    VkShaderModule m = VK_NULL_HANDLE;
    vk(vkCreateShaderModule(dev_, &ci, NULL, &m), "vkCreateShaderModule");
    return m;
}

bool Renderer::init() {
    gpu_wait_ms = present_ms = 0;
    memset(&stats_, 0, sizeof(stats_));
    memset(host_gpu_, 0, sizeof(host_gpu_));
    memset(host_fill_, 0, sizeof(host_fill_));
    host_gpu_frames_ = host_passes_ = host_slow_ = host_slow_logged_ = 0;
    ts_period_ms_ = 0;
    uploads = evictions = 0;
    frame_ = 0;
    frame_no_ = 0;
    texture_bytes_ = 0;
    upload_open_ = false;
    overflow_logged_ = false;
    ring_drops = 0;
    loads_logged_ = 0;
    const char* iexts[2] = { VK_KHR_SURFACE_EXTENSION_NAME, VK_KHR_DISPLAY_EXTENSION_NAME };
    VkApplicationInfo app;
    memset(&app, 0, sizeof(app));
    app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    app.pApplicationName = "cuphead360";
    app.apiVersion = VK_API_VERSION_1_0;
    VkInstanceCreateInfo ici;
    memset(&ici, 0, sizeof(ici));
    ici.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    ici.pApplicationInfo = &app;
    ici.enabledExtensionCount = 2;
    ici.ppEnabledExtensionNames = iexts;
    if (!vk(vkCreateInstance(&ici, NULL, &instance_), "vkCreateInstance")) return false;
    uint32_t n = 1;
    if (vkEnumeratePhysicalDevices(instance_, &n, &phys_) < 0 || n == 0) {
        logf("vk: no device");
        return false;
    }
    vkGetPhysicalDeviceMemoryProperties(phys_, &mem_props_);
    uint32_t qn = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(phys_, &qn, NULL);
    std::vector<VkQueueFamilyProperties> qf(qn ? qn : 1);
    if (qn) vkGetPhysicalDeviceQueueFamilyProperties(phys_, &qn, &qf[0]);
    family_ = 0;
    for (uint32_t i = 0; i < qn; ++i)
        if (qf[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) {
            family_ = i;
            break;
        }
    float prio = 1.0f;
    VkDeviceQueueCreateInfo qi;
    memset(&qi, 0, sizeof(qi));
    qi.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    qi.queueFamilyIndex = family_;
    qi.queueCount = 1;
    qi.pQueuePriorities = &prio;
    const char* dexts[1] = { VK_KHR_SWAPCHAIN_EXTENSION_NAME };
    VkDeviceCreateInfo dci;
    memset(&dci, 0, sizeof(dci));
    dci.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    dci.queueCreateInfoCount = 1;
    dci.pQueueCreateInfos = &qi;
    dci.enabledExtensionCount = 1;
    dci.ppEnabledExtensionNames = dexts;
    if (!vk(vkCreateDevice(phys_, &dci, NULL, &dev_), "vkCreateDevice")) return false;
    vkGetDeviceQueue(dev_, family_, 0, &queue_);

    // The display plane's swapchain at 1280x720, FIFO (vsync).
    uint32_t dn = 0;
    if (vkGetPhysicalDeviceDisplayPropertiesKHR(phys_, &dn, NULL) != VK_SUCCESS || !dn) return false;
    std::vector<VkDisplayPropertiesKHR> displays(dn);
    vkGetPhysicalDeviceDisplayPropertiesKHR(phys_, &dn, &displays[0]);
    uint32_t mn = 0;
    vkGetDisplayModePropertiesKHR(phys_, displays[0].display, &mn, NULL);
    if (!mn) return false;
    std::vector<VkDisplayModePropertiesKHR> modes(mn);
    vkGetDisplayModePropertiesKHR(phys_, displays[0].display, &mn, &modes[0]);
    VkDisplaySurfaceCreateInfoKHR sci;
    memset(&sci, 0, sizeof(sci));
    sci.sType = VK_STRUCTURE_TYPE_DISPLAY_SURFACE_CREATE_INFO_KHR;
    sci.displayMode = modes[0].displayMode;
    sci.transform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    sci.globalAlpha = 1.0f;
    sci.alphaMode = VK_DISPLAY_PLANE_ALPHA_OPAQUE_BIT_KHR;
    sci.imageExtent = modes[0].parameters.visibleRegion;
    if (!vk(vkCreateDisplayPlaneSurfaceKHR(instance_, &sci, NULL, &surface_), "vkCreateDisplayPlaneSurfaceKHR")) return false;
    VkSurfaceCapabilitiesKHR caps;
    vkGetPhysicalDeviceSurfaceCapabilitiesKHR(phys_, surface_, &caps);
    VkSwapchainCreateInfoKHR swci;
    memset(&swci, 0, sizeof(swci));
    swci.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
    swci.surface = surface_;
    swci.minImageCount = caps.minImageCount > 2 ? caps.minImageCount : 2;
    swci.imageFormat = TARGET_FORMAT;
    swci.imageColorSpace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    swci.imageExtent.width = W;
    swci.imageExtent.height = H;
    swci.imageArrayLayers = 1;
    swci.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    swci.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    swci.preTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    swci.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    swci.presentMode = VK_PRESENT_MODE_FIFO_KHR;
    swci.clipped = VK_TRUE;
    if (!vk(vkCreateSwapchainKHR(dev_, &swci, NULL, &swapchain_), "vkCreateSwapchainKHR")) return false;
    uint32_t count = 0;
    vkGetSwapchainImagesKHR(dev_, swapchain_, &count, NULL);
    images_.resize(count);
    vkGetSwapchainImagesKHR(dev_, swapchain_, &count, &images_[0]);

    // The sprite masks' stencil: a D24S8 target beside the colour (vulkan4xenon
    // keeps it in eDRAM, where both fit at 1280x720 and one sample).
    VkImageCreateInfo dii;
    memset(&dii, 0, sizeof(dii));
    dii.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    dii.imageType = VK_IMAGE_TYPE_2D;
    dii.format = DEPTH_FORMAT;
    dii.extent.width = W;
    dii.extent.height = H;
    dii.extent.depth = 1;
    dii.mipLevels = 1;
    dii.arrayLayers = 1;
    dii.samples = VK_SAMPLE_COUNT_1_BIT;
    dii.tiling = VK_IMAGE_TILING_OPTIMAL;
    dii.usage = VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT;
    dii.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (!vk(vkCreateImage(dev_, &dii, NULL, &depth_image_), "vkCreateImage (stencil)")) return false;
    VkMemoryRequirements dreq;
    vkGetImageMemoryRequirements(dev_, depth_image_, &dreq);
    VkMemoryAllocateInfo dai;
    memset(&dai, 0, sizeof(dai));
    dai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    dai.allocationSize = dreq.size;
    dai.memoryTypeIndex = memory_type(dreq.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    if (!vk(vkAllocateMemory(dev_, &dai, NULL, &depth_mem_), "vkAllocateMemory (stencil)")) return false;
    vkBindImageMemory(dev_, depth_image_, depth_mem_, 0);
    VkImageViewCreateInfo dvi;
    memset(&dvi, 0, sizeof(dvi));
    dvi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    dvi.image = depth_image_;
    dvi.viewType = VK_IMAGE_VIEW_TYPE_2D;
    dvi.format = DEPTH_FORMAT;
    dvi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_DEPTH_BIT | VK_IMAGE_ASPECT_STENCIL_BIT;
    dvi.subresourceRange.levelCount = 1;
    dvi.subresourceRange.layerCount = 1;
    if (!vk(vkCreateImageView(dev_, &dvi, NULL, &depth_view_), "vkCreateImageView (stencil)")) return false;

    // One render pass: the swapchain image, cleared, then presented; the
    // stencil cleared with it and not kept.
    VkAttachmentDescription att[2];
    memset(att, 0, sizeof(att));
    VkAttachmentDescription& a = att[0];
    a.format = TARGET_FORMAT;
    a.samples = VK_SAMPLE_COUNT_1_BIT;
    a.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    a.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    a.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    a.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    a.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    a.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    att[1].format = DEPTH_FORMAT;
    att[1].samples = VK_SAMPLE_COUNT_1_BIT;
    att[1].loadOp = att[1].stencilLoadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    att[1].storeOp = att[1].stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    att[1].initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    att[1].finalLayout = VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;
    VkAttachmentReference ref = { 0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL };
    VkAttachmentReference dref = { 1, VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL };
    VkSubpassDescription sub;
    memset(&sub, 0, sizeof(sub));
    sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    sub.colorAttachmentCount = 1;
    sub.pColorAttachments = &ref;
    sub.pDepthStencilAttachment = &dref;
    VkRenderPassCreateInfo rpi;
    memset(&rpi, 0, sizeof(rpi));
    rpi.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO;
    rpi.attachmentCount = 2;
    rpi.pAttachments = att;
    rpi.subpassCount = 1;
    rpi.pSubpasses = &sub;
    if (!vk(vkCreateRenderPass(dev_, &rpi, NULL, &pass_), "vkCreateRenderPass")) return false;
    // The screen once more after a render texture's pass: what it held drawn back in.
    a.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
    a.initialLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    if (!vk(vkCreateRenderPass(dev_, &rpi, NULL, &pass_load_), "vkCreateRenderPass (screen again)")) return false;
    // A render texture: the same attachments (every pipeline works in both),
    // cleared, then read by the passes after it.
    a.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    a.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    a.finalLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    VkSubpassDependency dep[2];
    memset(dep, 0, sizeof(dep));
    dep[0].srcSubpass = VK_SUBPASS_EXTERNAL;
    dep[0].dstSubpass = 0;
    dep[0].srcStageMask = VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT;
    dep[0].dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dep[0].srcAccessMask = VK_ACCESS_SHADER_READ_BIT;
    dep[0].dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    dep[1].srcSubpass = 0;
    dep[1].dstSubpass = VK_SUBPASS_EXTERNAL;
    dep[1].srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dep[1].dstStageMask = VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT;
    dep[1].srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    dep[1].dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
    rpi.dependencyCount = 2;
    rpi.pDependencies = dep;
    if (!vk(vkCreateRenderPass(dev_, &rpi, NULL, &rt_pass_), "vkCreateRenderPass (render texture)")) return false;
    a.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
    a.initialLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    if (!vk(vkCreateRenderPass(dev_, &rpi, NULL, &rt_pass_load_), "vkCreateRenderPass (render texture again)")) return false;
    views_.resize(count);
    fbs_.resize(count);
    for (uint32_t i = 0; i < count; ++i) {
        VkImageViewCreateInfo vi;
        memset(&vi, 0, sizeof(vi));
        vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
        vi.image = images_[i];
        vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
        vi.format = TARGET_FORMAT;
        vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        vi.subresourceRange.levelCount = 1;
        vi.subresourceRange.layerCount = 1;
        if (!vk(vkCreateImageView(dev_, &vi, NULL, &views_[i]), "vkCreateImageView")) return false;
        VkImageView fbv[2] = { views_[i], depth_view_ };
        VkFramebufferCreateInfo fi;
        memset(&fi, 0, sizeof(fi));
        fi.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
        fi.renderPass = pass_;
        fi.attachmentCount = 2;
        fi.pAttachments = fbv;
        fi.width = W;
        fi.height = H;
        fi.layers = 1;
        if (!vk(vkCreateFramebuffer(dev_, &fi, NULL, &fbs_[i]), "vkCreateFramebuffer")) return false;
    }

    VkCommandPoolCreateInfo pci;
    memset(&pci, 0, sizeof(pci));
    pci.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    pci.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pci.queueFamilyIndex = family_;
    if (!vk(vkCreateCommandPool(dev_, &pci, NULL, &pool_), "vkCreateCommandPool")) return false;
    VkCommandBufferAllocateInfo cai;
    memset(&cai, 0, sizeof(cai));
    cai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    cai.commandPool = pool_;
    cai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    cai.commandBufferCount = 1;
    VkFenceCreateInfo fci;
    memset(&fci, 0, sizeof(fci));
    fci.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
    fci.flags = VK_FENCE_CREATE_SIGNALED_BIT;
    VkSemaphoreCreateInfo semi;
    memset(&semi, 0, sizeof(semi));
    semi.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
    for (int i = 0; i < FRAMES; ++i) {
        if (!vk(vkAllocateCommandBuffers(dev_, &cai, &frames_[i].cmd), "vkAllocateCommandBuffers")) return false;
        vkCreateFence(dev_, &fci, NULL, &frames_[i].fence);
        vkCreateSemaphore(dev_, &semi, NULL, &frames_[i].acquired);
        vkCreateSemaphore(dev_, &semi, NULL, &frames_[i].rendered);
        VkQueryPoolCreateInfo qci;
        memset(&qci, 0, sizeof(qci));
        qci.sType = VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO;
        qci.queryType = VK_QUERY_TYPE_TIMESTAMP;
        qci.queryCount = QSLOTS;
        frames_[i].queries = VK_NULL_HANDLE;
        frames_[i].query_count = 0;
        if (vkCreateQueryPool(dev_, &qci, NULL, &frames_[i].queries) != VK_SUCCESS) frames_[i].queries = VK_NULL_HANDLE;
    }
    if (!vk(vkAllocateCommandBuffers(dev_, &cai, &upload_cmd_), "vkAllocateCommandBuffers")) return false;
    fci.flags = 0;
    vkCreateFence(dev_, &fci, NULL, &upload_fence_);

    VkDescriptorSetLayoutBinding b;
    memset(&b, 0, sizeof(b));
    b.binding = 0;
    b.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    b.descriptorCount = 1;
    b.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    VkDescriptorSetLayoutCreateInfo li;
    memset(&li, 0, sizeof(li));
    li.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    li.bindingCount = 1;
    li.pBindings = &b;
    if (!vk(vkCreateDescriptorSetLayout(dev_, &li, NULL, &set_layout_), "vkCreateDescriptorSetLayout")) return false;
    VkPushConstantRange push;
    push.stageFlags = VK_SHADER_STAGE_VERTEX_BIT;
    push.offset = 0;
    push.size = 16;
    VkPipelineLayoutCreateInfo pli;
    memset(&pli, 0, sizeof(pli));
    pli.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    pli.setLayoutCount = 1;
    pli.pSetLayouts = &set_layout_;
    pli.pushConstantRangeCount = 1;
    pli.pPushConstantRanges = &push;
    if (!vk(vkCreatePipelineLayout(dev_, &pli, NULL, &pipe_layout_), "vkCreatePipelineLayout")) return false;
    VkDescriptorSetLayout three[3] = { set_layout_, set_layout_, set_layout_ };
    VkPushConstantRange ray_push[2];
    ray_push[0] = push;
    ray_push[1].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    ray_push[1].offset = 16;
    ray_push[1].size = 16;
    pli.setLayoutCount = 3;
    pli.pSetLayouts = three;
    pli.pushConstantRangeCount = 2;
    pli.pPushConstantRanges = ray_push;
    if (!vk(vkCreatePipelineLayout(dev_, &pli, NULL, &ray_layout_), "vkCreatePipelineLayout (light ray)")) return false;
    ray_push[1].size = 48;
    pli.setLayoutCount = 2;
    if (!vk(vkCreatePipelineLayout(dev_, &pli, NULL, &fx_layout_), "vkCreatePipelineLayout (effects)")) return false;
    VkDescriptorPoolSize ps;
    ps.type = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    ps.descriptorCount = 4096;
    VkDescriptorPoolCreateInfo dpi;
    memset(&dpi, 0, sizeof(dpi));
    dpi.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    dpi.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
    dpi.maxSets = 4096;
    dpi.poolSizeCount = 1;
    dpi.pPoolSizes = &ps;
    if (!vk(vkCreateDescriptorPool(dev_, &dpi, NULL, &desc_pool_), "vkCreateDescriptorPool")) return false;
    VkSamplerCreateInfo smi;
    memset(&smi, 0, sizeof(smi));
    smi.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    smi.magFilter = smi.minFilter = VK_FILTER_LINEAR;
    smi.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
    smi.addressModeU = smi.addressModeV = smi.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    if (!vk(vkCreateSampler(dev_, &smi, NULL, &sampler_), "vkCreateSampler")) return false;

    VkShaderModule vert = module(ch360_vert_spirv, CH360_VERT_SPIRV_BYTES);
    frags_[MODE_SPRITE] = module(sprite_frag_spirv, SPRITE_FRAG_SPIRV_BYTES);
    frags_[MODE_SCREEN] = module(screen_frag_spirv, SCREEN_FRAG_SPIRV_BYTES);
    frags_[MODE_MULTIPLY] = module(multiply_frag_spirv, MULTIPLY_FRAG_SPIRV_BYTES);
    frags_[MODE_OVERLAY] = module(overlay_frag_spirv, OVERLAY_FRAG_SPIRV_BYTES);
    frags_[MODE_SDF] = module(sdf_frag_spirv, SDF_FRAG_SPIRV_BYTES);
    frags_[MODE_COLOROVERLAY] = module(coloroverlay_frag_spirv, COLOROVERLAY_FRAG_SPIRV_BYTES);
    frags_[MODE_LIGHTRAY] = module(lightray_frag_spirv, LIGHTRAY_FRAG_SPIRV_BYTES);
    frags_[MODE_KNIGHT] = module(knightglow_frag_spirv, KNIGHTGLOW_FRAG_SPIRV_BYTES);
    frags_[MODE_DARK] = module(darkness_frag_spirv, DARKNESS_FRAG_SPIRV_BYTES);
    frags_[MODE_BLURSPRITE] = module(blursprite_frag_spirv, BLURSPRITE_FRAG_SPIRV_BYTES);
    frags_[MODE_COPY] = module(copy_frag_spirv, COPY_FRAG_SPIRV_BYTES);
    frags_[MODE_DOWN4] = module(down4_frag_spirv, DOWN4_FRAG_SPIRV_BYTES);
    frags_[MODE_BLUR7] = module(blur7_frag_spirv, BLUR7_FRAG_SPIRV_BYTES);
    frags_[MODE_BLUR7G] = module(blur7g_frag_spirv, BLUR7G_FRAG_SPIRV_BYTES);
    frags_[MODE_TWOSTRIP] = module(twostrip_frag_spirv, TWOSTRIP_FRAG_SPIRV_BYTES);
    frags_[MODE_BW] = module(bw_frag_spirv, BW_FRAG_SPIRV_BYTES);
    frags_[MODE_GRAIN] = module(filmgrain_frag_spirv, FILMGRAIN_FRAG_SPIRV_BYTES);
    frags_[MODE_BLUR7A] = module(blur7a_frag_spirv, BLUR7A_FRAG_SPIRV_BYTES);
    pass_frags_[PF_SPRITE] = frags_[MODE_SPRITE];
    pass_frags_[PF_INV] = module(am_inv_frag_spirv, AM_INV_FRAG_SPIRV_BYTES);
    pass_frags_[PF_STRAIGHT] = module(am_straight_frag_spirv, AM_STRAIGHT_FRAG_SPIRV_BYTES);
    pass_frags_[PF_SQ] = module(am_sq_frag_spirv, AM_SQ_FRAG_SPIRV_BYTES);
    pass_frags_[PF_A] = module(am_a_frag_spirv, AM_A_FRAG_SPIRV_BYTES);
    pass_frags_[PF_CLEAR] = module(am_clear_frag_spirv, AM_CLEAR_FRAG_SPIRV_BYTES);
    pass_frags_[PF_SCREEN] = module(am_screen_frag_spirv, AM_SCREEN_FRAG_SPIRV_BYTES);
    pass_frags_[PF_STENCIL] = module(stencilmask_frag_spirv, STENCILMASK_FRAG_SPIRV_BYTES);
    pass_frags_[PF_BLENDK] = module(blendk_frag_spirv, BLENDK_FRAG_SPIRV_BYTES);
    pass_frags_[PF_BLENDM] = module(blendm_frag_spirv, BLENDM_FRAG_SPIRV_BYTES);
    pass_frags_[PF_BLENDN] = module(blendn_frag_spirv, BLENDN_FRAG_SPIRV_BYTES);
    pass_frags_[PF_MONO] = module(mono_frag_spirv, MONO_FRAG_SPIRV_BYTES);
    pass_frags_[PF_BAND] = module(band_frag_spirv, BAND_FRAG_SPIRV_BYTES);
    pass_frags_[PF_CAPPED] = module(capped_frag_spirv, CAPPED_FRAG_SPIRV_BYTES);
    pass_frags_[PF_HONEY] = module(honey_frag_spirv, HONEY_FRAG_SPIRV_BYTES);
    pass_frags_[PF_ICE] = module(ice_frag_spirv, ICE_FRAG_SPIRV_BYTES);
    pass_frags_[PF_GRABOVL] = module(grabovl_frag_spirv, GRABOVL_FRAG_SPIRV_BYTES);
    mask_frag_ = module(mask_frag_spirv, MASK_FRAG_SPIRV_BYTES);
    vert_ = vert;
    memset(pipes_, 0, sizeof(pipes_));
    // The sprites' pipelines now; the effects' when first drawn.
    for (int i = 0; i < FIRST_FX; ++i)
        if (!pipeline(i, STENCIL_NONE)) return false;
    for (int s = 0; s < 2; ++s)
        if (!(mask_pipes_[s] = make_pipeline(mask_frag_, pipe_layout_, STENCIL_ADD + s))) return false;
    void* vm = NULL;
    if (!make_buffer(&vbuf_, &vmem_, &vm, VERTEX_RING * FRAMES, VK_BUFFER_USAGE_VERTEX_BUFFER_BIT)) return false;
    vmap_ = (unsigned char*)vm;
    void* rm = NULL;
    if (!make_buffer(&ring_buf_, &ring_mem_, &rm, STAGING_RING, VK_BUFFER_USAGE_TRANSFER_SRC_BIT)) return false;
    ring_map_ = (unsigned char*)rm;
    ring_used_ = 0;
    logf("vk: staging in %s memory", staging_cached_ ? "cached" : "write-combined");
    bounce_.resize(256 * 1024);
    unsigned char px[4] = { 255, 255, 255, 255 };
    if (!upload(white_, TARGET_FORMAT, 1, 1, px, 4)) return false;
    VkPhysicalDeviceProperties props;
    vkGetPhysicalDeviceProperties(phys_, &props);
    logf("vk: %s, %dx%d, %u swapchain images", props.deviceName, W, H, count);
    ts_period_ms_ = props.limits.timestampPeriod * 1e-6;
    logf("vk: GPU time stamps %s (%.3f ns a tick, every 4th frame)", frames_[0].queries ? "on" : "unavailable", props.limits.timestampPeriod);
    return true;
}

// A mode's pipeline with a stencil test, made the first time a frame asks for it.
VkPipeline Renderer::pipeline(int mode, int stencil) {
    if (!pipes_[mode][stencil])
        pipes_[mode][stencil] = make_pipeline(frags_[mode], mode == MODE_LIGHTRAY ? ray_layout_ : mode >= FIRST_FX ? fx_layout_ : pipe_layout_,
                                              stencil, mode >= FIRST_BLIT ? BLEND_REPLACE : BLEND_PREMUL);
    return pipes_[mode][stencil];
}

// A pass of the game's blend and mask shaders: its pipeline, made on first use
// (the effects' layout: their programs read one texture and at most p0).
VkPipeline Renderer::pass_pipeline(const FxPass& p) {
    unsigned key = (unsigned)p.frag | ((unsigned)p.blend << 8) | ((unsigned)p.stencil << 16) | ((unsigned)p.ref << 24);
    std::map<unsigned, VkPipeline>::iterator i = pass_pipes_.find(key);
    if (i != pass_pipes_.end()) return i->second;
    VkPipeline pipe = make_pipeline(pass_frags_[p.frag], fx_layout_, p.stencil, p.blend, p.ref);
    pass_pipes_[key] = pipe;
    return pipe;
}

// stencil: STENCIL_NONE; STENCIL_INSIDE/OUTSIDE draw where the stencil is 1 or
// more / 0 (Unity's sprite mask interaction: reference 1, LessEqual / Greater);
// STENCIL_ADD/REMOVE add or take away one where the shader keeps the pixel,
// writing no colour (Unity's sprite mask: IncrSat / DecrSat); STENCIL_WRITE
// writes ref, STENCIL_EQUAL draws where the stencil is ref (the game's stencil
// masks). blend: premultiplied over, a blit's replace (Unity's Blend One
// Zero), or one of the game's blend and mask shaders' states.
VkPipeline Renderer::make_pipeline(VkShaderModule frag, VkPipelineLayout layout, int stencil, int blend, int ref) {
    VkPipelineShaderStageCreateInfo st[2];
    memset(st, 0, sizeof(st));
    st[0].sType = st[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    st[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
    st[0].module = vert_;
    st[0].pName = "main";
    st[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
    st[1].module = frag;
    st[1].pName = "main";
    // ch360_vertex: position and uv as floats, the colour and blend colour 8 bits each by address.
    VkVertexInputBindingDescription vb = { 0, (uint32_t)sizeof(ch360_vertex), VK_VERTEX_INPUT_RATE_VERTEX };
    VkVertexInputAttributeDescription va[4] = { { 0, 0, VK_FORMAT_R32G32_SFLOAT, 0 },
                                                { 1, 0, VK_FORMAT_R8G8B8A8_UNORM, 16 },
                                                { 2, 0, VK_FORMAT_R32G32_SFLOAT, 8 },
                                                { 3, 0, VK_FORMAT_R8G8B8A8_UNORM, 20 } };
    VkPipelineVertexInputStateCreateInfo vi;
    memset(&vi, 0, sizeof(vi));
    vi.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO;
    vi.vertexBindingDescriptionCount = 1;
    vi.pVertexBindingDescriptions = &vb;
    vi.vertexAttributeDescriptionCount = 4;
    vi.pVertexAttributeDescriptions = va;
    VkPipelineInputAssemblyStateCreateInfo ia;
    memset(&ia, 0, sizeof(ia));
    ia.sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO;
    ia.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    VkPipelineViewportStateCreateInfo vps;
    memset(&vps, 0, sizeof(vps));
    vps.sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO;
    vps.viewportCount = 1;
    vps.scissorCount = 1;
    VkPipelineRasterizationStateCreateInfo rs;
    memset(&rs, 0, sizeof(rs));
    rs.sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO;
    rs.polygonMode = VK_POLYGON_MODE_FILL;
    rs.cullMode = VK_CULL_MODE_NONE;
    rs.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE;
    rs.lineWidth = 1.0f;
    VkPipelineMultisampleStateCreateInfo ms;
    memset(&ms, 0, sizeof(ms));
    ms.sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO;
    ms.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
    // Premultiplied alpha over: the shaders output colour * alpha.
    VkPipelineColorBlendAttachmentState ba;
    memset(&ba, 0, sizeof(ba));
    ba.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
    ba.blendEnable = VK_TRUE;
    ba.colorBlendOp = ba.alphaBlendOp = VK_BLEND_OP_ADD;
    ba.srcColorBlendFactor = ba.srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
    ba.dstColorBlendFactor = ba.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    const VkBlendFactor ZERO = VK_BLEND_FACTOR_ZERO, ONE = VK_BLEND_FACTOR_ONE, DA = VK_BLEND_FACTOR_DST_ALPHA,
                        IDA = VK_BLEND_FACTOR_ONE_MINUS_DST_ALPHA;
    switch (blend) {
    case BLEND_REPLACE: ba.blendEnable = VK_FALSE; break;
    // Alpha alone, replaced (AlphaMask, AlphaMaskClearer), or worn away (AlphaMasked's first pass).
    case BLEND_MASK_WRITE: ba.srcColorBlendFactor = ZERO; ba.dstColorBlendFactor = ONE; ba.srcAlphaBlendFactor = ONE; ba.dstAlphaBlendFactor = ZERO; break;
    case BLEND_MASKED0: ba.srcColorBlendFactor = ZERO; ba.dstColorBlendFactor = ONE; ba.srcAlphaBlendFactor = IDA; ba.dstAlphaBlendFactor = ONE; break;
    // Colour by the target's alpha, alpha kept.
    case BLEND_MASKED1: ba.srcColorBlendFactor = IDA; ba.dstColorBlendFactor = DA; ba.srcAlphaBlendFactor = ZERO; ba.dstAlphaBlendFactor = ONE; break;
    case BLEND_REV_DA: case BLEND_ADD_DA: case BLEND_REV_IDA: case BLEND_ADD_IDA:
        ba.srcColorBlendFactor = blend == BLEND_REV_DA || blend == BLEND_ADD_DA ? DA : IDA;
        ba.dstColorBlendFactor = ONE;
        ba.colorBlendOp = blend == BLEND_REV_DA || blend == BLEND_REV_IDA ? VK_BLEND_OP_REVERSE_SUBTRACT : VK_BLEND_OP_ADD;
        ba.srcAlphaBlendFactor = ZERO;
        ba.dstAlphaBlendFactor = ONE;
        break;
    case BLEND_NO_COLOR: ba.colorWriteMask = 0; break;
    // The target times the colour; plus it (alpha as Unity's SrcAlpha blend); less it.
    case BLEND_MUL: ba.srcColorBlendFactor = ZERO; ba.dstColorBlendFactor = VK_BLEND_FACTOR_SRC_COLOR; ba.srcAlphaBlendFactor = ZERO; ba.dstAlphaBlendFactor = ONE; break;
    case BLEND_ADD_LIT: ba.srcColorBlendFactor = ONE; ba.dstColorBlendFactor = ONE; ba.srcAlphaBlendFactor = VK_BLEND_FACTOR_SRC_ALPHA; break;
    case BLEND_REV_SUB:
        ba.srcColorBlendFactor = ONE; ba.dstColorBlendFactor = ONE; ba.colorBlendOp = VK_BLEND_OP_REVERSE_SUBTRACT;
        ba.srcAlphaBlendFactor = ZERO; ba.dstAlphaBlendFactor = ONE;
        break;
    default: break;
    }
    VkPipelineColorBlendStateCreateInfo cb;
    memset(&cb, 0, sizeof(cb));
    cb.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO;
    cb.attachmentCount = 1;
    cb.pAttachments = &ba;
    if (stencil == STENCIL_ADD || stencil == STENCIL_REMOVE) ba.colorWriteMask = 0;
    VkPipelineDepthStencilStateCreateInfo dss;
    memset(&dss, 0, sizeof(dss));
    dss.sType = VK_STRUCTURE_TYPE_PIPELINE_DEPTH_STENCIL_STATE_CREATE_INFO;
    dss.depthCompareOp = VK_COMPARE_OP_ALWAYS;
    if (stencil != STENCIL_NONE) {
        VkStencilOpState so;
        memset(&so, 0, sizeof(so));
        so.failOp = so.depthFailOp = VK_STENCIL_OP_KEEP;
        so.passOp = stencil == STENCIL_ADD ? VK_STENCIL_OP_INCREMENT_AND_CLAMP
                  : stencil == STENCIL_REMOVE ? VK_STENCIL_OP_DECREMENT_AND_CLAMP
                  : stencil == STENCIL_WRITE ? VK_STENCIL_OP_REPLACE : VK_STENCIL_OP_KEEP;
        so.compareOp = stencil == STENCIL_INSIDE ? VK_COMPARE_OP_LESS_OR_EQUAL
                     : stencil == STENCIL_OUTSIDE || stencil == STENCIL_GREATER ? VK_COMPARE_OP_GREATER
                     : stencil == STENCIL_EQUAL ? VK_COMPARE_OP_EQUAL : VK_COMPARE_OP_ALWAYS;
        so.compareMask = 0xFF;
        so.writeMask = stencil == STENCIL_ADD || stencil == STENCIL_REMOVE || stencil == STENCIL_WRITE ? 0xFF : 0;
        so.reference = (uint32_t)ref;
        dss.stencilTestEnable = VK_TRUE;
        dss.front = dss.back = so;
    }
    VkDynamicState dyn[2] = { VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR };
    VkPipelineDynamicStateCreateInfo ds;
    memset(&ds, 0, sizeof(ds));
    ds.sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO;
    ds.dynamicStateCount = 2;
    ds.pDynamicStates = dyn;
    VkGraphicsPipelineCreateInfo ci;
    memset(&ci, 0, sizeof(ci));
    ci.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO;
    ci.stageCount = 2;
    ci.pStages = st;
    ci.pVertexInputState = &vi;
    ci.pInputAssemblyState = &ia;
    ci.pViewportState = &vps;
    ci.pRasterizationState = &rs;
    ci.pMultisampleState = &ms;
    ci.pColorBlendState = &cb;
    ci.pDepthStencilState = &dss;
    ci.pDynamicState = &ds;
    ci.layout = layout;
    ci.renderPass = pass_;
    VkPipeline p = VK_NULL_HANDLE;
    vk(vkCreateGraphicsPipelines(dev_, VK_NULL_HANDLE, 1, &ci, NULL, &p), "vkCreateGraphicsPipelines");
    return p;
}

// A texture's image, memory, view and descriptor set. Room comes first from
// textures not drawn lately: down to the budget, then above the memory floor,
// then once more if the allocation still fails.
bool Renderer::make_image(Tex& t, VkFormat format, int w, int h, VkImageUsageFlags usage) {
    // In use from now: making room below must not take this texture's own
    // image (never drawn, it was the oldest; the sixth run crashed so).
    t.last_used = frame_no_;
    VkImageCreateInfo ci;
    memset(&ci, 0, sizeof(ci));
    ci.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ci.imageType = VK_IMAGE_TYPE_2D;
    ci.format = format;
    ci.extent.width = (uint32_t)w;
    ci.extent.height = (uint32_t)h;
    ci.extent.depth = 1;
    ci.mipLevels = 1;
    ci.arrayLayers = 1;
    ci.samples = VK_SAMPLE_COUNT_1_BIT;
    ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = usage;
    ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ci.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (!vk(vkCreateImage(dev_, &ci, NULL, &t.image), "vkCreateImage")) {
        t.image = VK_NULL_HANDLE;
        return false;
    }
    VkMemoryRequirements req;
    vkGetImageMemoryRequirements(dev_, t.image, &req);
    size_t size = (size_t)req.size;
    if (texture_bytes_ + size > TEXTURE_BUDGET) release_unused(texture_bytes_ + size - TEXTURE_BUDGET);
    unsigned free_mb, total_mb;
    memory_mb(&free_mb, &total_mb);
    unsigned need_mb = MEMORY_FLOOR_MB + (unsigned)(size >> 20) + 1;
    if (free_mb < need_mb) {
        size_t want = (size_t)(need_mb - free_mb) << 20;
        release_unused(want);
        memory_mb(&free_mb, &total_mb);
    }
    VkResult r = VK_ERROR_OUT_OF_DEVICE_MEMORY;
    if (free_mb >= need_mb) {
        VkMemoryAllocateInfo ai;
        memset(&ai, 0, sizeof(ai));
        ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
        ai.allocationSize = req.size;
        ai.memoryTypeIndex = memory_type(req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        r = vkAllocateMemory(dev_, &ai, NULL, &t.mem);
        if (r != VK_SUCCESS && release_unused(size * 2) > 0) r = vkAllocateMemory(dev_, &ai, NULL, &t.mem);
    }
    if (r != VK_SUCCESS) {
        logf("vk: no memory for a %dx%d texture (%u KB): %u MB free, textures %u MB", w, h, (unsigned)(size >> 10), free_mb,
             (unsigned)(texture_bytes_ >> 20));
        vkDestroyImage(dev_, t.image, NULL);
        t.image = VK_NULL_HANDLE;
        t.mem = VK_NULL_HANDLE;
        return false;
    }
    vkBindImageMemory(dev_, t.image, t.mem, 0);
    VkImageViewCreateInfo vi;
    memset(&vi, 0, sizeof(vi));
    vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    vi.image = t.image;
    vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
    vi.format = format;
    vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    vi.subresourceRange.levelCount = 1;
    vi.subresourceRange.layerCount = 1;
    if (!vk(vkCreateImageView(dev_, &vi, NULL, &t.view), "vkCreateImageView")) {
        vkDestroyImage(dev_, t.image, NULL);
        vkFreeMemory(dev_, t.mem, NULL);
        t.image = VK_NULL_HANDLE;
        t.mem = VK_NULL_HANDLE;
        t.view = VK_NULL_HANDLE;
        return false;
    }
    VkDescriptorSetAllocateInfo sai;
    memset(&sai, 0, sizeof(sai));
    sai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    sai.descriptorPool = desc_pool_;
    sai.descriptorSetCount = 1;
    sai.pSetLayouts = &set_layout_;
    if (!vk(vkAllocateDescriptorSets(dev_, &sai, &t.set), "vkAllocateDescriptorSets")) {
        vkDestroyImageView(dev_, t.view, NULL);
        vkDestroyImage(dev_, t.image, NULL);
        vkFreeMemory(dev_, t.mem, NULL);
        t.image = VK_NULL_HANDLE;
        t.mem = VK_NULL_HANDLE;
        t.view = VK_NULL_HANDLE;
        t.set = VK_NULL_HANDLE;
        return false;
    }
    VkDescriptorImageInfo ii;
    ii.sampler = sampler_;
    ii.imageView = t.view;
    ii.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    VkWriteDescriptorSet wd;
    memset(&wd, 0, sizeof(wd));
    wd.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
    wd.dstSet = t.set;
    wd.descriptorCount = 1;
    wd.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    wd.pImageInfo = &ii;
    vkUpdateDescriptorSets(dev_, 1, &wd, 0, NULL);
    t.w = w;
    t.h = h;
    t.format = format;
    t.bytes = size;
    texture_bytes_ += t.bytes;
    if (t.evicted && t.reloads < 255) ++t.reloads;
    t.evicted = false;
    return true;
}

// Uploads are recorded into one command buffer that runs before the frame's
// (the frame submits it and waits: scene loads, never the steady state), or
// sooner when the staging ring is full.
void Renderer::flush_uploads() {
    if (!upload_open_) return;
    Stage marker("vk: uploading textures");
    vkEndCommandBuffer(upload_cmd_);
    VkSubmitInfo si;
    memset(&si, 0, sizeof(si));
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &upload_cmd_;
    vkQueueSubmit(queue_, 1, &si, upload_fence_);
    vkWaitForFences(dev_, 1, &upload_fence_, VK_TRUE, ~(uint64_t)0);
    vkResetFences(dev_, 1, &upload_fence_);
    upload_open_ = false;
    for (size_t i = 0; i < staging_.size(); ++i) {
        vkDestroyBuffer(dev_, staging_[i].first, NULL);
        vkFreeMemory(dev_, staging_[i].second, NULL);
    }
    staging_.clear();
    ring_used_ = 0;
}

// Room for bytes of upload data: in the ring (run what it holds first if it
// is full), or a buffer of its own for data larger than the ring.
unsigned char* Renderer::staging(size_t bytes, VkBuffer* buf, VkDeviceSize* off) {
    size_t need = (bytes + 4095) & ~(size_t)4095;
    if (need <= STAGING_RING) {
        if (ring_used_ + need > STAGING_RING) flush_uploads();
        *buf = ring_buf_;
        *off = ring_used_;
        ring_used_ += need;
        return ring_map_ + *off;
    }
    flush_uploads();
    VkBuffer sb;
    VkDeviceMemory sm;
    void* map = NULL;
    if (!make_buffer(&sb, &sm, &map, bytes, VK_BUFFER_USAGE_TRANSFER_SRC_BIT) &&
        (release_unused(bytes) == 0 || !make_buffer(&sb, &sm, &map, bytes, VK_BUFFER_USAGE_TRANSFER_SRC_BIT)))
        return NULL;
    staging_.push_back(std::make_pair(sb, sm));
    *buf = sb;
    *off = 0;
    return (unsigned char*)map;
}

void Renderer::record_copy(Tex& t, VkBuffer buf, VkDeviceSize off) {
    if (!upload_open_) {
        VkCommandBufferBeginInfo bi;
        memset(&bi, 0, sizeof(bi));
        bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
        bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        vkResetCommandBuffer(upload_cmd_, 0);
        vkBeginCommandBuffer(upload_cmd_, &bi);
        upload_open_ = true;
    }
    VkImageMemoryBarrier bar;
    memset(&bar, 0, sizeof(bar));
    bar.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    bar.srcAccessMask = VK_ACCESS_SHADER_READ_BIT;
    bar.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    bar.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    bar.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    bar.srcQueueFamilyIndex = bar.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    bar.image = t.image;
    bar.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    bar.subresourceRange.levelCount = 1;
    bar.subresourceRange.layerCount = 1;
    vkCmdPipelineBarrier(upload_cmd_, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT | VK_PIPELINE_STAGE_TRANSFER_BIT,
                         VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, NULL, 0, NULL, 1, &bar);
    VkBufferImageCopy region;
    memset(&region, 0, sizeof(region));
    region.bufferOffset = off;
    region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    region.imageSubresource.layerCount = 1;
    region.imageExtent.width = (uint32_t)t.w;
    region.imageExtent.height = (uint32_t)t.h;
    region.imageExtent.depth = 1;
    vkCmdCopyBufferToImage(upload_cmd_, buf, t.image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
    bar.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    bar.dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
    bar.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    bar.newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    vkCmdPipelineBarrier(upload_cmd_, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, 0, 0, NULL, 0,
                         NULL, 1, &bar);
    ++uploads;
}

bool Renderer::upload(Tex& t, VkFormat format, int w, int h, const unsigned char* data, size_t bytes) {
    if (t.image && (t.w != w || t.h != h || t.format != format)) destroy(t, false);
    if (!t.image && !make_image(t, format, w, h)) return false;
    VkBuffer buf;
    VkDeviceSize off;
    unsigned char* dst = staging(bytes, &buf, &off);
    if (!dst) return false;
    g_memcpy(dst, data, bytes);
    record_copy(t, buf, off);
    return true;
}

void Renderer::destroy(Tex& t, bool now) {
    if (!t.image) return;
    Garbage g = { t.image, t.mem, t.view, t.set, VK_NULL_HANDLE };
    if (now) {
        vkFreeDescriptorSets(dev_, desc_pool_, 1, &g.set);
        vkDestroyImageView(dev_, g.view, NULL);
        vkDestroyImage(dev_, g.image, NULL);
        vkFreeMemory(dev_, g.mem, NULL);
    } else {
        frames_[frame_].dead.push_back(g);
    }
    texture_bytes_ -= t.bytes;
    t.image = VK_NULL_HANDLE;
    t.mem = VK_NULL_HANDLE;
    t.view = VK_NULL_HANDLE;
    t.set = VK_NULL_HANDLE;
    t.bytes = 0;
}

Tex* Renderer::tex(int id) {
    if (id < 0 || id > 65535) return NULL;
    if ((size_t)id >= textures_.size()) textures_.resize((size_t)id + 1);
    return &textures_[(size_t)id];
}

// Pass 1: a texture an item draws, loaded (render textures are drawn this frame).
void Renderer::load_for(int id) {
    if (id < 0 || id >= CH360_RT_BASE) return;
    Tex* t = tex(id);
    if (t) {
        if (g_loader && !t->image && !t->failed && !t->key.empty() && t->waited_frame != frame_no_ && frame_no_ >= t->retry_at) {
            t->waited_frame = frame_no_;
            if (t->loading) ++waits_.reading;          // asked ahead (or by an earlier frame), still being read
            else if (t->pre_wanted) ++waits_.queued;   // asked ahead, held back (no room or the rings full)
            else if (t->evicted) ++waits_.let_go;      // let go for room since it was last loaded
            else ++waits_.never;
        }
        load(id, *t);
        t->last_used = frame_no_;
        t->unused_pre = false;
        // Drawn this frame: waited for (finish_waits) rather than drawn without it (the sixteenth run's
        // flicker: the logo, transitions, the onion's new pages); also when the in-flight cap kept it back.
        if (g_loader && (t->loading || (!t->image && !t->failed && !t->key.empty() && frame_no_ >= t->retry_at))) waiting_.push_back(id);
    }
}

// The texture an item samples: a converted or run-time one, or a render
// texture (NULL until something drew it).
Tex* Renderer::sampled(int id) {
    if (id < CH360_RT_BASE) return tex(id);
    std::map<unsigned, Target>::iterator i = targets_.find((unsigned)(id - CH360_RT_BASE));
    if (i == targets_.end() || !i->second.tex.image) return NULL;
    i->second.last_used = frame_no_;
    return &i->second.tex;
}

// Depth and stencil for a render texture's pass (eDRAM only; vulkan4xenon
// resolves no depth out): the screen's at its size, else one per size.
VkImageView Renderer::depth_for(int w, int h) {
    if (w == W && h == H) return depth_view_;
    std::map<std::pair<int, int>, DepthBuf>::iterator i = depths_.find(std::make_pair(w, h));
    if (i != depths_.end()) return i->second.view;
    DepthBuf d;
    memset(&d, 0, sizeof(d));
    VkImageCreateInfo ci;
    memset(&ci, 0, sizeof(ci));
    ci.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ci.imageType = VK_IMAGE_TYPE_2D;
    ci.format = DEPTH_FORMAT;
    ci.extent.width = (uint32_t)w;
    ci.extent.height = (uint32_t)h;
    ci.extent.depth = 1;
    ci.mipLevels = 1;
    ci.arrayLayers = 1;
    ci.samples = VK_SAMPLE_COUNT_1_BIT;
    ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT;
    ci.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (!vk(vkCreateImage(dev_, &ci, NULL, &d.image), "vkCreateImage (render texture stencil)")) return VK_NULL_HANDLE;
    VkMemoryRequirements req;
    vkGetImageMemoryRequirements(dev_, d.image, &req);
    VkMemoryAllocateInfo ai;
    memset(&ai, 0, sizeof(ai));
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = memory_type(req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    if (!vk(vkAllocateMemory(dev_, &ai, NULL, &d.mem), "vkAllocateMemory (render texture stencil)")) {
        vkDestroyImage(dev_, d.image, NULL);
        return VK_NULL_HANDLE;
    }
    vkBindImageMemory(dev_, d.image, d.mem, 0);
    VkImageViewCreateInfo vi;
    memset(&vi, 0, sizeof(vi));
    vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    vi.image = d.image;
    vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
    vi.format = DEPTH_FORMAT;
    vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_DEPTH_BIT | VK_IMAGE_ASPECT_STENCIL_BIT;
    vi.subresourceRange.levelCount = 1;
    vi.subresourceRange.layerCount = 1;
    if (!vk(vkCreateImageView(dev_, &vi, NULL, &d.view), "vkCreateImageView (render texture stencil)")) {
        vkDestroyImage(dev_, d.image, NULL);
        vkFreeMemory(dev_, d.mem, NULL);
        return VK_NULL_HANDLE;
    }
    depths_[std::make_pair(w, h)] = d;
    return d.view;
}

// A render texture to draw into, made (or remade at a new size) on first use.
// Its memory counts against the texture budget.
Renderer::Target* Renderer::target(unsigned id, int w, int h) {
    if (w <= 0 || h <= 0 || w > 2048 || h > 2048) return NULL;
    Target& t = targets_[id];
    t.last_used = frame_no_;
    if (t.tex.image && (t.tex.w != w || t.tex.h != h)) destroy_target(t);
    if (t.tex.image) return &t;
    if (frame_no_ < t.tex.retry_at) return NULL;
    VkImageView depth = depth_for(w, h);
    if (!depth || !make_image(t.tex, TARGET_FORMAT, w, h, VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_SAMPLED_BIT)) {
        t.tex.retry_at = frame_no_ + 120;
        logf("vk: no memory for render texture %u (%dx%d)", id, w, h);
        return NULL;
    }
    VkImageView fbv[2] = { t.tex.view, depth };
    VkFramebufferCreateInfo fi;
    memset(&fi, 0, sizeof(fi));
    fi.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
    fi.renderPass = rt_pass_;
    fi.attachmentCount = 2;
    fi.pAttachments = fbv;
    fi.width = (uint32_t)w;
    fi.height = (uint32_t)h;
    fi.layers = 1;
    if (!vk(vkCreateFramebuffer(dev_, &fi, NULL, &t.fb), "vkCreateFramebuffer (render texture)")) {
        destroy(t.tex, true);
        t.fb = VK_NULL_HANDLE;
        t.tex.retry_at = frame_no_ + 120;
        return NULL;
    }
    unsigned free_mb, total_mb;
    memory_mb(&free_mb, &total_mb);
    logf("vk: render texture %u, %dx%d; %u MB free, textures %u MB", id, w, h, free_mb, (unsigned)(texture_bytes_ >> 20));
    return &t;
}

void Renderer::destroy_target(Target& t) {
    if (t.fb) {
        Garbage g = { VK_NULL_HANDLE, VK_NULL_HANDLE, VK_NULL_HANDLE, VK_NULL_HANDLE, t.fb };
        frames_[frame_].dead.push_back(g);
        t.fb = VK_NULL_HANDLE;
    }
    destroy(t.tex, false);
}

// Render textures not drawn or sampled for 300 frames (a scene left behind).
void Renderer::free_targets() {
    for (std::map<unsigned, Target>::iterator i = targets_.begin(); i != targets_.end();) {
        if (frame_no_ - i->second.last_used > 300) {
            destroy_target(i->second);
            targets_.erase(i++);
        } else {
            ++i;
        }
    }
}

void Renderer::texture_file(int id, const char* key) {
    Tex* t = tex(id);
    if (t) {
        if (t->key != key && !t->loading) forget_stored(*t);
        t->key = key;
        t->failed = false;
        // The film grain shows its 64 frames in turn, each every two seconds: evicted as the least
        // recently used, each came back from the USB drive a second later (the fifteenth run's map: texture
        // loads 4-8 ms a frame, 586 grain loads). Its 8 MB stay.
        t->pinned = strncmp(key, "bundles_tex_screen_fx_", 22) == 0;
    }
}

void Renderer::texture_pixels(int id, int w, int h, const unsigned char* bgra) {
    Tex* t = tex(id);
    if (t) {
        t->key.clear();
        upload(*t, TARGET_FORMAT, w, h, bgra, (size_t)w * h * 4);
    }
}

// A converted texture (tools/convert_console.py's .ctx, or a full-size
// sprite page of tools/cluster_console.py) on first use, from the texture
// pack (CTEX*.PAK, LZ4) decoded straight into staging when that is cached
// memory, else in pieces through a bounce buffer (the kernel's copies into
// the GPU's write-combined memory aren't safe: g_memcpy is). Without a pack,
// the loose ctex/XX/YYYYYYYY.CTX files. Out of memory, it is drawn without
// and tried again two seconds later.
void Renderer::load(int id, Tex& t) {
    if (t.image || t.failed || t.loading || t.key.empty() || frame_no_ < t.retry_at) return;
    if (ch360_ctex_pack >= 0) {
        load_packed(id, t);
        return;
    }
    char staged[12];
    ch360_staged_name(t.key.c_str(), t.key.size(), staged);
    std::string path = std::string("game:/GameData/ctex/") + staged + ".CTX";
    double t0 = now_ms();
    unsigned char head[28];
    int fd = open(path.c_str(), O_RDONLY);
    bool ok = fd >= 0 && read(fd, head, sizeof(head)) == (ssize_t)sizeof(head) && ch360_u32(head) == 0x31585443u;
    unsigned fmt = ok ? ch360_u32(head + 4) : 0, w = ok ? ch360_u32(head + 8) : 0, h = ok ? ch360_u32(head + 12) : 0,
             n = ok ? ch360_u32(head + 24) : 0;
    if (!ok || w == 0 || h == 0 || w > 8192 || h > 8192 || n == 0) {
        if (fd >= 0) close(fd);
        logf("texture %s (%s): missing or unreadable (run tools/convert_console.py, stage_console.py)", t.key.c_str(), path.c_str());
        t.failed = true;
        return;
    }
    VkFormat f = fmt == 1 ? VK_FORMAT_BC1_RGBA_UNORM_BLOCK : fmt == 2 ? VK_FORMAT_BC3_UNORM_BLOCK : TARGET_FORMAT;
    VkBuffer buf;
    VkDeviceSize off;
    unsigned char* dst = NULL;
    if (make_image(t, f, (int)w, (int)h)) {
        dst = staging(n, &buf, &off);
        if (!dst) destroy(t, true);
    }
    if (!dst) {
        close(fd);
        t.retry_at = frame_no_ + 120;
        logf("texture %s: no memory, drawn without it for now", t.key.c_str());
        return;
    }
    size_t got = 0;
    while (got < n) {
        size_t want = n - got < bounce_.size() ? n - got : bounce_.size();
        if (staging_cached_) want = n - got < (4u << 20) ? n - got : (4u << 20);
        ssize_t r = read(fd, staging_cached_ ? dst + got : &bounce_[0], want);
        if (r <= 0) break;
        if (!staging_cached_) g_memcpy(dst + got, &bounce_[0], (size_t)r);
        got += (size_t)r;
    }
    close(fd);
    if (got != n) {
        destroy(t, true);
        logf("texture %s: short file (%u of %u bytes)", path.c_str(), (unsigned)got, n);
        t.failed = true;
        return;
    }
    record_copy(t, buf, off);
    g_texture_bytes = texture_bytes_;
    if (loads_logged_ < 400) {
        ++loads_logged_;
        unsigned free_mb, total_mb;
        memory_mb(&free_mb, &total_mb);
        logf("texture %s: format %u, %ux%u, %u KB in %u ms; %u MB free, textures %u MB", t.key.c_str(), fmt, w, h, n >> 10,
             (unsigned)(now_ms() - t0), free_mb, (unsigned)(texture_bytes_ >> 20));
    }
}

void Renderer::start_loader() {
    load_jobs_ = 0;
    load_bytes_ = pre_bytes_ = 0;
    stored_bytes_ = 0;
    wait_ms_ = 0;
    hold_feed_ = false;
    memset(&waits_, 0, sizeof(waits_));
    held_frame_ = 0;
    if (ch360_ctex_pack < 0) return;
    g_arena = (unsigned char*)malloc(LOAD_ARENA);
    if (g_arena) g_arena_free.push_back(std::make_pair((size_t)0, LOAD_ARENA));
    fill_asked_ = fill_kept_ = fill_dropped_ = 0;
    fill_open_ = false;
    g_stored_base = STORED_ARENA ? (unsigned char*)malloc(STORED_ARENA) : NULL;
    if (g_stored_base) {
        g_stored_free.push_back(std::make_pair((size_t)0, STORED_ARENA));
        logf("textures: %u MB kept for pages' stored bytes", (unsigned)(STORED_ARENA >> 20));
    }
    g_load_reader = ch360_pack_reader_new();
    if (!g_load_reader) return;
    pthread_t th;
    g_loader = pthread_create(&th, NULL, texture_loader, NULL) == 0;
    logf("textures: %s", g_loader ? "read and decoded on hardware thread 2" : "loader thread failed: loaded on the game thread");
}

// The frame's start: textures the loader has read become images, copied into staging, within an upload
// budget a frame (the copies are the game thread's part).
void Renderer::take_loads(bool all) {
    if (!g_loader) return;
    pthread_mutex_lock(&g_load_lock);
    while (g_load_out_head != g_load_out_tail) {
        taken_.push_back(g_load_out[g_load_out_head % LOAD_RING]);
        ++g_load_out_head;
    }
    pthread_mutex_unlock(&g_load_lock);
    size_t budget = 6u << 20, used = 0;
    size_t k = 0;
    for (; k < taken_.size() && (all || used == 0 || used + taken_[k]->n <= budget); ++k) {
        LoadJob* j = taken_[k];
        used += j->n;
        Tex* t = tex(j->id);
        bool ours = t && t->key == j->key && t->loading && !t->image;
        if (j->fresh) {
            // The stored bytes read: the texture's to keep (if still its key and the read was good).
            if (t && t->key == j->key && j->state == 0 && !t->stored) {
                t->stored = j->stored;
                t->stored_n = j->e.stored;
                t->stored_used = frame_no_;
                if (t->scene_kept && !j->data) ++fill_kept_;
            } else {
                stored_give(j->stored, j->e.stored);
                stored_bytes_ -= j->e.stored;
            }
        }
        if (ours && !j->data) {   // stored bytes only
            t->loading = false;
            t->prefetching = false;
            if (j->state != 0) t->retry_at = frame_no_ + 600;
        } else if (ours) {
            t->loading = false;
            t->prefetching = false;
            if (j->state != 0) {
                logf("texture %s: %s", j->key.c_str(), j->state == 1 ? "missing from the pack or unreadable (run tools/stage_console.py)" : "bad read from the pack");
                t->failed = true;
            } else {
                VkFormat f = j->fmt == 1 ? VK_FORMAT_BC1_RGBA_UNORM_BLOCK : j->fmt == 2 ? VK_FORMAT_BC3_UNORM_BLOCK : TARGET_FORMAT;
                VkBuffer buf;
                VkDeviceSize off;
                unsigned char* dst = NULL;
                if (make_image(*t, f, (int)j->w, (int)j->h)) {
                    dst = staging(j->n, &buf, &off);
                    if (!dst) destroy(*t, true);
                }
                if (!dst) {
                    // A prefetch is simply dropped (read again when drawn); a drawn one waits two seconds.
                    if (!j->pre) {
                        t->retry_at = frame_no_ + 120;
                        logf("texture %s: no memory, drawn without it for now", j->key.c_str());
                    }
                } else {
                    if (staging_cached_) memcpy(dst, j->data, j->n);
                    else g_memcpy(dst, j->data, j->n);
                    record_copy(*t, buf, off);
                    if (j->stored && !j->fresh) ++waits_.from_memory;
                    else ++waits_.from_drive;
                    t->unused_pre = j->pre;
                    g_texture_bytes = texture_bytes_;
                    if (!j->pre) stats_.demand += 1;
                    if (t->reloads > 0) stats_.again += 1;
                    if (loads_logged_ < 1500 && ch360_verbose) {
                        ++loads_logged_;
                        unsigned free_mb, total_mb;
                        memory_mb(&free_mb, &total_mb);
                        logf("texture %s: format %u, %ux%u, %u KB (%u KB stored), %s %u ms on the loader, %s %u ms after the ask; "
                             "%u MB free, textures %u MB", j->key.c_str(), j->fmt, j->w, j->h, j->n >> 10, j->e.stored >> 10,
                             j->stored && !j->fresh ? "decoded from memory in" : "read and decode", (unsigned)j->read_ms,
                             j->pre ? "prefetched, ready" : "ready", (unsigned)(now_ms() - j->queued), free_mb,
                             (unsigned)(texture_bytes_ >> 20));
                    }
                }
            }
        } else if (t) {
            // Given another texture (or pixels) meanwhile: free to load what it is now.
            t->loading = false;
            t->prefetching = false;
        }
        if (j->pre) pre_bytes_ -= j->bytes;
        load_bytes_ -= j->bytes;
        --load_jobs_;
        arena_give(j->data, j->n ? j->n : 1);
        delete j;
    }
    taken_.erase(taken_.begin(), taken_.begin() + (long)k);
}

// The frame's textures still with the loader: the frame waits for them (the loader reads them meanwhile,
// several in a row); those the in-flight cap kept back are queued as room comes.
// The frame waits for what it draws, but never past WAIT_GIVE_UP_MS: a load that never ends drew the frame without
// its texture (logged), where it held the renderer's lock forever and froze the console.
const double WAIT_GIVE_UP_MS = 3000;
void Renderer::finish_waits() {
    last_waited_ = (unsigned)waiting_.size();
    if (waiting_.empty()) return;
    double t0 = now_ms();
    for (;;) {
        take_loads(true);
        bool pending = false, held = false;
        for (size_t k = 0; k < waiting_.size(); ++k) {
            Tex* t = tex(waiting_[k]);
            if (!t || t->image || t->failed || t->key.empty() || frame_no_ < t->retry_at) continue;
            // kept back by the cap, or a prefetch: ahead now
            if ((!t->loading || t->prefetching) && !queue_load(waiting_[k], *t, false) && !t->failed && frame_no_ >= t->retry_at) held = true;
            if (t->loading) pending = true;
        }
        if (!pending && !(held && load_jobs_ > 0)) break;
        if (now_ms() - t0 > WAIT_GIVE_UP_MS) {
            for (size_t k = 0; k < waiting_.size(); ++k) {
                Tex* t = tex(waiting_[k]);
                if (t && !t->image && !t->failed)
                    logf("texture wait: %s still not loaded after %u ms (loading %d, prefetching %d, queued ahead %d; %u jobs; "
                         "texture loader: %s), drawn without it", t->key.c_str(), (unsigned)(now_ms() - t0), (int)t->loading,
                         (int)t->prefetching, (int)t->pre_wanted, load_jobs_, g_texload_what);
            }
            break;
        }
        // a job done, or 1 ms (the give-up's clock)
        pthread_mutex_lock(&g_load_lock);
        bool done = g_load_out_head != g_load_out_tail;
        pthread_mutex_unlock(&g_load_lock);
        if (!done) {
            struct timespec ts = { 0, 1000000 };
            nanosleep(&ts, NULL);
        }
    }
    waiting_.clear();
    wait_ms_ += now_ms() - t0;
}

// A texture the glue expects to draw soon: the pages of an atlas the game loaded (Unity's atlas load
// uploads its textures: the logo's 48 MB of pages were read one by one as its first frames reached them),
// the other animations' pages of an atlas page just drawn. Read ahead, behind every texture a frame waits
// for, while memory has room: the backlog feeds the loader as it does.
void Renderer::prefetch(int id, bool wanted) {
    if (!g_loader || ch360_ctex_pack < 0) return;
    Tex* t = tex(id);
    // A guess (an atlas the game loaded, a drawn page's siblings) isn't read: in play its reads held up the loader
    // the frames' pages need (a level's own come through fill, under the hourglass).
    if (!wanted) return;
    if (t && hold_feed_) t->scene_kept = true;   // asked for by the scene's load: its stored bytes kept through it
    if (t && t->image && frame_no_ - t->last_used > 3) {
        // Loaded and wanted soon (a sprite near the camera, the clip it plays): kept as if drawn lately,
        // so making room takes older textures first (but not as drawn: the GPU isn't using it).
        t->last_used = frame_no_ - 3;
        t->unused_pre = false;
    }
    if (!t || t->image || t->loading || t->failed || t->key.empty() || t->pre_wanted || frame_no_ < t->retry_at) return;
    if (pre_backlog_.size() >= 1024) return;
    t->pre_wanted = true;
    pre_backlog_.push_back(id);
    if (!hold_feed_) feed_prefetch();
}

// A new scene's read-ahead (the load's last stage under the hourglass: RenderList.ReadAheadLoad) begins or
// ends. At its beginning the textures not drawn in the last frames (the scene going away: the loading screen
// draws its kept frames) count as not drawn for long. A restart or a return to the map found the textures the
// scene before had drawn lately, none old enough to make room from (the thirty-sixth run's Forest Follies
// restart: 64 pages still coming as its read-ahead ended, 92 as its hold did), and its first frames read them.
// The new scene's asks then mark what it wants as drawn lately (prefetch), and the backlog is fed only at the
// end, so room never comes from a texture the new scene wants too.
// A level's page (RenderList.FillAhead, under the hourglass): its stored bytes read into the block and kept through
// the level. Behind the read-ahead's pages (feed_prefetch), and not making room from the level's own.
void Renderer::fill(int id) {
    Tex* t = tex(id);
    if (!g_loader || !g_stored_base || ch360_ctex_pack < 0 || !t || t->key.empty() || t->failed) return;
    ++fill_asked_;
    fill_open_ = true;
    t->scene_kept = true;
    if (t->stored) {
        t->stored_used = frame_no_;
        ++fill_kept_;
        return;
    }
    if (t->loading || t->soft_wanted || soft_backlog_.size() >= 4096) return;
    t->soft_wanted = true;
    soft_backlog_.push_back(id);
    if (!hold_feed_) feed_prefetch();
}

void Renderer::scene_read_ahead(bool begin) {
    if (begin) {
        // the scene before's pages are no longer kept for it (least recently used go first as the new one fills)
        for (size_t i = 0; i < textures_.size(); ++i) textures_[i].scene_kept = false;
        fill_asked_ = fill_kept_ = fill_dropped_ = 0;
        const unsigned old = AHEAD_AGE + RELOAD_CAP * RELOAD_CREDIT + 4;
        for (size_t i = 0; i < textures_.size(); ++i) {
            Tex& t = textures_[i];
            if (!t.image || t.key.empty() || t.pinned || frame_no_ - t.last_used <= 3) continue;
            t.last_used = t.last_used > old ? t.last_used - old : 0;
        }
        hold_feed_ = true;
        return;
    }
    hold_feed_ = false;
    feed_prefetch();
}

void Renderer::feed_prefetch() {
    if (pre_backlog_.empty() && soft_backlog_.empty() && !fill_open_) return;
    unsigned free_mb, total_mb;
    memory_mb(&free_mb, &total_mb);   // once: the jobs queued below count through load_bytes_
    while (!pre_backlog_.empty()) {
        Tex* t = tex(pre_backlog_.front());
        if (!t || t->image || t->loading || t->failed || t->key.empty() || frame_no_ < t->retry_at) {
            if (t) t->pre_wanted = false;
            pre_backlog_.pop_front();
            continue;
        }
        if (pre_bytes_ > (8u << 20)) goto fill;
        // Room from textures not drawn for a while; none, and the read-ahead waits. (It needed 48 MB free
        // above the floor and the set under 3/4 of the budget: in the levels, 37-43 MB free, it never ran,
        // and every page coming into view was read while its frame waited: the twentieth run's hitches.)
        size_t want = texture_bytes_ + load_bytes_ + PAGE_MAX;
        if (want > TEXTURE_BUDGET && (release_aged(want - TEXTURE_BUDGET, AHEAD_AGE), texture_bytes_ + load_bytes_ + PAGE_MAX > TEXTURE_BUDGET)) goto fill;
        // As much free memory as a drawn texture's load leaves (make_image: the floor and its page), what is being
        // read counted in. 8 MB more before the thirty-eighth run: free sat at 29-38 MB in the levels, the read-ahead
        // waited at 36 while the frames' own loads took the same memory, and those were the hitches (FF 64 textures
        // on demand and 122 read again, Hilda 66 and 91).
        unsigned need_mb = MEMORY_FLOOR_MB + (unsigned)(load_bytes_ >> 20) + (unsigned)(PAGE_MAX >> 20);
        if (free_mb < need_mb) {
            if (!release_aged((size_t)(need_mb - free_mb) << 20, AHEAD_AGE)) {
                held_ahead(free_mb);
                goto fill;
            }
            memory_mb(&free_mb, &total_mb);
            if (free_mb < need_mb) {
                held_ahead(free_mb);
                goto fill;
            }
        }
        if (!queue_load(pre_backlog_.front(), *t, true) && !t->failed) goto fill;   // the rings or the in-flight cap: next frame
        t->pre_wanted = false;
        pre_backlog_.pop_front();
    }
fill:   // a level's fill reads into its own block: not held back with the read-ahead
    while (!soft_backlog_.empty()) {
        Tex* t = tex(soft_backlog_.front());
        if (!t || t->stored || t->loading || t->failed || t->key.empty() || frame_no_ < t->retry_at) {
            if (t) t->soft_wanted = false;
            soft_backlog_.pop_front();
            continue;
        }
        // Under the hourglass 4 MB at a time behind the read-ahead; in play (what the load's eight seconds left)
        // one at a time, only when no read-ahead is waiting.
        bool under = g_log_loading || hold_feed_;   // the last frame drawn the loading screen, or the scene asking
        if (under ? pre_bytes_ > (4u << 20) : (pre_bytes_ > 0 || !pre_backlog_.empty())) return;
        if (load_jobs_ >= LOAD_RING - 1 || load_bytes_ > (8u << 20) || g_pre_in_tail - g_pre_in_head >= LOAD_RING - 1) return;
        int id = soft_backlog_.front();
        t->soft_wanted = false;
        soft_backlog_.pop_front();
        if (!queue_load(id, *t, true, true) && !t->failed) ++fill_dropped_;   // no room left in the block
    }
    if (fill_open_ && load_jobs_ == 0) {
        fill_open_ = false;
        logf("HOST stored bytes: the level's fill asked for %u pages, %u kept, %u without room; %u MB of %u kept in all",
             fill_asked_, fill_kept_, fill_dropped_, (unsigned)(stored_bytes_ >> 20), (unsigned)(STORED_ARENA >> 20));
    }
}

// Room in the block for n stored bytes: the least recently used copies go (not the scene's own, none a job reads).
unsigned char* Renderer::stored_room(size_t n) {
    unsigned char* p = stored_take(n);
    if (p) return p;
    static unsigned failed_frame = ~0u;
    static size_t failed_n = 0;
    if (failed_frame == frame_no_ && n >= failed_n) return NULL;   // nothing to let go this frame: not searched again
    std::vector<std::pair<unsigned, size_t> > order;
    for (size_t i = 0; i < textures_.size(); ++i)
        if (textures_[i].stored && !textures_[i].loading && !textures_[i].scene_kept) order.push_back(std::make_pair(textures_[i].stored_used, i));
    std::sort(order.begin(), order.end());
    for (size_t k = 0; k < order.size(); ++k) {
        forget_stored(textures_[order[k].second]);
        if ((p = stored_take(n)) != NULL) return p;
    }
    failed_frame = frame_no_;
    failed_n = n;
    return NULL;
}

// Kept stored bytes go, least recently used first (none a job is reading), until want bytes are freed.
size_t Renderer::release_stored(size_t want) {
    std::vector<std::pair<unsigned, size_t> > order;
    for (size_t i = 0; i < textures_.size(); ++i)
        if (textures_[i].stored && !textures_[i].loading) order.push_back(std::make_pair(textures_[i].stored_used, i));
    std::sort(order.begin(), order.end());
    size_t freed = 0;
    for (size_t k = 0; k < order.size() && freed < want; ++k) {
        freed += textures_[order[k].second].stored_n;
        forget_stored(textures_[order[k].second]);
    }
    return freed;
}

void Renderer::forget_stored(Tex& t) {
    if (!t.stored) return;
    stored_give(t.stored, t.stored_n);
    stored_bytes_ -= t.stored_n;
    t.stored = NULL;
    t.stored_n = 0;
}

// A job for the loader thread: the drawn ring, or the prefetch ring (read when nothing drawn waits). A drawn
// texture whose prefetch is still queued moves to the drawn ring. False when the in-flight cap (8 MB of
// buffers) keeps it back, or it can't be read.
bool Renderer::queue_load(int id, Tex& t, bool pre, bool stored_only) {
    if (t.loading && t.prefetching && !pre) {
        pthread_mutex_lock(&g_load_lock);
        for (unsigned k = g_pre_in_head; k != g_pre_in_tail; ++k) {
            LoadJob* q = g_pre_in[k % LOAD_RING];
            if (q && q->id == id && q->key == t.key) {
                g_pre_in[k % LOAD_RING] = NULL;
                q->pre = false;
                pre_bytes_ -= q->bytes;
                g_load_in[g_load_in_tail++ % LOAD_RING] = q;
                pthread_cond_signal(&g_load_cond);
                break;
            }
        }
        pthread_mutex_unlock(&g_load_lock);
        t.prefetching = false;   // already being read when not found: it comes back as it is
        return true;
    }
    if (t.loading) return true;
    if (stored_only && t.stored) return true;
    if (load_jobs_ >= LOAD_RING - 1 || load_bytes_ > (8u << 20)) return false;
    // The prefetch ring also holds the empty slots of prefetches moved to the drawn ring until the loader
    // passes them (the loader moves its head; a stale read only holds a prefetch back a frame).
    if (pre && g_pre_in_tail - g_pre_in_head >= LOAD_RING - 1) return false;
    ch360_pack_entry pe;
    if (!ch360_pack_find(ch360_ctex_pack, t.key.c_str(), t.key.size(), &pe) || pe.size < 28) {
        logf("texture %s: missing from the pack (run tools/stage_console.py)", t.key.c_str());
        t.failed = true;
        return false;
    }
    unsigned n = pe.size - 28;
    // The stored bytes: the texture's kept copy, or read into a buffer kept after (room made from the
    // least recently used); without it, read from the pack as before.
    unsigned char* stored = t.stored;
    bool fresh = false;
    if (!stored && g_stored_base && pe.stored <= PAGE_MAX + (1u << 20) && (stored = stored_room(pe.stored)) != NULL) fresh = true;
    if (stored_only && !stored) return false;
    if (t.stored) t.stored_used = frame_no_;
    unsigned char* data = NULL;
    if (!stored_only && (data = arena_take(n ? n : 1)) == NULL) data = (unsigned char*)malloc(n ? n : 1);
    if (!stored_only && !data) {
        if (fresh) stored_give(stored, pe.stored);
        t.retry_at = frame_no_ + 120;
        return false;
    }
    LoadJob* j = new LoadJob();
    j->id = id;
    j->key = t.key;
    j->e = pe;
    j->data = data;
    j->stored = stored;
    j->fresh = fresh;
    j->n = stored_only ? 0 : n;
    j->bytes = (stored_only ? 0 : n) + (fresh ? pe.stored : 0);
    j->fmt = j->w = j->h = 0;
    j->state = 1;
    j->pre = pre;
    j->queued = now_ms();
    j->read_ms = 0;
    ++load_jobs_;
    load_bytes_ += j->bytes;
    if (pre) pre_bytes_ += j->bytes;
    if (fresh) stored_bytes_ += pe.stored;   // counted from now: its buffer is made
    t.loading = true;
    t.prefetching = pre;
    pthread_mutex_lock(&g_load_lock);
    if (pre) g_pre_in[g_pre_in_tail++ % LOAD_RING] = j;
    else g_load_in[g_load_in_tail++ % LOAD_RING] = j;
    pthread_cond_signal(&g_load_cond);
    pthread_mutex_unlock(&g_load_lock);
    return true;
}

void Renderer::load_packed(int id, Tex& t) {
    if (g_loader) {
        queue_load(id, t, false);
        return;
    }
    double t0 = now_ms();
    ch360_pack_entry e;
    unsigned char head[28];
    bool ok = ch360_pack_find(ch360_ctex_pack, t.key.c_str(), t.key.size(), &e) && e.size >= 28 &&
              ch360_pack_read(&e, 0, head, sizeof(head)) == 0 && ch360_u32(head) == 0x31585443u;
    unsigned fmt = ok ? ch360_u32(head + 4) : 0, w = ok ? ch360_u32(head + 8) : 0, h = ok ? ch360_u32(head + 12) : 0,
             n = ok ? ch360_u32(head + 24) : 0;
    if (!ok || w == 0 || h == 0 || w > 8192 || h > 8192 || n == 0 || n != e.size - 28) {
        logf("texture %s: missing from the pack or unreadable (run tools/stage_console.py)", t.key.c_str());
        t.failed = true;
        return;
    }
    VkFormat f = fmt == 1 ? VK_FORMAT_BC1_RGBA_UNORM_BLOCK : fmt == 2 ? VK_FORMAT_BC3_UNORM_BLOCK : TARGET_FORMAT;
    VkBuffer buf;
    VkDeviceSize off;
    unsigned char* dst = NULL;
    if (make_image(t, f, (int)w, (int)h)) {
        dst = staging(n, &buf, &off);
        if (!dst) destroy(t, true);
    }
    if (!dst) {
        t.retry_at = frame_no_ + 120;
        logf("texture %s: no memory, drawn without it for now", t.key.c_str());
        return;
    }
    double t1 = now_ms();
    bool read = true;
    if (staging_cached_) {
        read = ch360_pack_read(&e, 28, dst, n) == 0;
    } else {
        for (unsigned got = 0; read && got < n;) {
            unsigned want = n - got < bounce_.size() ? n - got : (unsigned)bounce_.size();
            read = ch360_pack_read(&e, 28 + got, &bounce_[0], want) == 0;
            if (read) g_memcpy(dst + got, &bounce_[0], want);
            got += want;
        }
    }
    if (!read) {
        destroy(t, true);
        logf("texture %s: bad read from the pack", t.key.c_str());
        t.failed = true;
        return;
    }
    double t2 = now_ms();
    record_copy(t, buf, off);
    g_texture_bytes = texture_bytes_;
    if (loads_logged_ < 400) {
        ++loads_logged_;
        unsigned free_mb, total_mb;
        memory_mb(&free_mb, &total_mb);
        logf("texture %s: format %u, %ux%u, %u KB (%u KB stored) in %u ms (read and decode %u); %u MB free, textures %u MB",
             t.key.c_str(), fmt, w, h, n >> 10, e.stored >> 10, (unsigned)(now_ms() - t0), (unsigned)(t2 - t1), free_mb,
             (unsigned)(texture_bytes_ >> 20));
    }
}

// Frees textures not drawn in the last three frames (the GPU is done with
// them: two frames are in flight), oldest first, until want bytes are freed.
// A texture read again after being let go counts RELOAD_CREDIT frames
// younger for each time (up to RELOAD_CAP): the twenty-fourth run's Forest Follies
// read 638 MB of textures in 99 s, 120 MB of them pages loaded two to eleven
// times (the player's, the explosions'), let go for pages behind the camera
// that never came back.
size_t Renderer::release_unused(size_t want) {
    std::vector<std::pair<unsigned, size_t> > order;
    // The least recently drawn or asked for first. Read-ahead pages not drawn yet went first before the thirty-ninth
    // run: they were the ones about to be drawn (within reach of the view, asked again every 30 game frames), and
    // each frame's own load let them go, to be read again moments later.
    for (size_t i = 0; i < textures_.size(); ++i)
        if (textures_[i].image && !textures_[i].key.empty() && !textures_[i].pinned && frame_no_ - textures_[i].last_used > 2) {
            const Tex& t = textures_[i];
            unsigned credit = (t.reloads < RELOAD_CAP ? t.reloads : RELOAD_CAP) * RELOAD_CREDIT;
            order.push_back(std::make_pair(t.last_used + credit, i));
        }
    std::sort(order.begin(), order.end());
    size_t freed = 0;
    for (size_t k = 0; k < order.size() && freed < want; ++k) {
        Tex& t = textures_[order[k].second];
        freed += t.bytes;
        destroy(t, true);
        t.evicted = true;
        ++evictions;
    }
    return freed;
}

// Frees textures not drawn (or, read ahead, not wanted) for more than age frames, oldest first, until want
// bytes are freed: room for read-ahead. The often reloaded wait their credit longer (release_unused).
size_t Renderer::release_aged(size_t want, unsigned age) {
    std::vector<std::pair<unsigned, size_t> > order;
    for (size_t i = 0; i < textures_.size(); ++i) {
        const Tex& t = textures_[i];
        unsigned credit = (t.reloads < RELOAD_CAP ? t.reloads : RELOAD_CAP) * RELOAD_CREDIT;
        if (t.image && !t.key.empty() && !t.pinned && frame_no_ - t.last_used > age + credit)
            order.push_back(std::make_pair(t.last_used + credit, i));
    }
    std::sort(order.begin(), order.end());
    size_t freed = 0;
    for (size_t k = 0; k < order.size() && freed < want; ++k) {
        Tex& t = textures_[order[k].second];
        freed += t.bytes;
        destroy(t, true);
        t.evicted = true;
        ++evictions;
    }
    return freed;
}

// Textures not drawn for a while go when the set passes the budget.
void Renderer::evict() {
    if (texture_bytes_ <= TEXTURE_BUDGET) return;
    for (unsigned age = 600; age >= 30 && texture_bytes_ > TEXTURE_BUDGET * 7 / 8; age /= 2)
        for (size_t i = 0; i < textures_.size() && texture_bytes_ > TEXTURE_BUDGET * 7 / 8; ++i) {
            Tex& t = textures_[i];
            if (t.image && !t.key.empty() && !t.pinned && frame_no_ - t.last_used > age) {
                destroy(t, false);
                t.evicted = true;
                ++evictions;
            }
        }
}

// Read-ahead asks made while another thread had the renderer (the replay thread drawing the frame in between,
// waiting in its acquire for the vblank): kept here and taken as the next frame starts, so the game's thread never
// waits on them (each sprite near the view asks during the game's update).
// A texture's file named (key non-empty) or a read-ahead ask (wanted 0/1), in the order the game made them.
struct Ask {
    int id, wanted;
    std::string key;
};
pthread_mutex_t g_ask_lock = PTHREAD_MUTEX_INITIALIZER;
std::vector<Ask> g_asks, g_asks_taken;

// The asks queued meanwhile, in their order (g_gpu_lock held): before anything newer, and before a frame draws.
void Renderer::take_asks() {
    pthread_mutex_lock(&g_ask_lock);
    g_asks_taken.swap(g_asks);
    pthread_mutex_unlock(&g_ask_lock);
    for (size_t i = 0; i < g_asks_taken.size(); ++i) {
        const Ask& a = g_asks_taken[i];
        if (!a.key.empty()) texture_file(a.id, a.key.c_str());
        else if (a.wanted == 2) fill(a.id);
        else prefetch(a.id, a.wanted != 0);
    }
    g_asks_taken.clear();
}

void Renderer::frame(const ch360_frame* f) {
    ++frame_no_;
    double t0 = now_ms();
    unsigned uploads_at = uploads;   // the loader's textures taken here count as this frame's loads
    take_asks();
    take_loads();
    feed_prefetch();

    // Pass 1: which items meet their view (the glue worked out their boxes;
    // the scissor would clip the rest away: the map draws 17 MB of textures
    // wholly off screen), load what those draw, then run the uploads.
    Stage marker("vk: loading textures");
    on_view_.assign(f->item_count, 0);
    unsigned uploads0 = uploads_at;
    double fill[2] = { 0, 0 };   // pixels the items cover (their boxes in their views): the screen's, render textures'
    for (unsigned v = 0; v < f->view_count; ++v) {
        const ch360_view& view = f->views[v];
        float tw = view.target ? (float)view.tw : (float)W, th = view.target ? (float)view.th : (float)H;
        float rx0 = view.x < 0 ? 0 : view.x, ry0 = view.y < 0 ? 0 : view.y;
        float rx1 = view.x + view.w > tw ? tw : view.x + view.w, ry1 = view.y + view.h > th ? th : view.y + view.h;
        unsigned end = view.first_item + view.items;
        for (unsigned i = view.first_item; i < end && i < f->item_count; ++i) {
            const ch360_item& it = f->items[i];
            bool on = it.count > 0 && (it.x0 > it.x1 || (it.x1 >= rx0 && it.x0 <= rx1 && it.y1 >= ry0 && it.y0 <= ry1));
            on_view_[i] = on ? 1 : 0;
            if (!on) continue;
            if (it.x0 > it.x1) fill[view.target ? 1 : 0] += (double)(rx1 - rx0) * (ry1 - ry0);
            else {
                float ax0 = it.x0 > rx0 ? it.x0 : rx0, ax1 = it.x1 < rx1 ? it.x1 : rx1;
                float ay0 = it.y0 > ry0 ? it.y0 : ry0, ay1 = it.y1 < ry1 ? it.y1 : ry1;
                if (ax1 > ax0 && ay1 > ay0) fill[view.target ? 1 : 0] += (double)(ax1 - ax0) * (ay1 - ay0);
            }
            load_for(it.texture);
            if (it.mode == 6 || it.mode == 17)   // the light ray's two more textures, the film grain's overlay
                for (int k = 0; k < 2; ++k) load_for(it.textures[k]);
        }
    }
    finish_waits();
    double t1 = now_ms();
    Frame& fr = frames_[frame_];
    g_stage = "vk: waiting for the frame's fence";
    vkWaitForFences(dev_, 1, &fr.fence, VK_TRUE, ~(uint64_t)0);
    double t2 = now_ms();
    gpu_wait_ms += t2 - t1;
    vkResetFences(dev_, 1, &fr.fence);
    if (fr.query_count > 1) {
        uint64_t ts[QSLOTS];
        if (vkGetQueryPoolResults(dev_, fr.queries, 0, fr.query_count, sizeof(ts), ts, sizeof(uint64_t), VK_QUERY_RESULT_64_BIT) == VK_SUCCESS) {
            double total = (double)(ts[fr.query_count - 1] - ts[0]) * ts_period_ms_;
            for (unsigned q = 1; q < fr.query_count; ++q) {
                double d = (double)(ts[q] - ts[q - 1]) * ts_period_ms_;
                stats_.gpu_kind[fr.query_kind[q]] += d;
                host_gpu_[fr.query_kind[q]] += d;
            }
            stats_.gpu_ms += total;
            stats_.gpu_frames += 1;
            host_gpu_[GPU_KINDS] += total;
            ++host_gpu_frames_;
        }
    }
    fr.query_count = 0;
    for (size_t i = 0; i < fr.dead.size(); ++i) {
        if (fr.dead[i].fb) vkDestroyFramebuffer(dev_, fr.dead[i].fb, NULL);
        if (fr.dead[i].set) vkFreeDescriptorSets(dev_, desc_pool_, 1, &fr.dead[i].set);
        if (fr.dead[i].view) vkDestroyImageView(dev_, fr.dead[i].view, NULL);
        if (fr.dead[i].image) vkDestroyImage(dev_, fr.dead[i].image, NULL);
        if (fr.dead[i].mem) vkFreeMemory(dev_, fr.dead[i].mem, NULL);
    }
    fr.dead.clear();
    free_targets();
    flush_uploads();
    double t3 = now_ms();

    uint32_t image = 0;
    g_stage = "vk: acquiring a swapchain image";
    VkResult r = vkAcquireNextImageKHR(dev_, swapchain_, ~(uint64_t)0, fr.acquired, VK_NULL_HANDLE, &image);
    if (r != VK_SUCCESS && r != VK_SUBOPTIMAL_KHR) {
        vk(r, "vkAcquireNextImageKHR");
        return;
    }
    double t4 = now_ms();
    g_stage = "vk: recording";
    VkCommandBuffer cmd = fr.cmd;
    vkResetCommandBuffer(cmd, 0);
    VkCommandBufferBeginInfo bi;
    memset(&bi, 0, sizeof(bi));
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    vkBeginCommandBuffer(cmd, &bi);
    // Every 4th frame timed on the GPU: a stamp as its work starts and as each run of passes of one
    // kind (render textures, effect blits, the screen) ends.
    bool timed = fr.queries != VK_NULL_HANDLE && frame_no_ % 4 == 0;
    int run_kind = -1;
    unsigned passes = 0;
    if (timed) {
        vkCmdResetQueryPool(cmd, fr.queries, 0, QSLOTS);
        vkCmdWriteTimestamp(cmd, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, fr.queries, fr.query_count++);
    }
    auto stamp = [&](bool last) {   // the run of run_kind passes ends (the last slot kept for the frame's end)
        if (!timed || run_kind < 0 || fr.query_count >= (last ? QSLOTS : QSLOTS - 1)) return;
        fr.query_kind[fr.query_count] = (unsigned char)run_kind;
        vkCmdWriteTimestamp(cmd, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, fr.queries, fr.query_count++);
    };
    auto kind_of = [&](const ch360_view& view) {
        if (view.target == 0) return (int)GPU_SCREEN;
        unsigned m = view.items && view.first_item < f->item_count ? f->items[view.first_item].mode : 0;
        return m >= 11 && m <= 18 ? (int)GPU_FX : (int)GPU_RT;
    };
    VkDeviceSize base = (VkDeviceSize)frame_ * VERTEX_RING;
    vkCmdBindVertexBuffers(cmd, 0, 1, &vbuf_, &base);
    ch360_vertex* ring = (ch360_vertex*)(vmap_ + base);
    size_t used = 0, cap = VERTEX_RING / sizeof(ch360_vertex);
    // More vertices than the ring holds: the largest items (a sky's or a cloud bank's layers, meshes of
    // thousands of triangles) are left out until the rest fits. The ring used to stop at the first item
    // that didn't fit, so everything drawn after Hilda Berg's night sky went (the twenty-second run).
    size_t drop_from = ~(size_t)0;
    unsigned drop_left = 0;
    {
        size_t need = 0;
        for (unsigned i = 0; i < f->item_count; ++i) need += f->items[i].count;
        if (need > cap) {
            size_t wanted = need;
            std::vector<unsigned> big;
            for (unsigned i = 0; i < f->item_count; ++i)
                if (f->items[i].count >= 3000) big.push_back(f->items[i].count);
            std::sort(big.begin(), big.end());
            while (need > cap && !big.empty()) {
                need -= big.back();
                drop_from = big.back();
                ++drop_left;
                big.pop_back();
            }
            if (!overflow_logged_)
                logf("vk: a frame wanted %u vertices of the ring's %u: its %u largest items left out", (unsigned)wanted, (unsigned)cap, drop_left);
            overflow_logged_ = true;
        }
    }

    // Pass 2: record the views' draws, consecutive items of one pipeline and
    // texture batched into one draw; each item's vertices are copied as the
    // glue wrote them. A render pass per run of views with one target: render
    // textures first (the engine lists them so), the screen last.
    VkPipeline bound = VK_NULL_HANDLE;
    VkPipelineLayout pushed = VK_NULL_HANDLE;   // the layout the viewport constants were last pushed with
    VkDescriptorSet bound_set = VK_NULL_HANDLE;
    size_t batch_start = 0, batch_count = 0;
    unsigned draws = 0;
    bool stencil_dirty = false;   // a mask has written the stencil since it was last cleared
    bool in_pass = false, screen_begun = false, skip_target = false;
    unsigned cur_target = 0;
    int pw = W, ph = H;
    float pc[4] = { 2.0f / W, -2.0f / H, -1.0f, 1.0f };
    auto flush = [&]() {
        if (batch_count) {
            vkCmdDraw(cmd, (uint32_t)batch_count, 1, (uint32_t)batch_start, 0);
            ++draws;
        }
        batch_start = used;
        batch_count = 0;
    };
    auto use_layout = [&](VkPipelineLayout layout) {
        if (layout != pushed) {
            vkCmdPushConstants(cmd, layout, VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof(pc), pc);
            pushed = layout;
        }
    };
    auto begin = [&](const ch360_view& view) {
        flush();
        if (in_pass) vkCmdEndRenderPass(cmd);
        in_pass = false;
        int kind = kind_of(view);
        if (kind != run_kind) {
            stamp(false);
            run_kind = kind;
        }
        ++passes;
        cur_target = view.target;
        skip_target = false;
        VkClearValue clears[2];
        memset(clears, 0, sizeof(clears));
        clears[1].depthStencil.depth = 1.0f;
        VkRenderPassBeginInfo rbi;
        memset(&rbi, 0, sizeof(rbi));
        rbi.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
        if (view.target == 0) {
            rbi.renderPass = screen_begun ? pass_load_ : pass_;
            rbi.framebuffer = fbs_[image];
            pw = W;
            ph = H;
            clears[0].color.float32[3] = 1.0f;
            // Unity's screen pixels (bottom-left origin) straight to clip space: y flips here.
            pc[0] = 2.0f / W; pc[1] = -2.0f / H; pc[2] = -1.0f; pc[3] = 1.0f;
            screen_begun = true;
        } else {
            Target* t = target(view.target, (int)view.tw, (int)view.th);
            if (!t) {
                skip_target = true;
                return;
            }
            rbi.renderPass = (view.clear & 4) ? rt_pass_load_ : rt_pass_;
            rbi.framebuffer = t->fb;
            pw = (int)view.tw;
            ph = (int)view.th;
            // Cleared to its camera's colour, alpha too (a blit replaces it all).
            if (view.clear & 1) {
                clears[0].color.float32[0] = view.r;
                clears[0].color.float32[1] = view.g;
                clears[0].color.float32[2] = view.b;
                clears[0].color.float32[3] = view.a;
            }
            // A render texture's first row is Unity's bottom one, as a texture's is: no flip.
            pc[0] = 2.0f / pw; pc[1] = 2.0f / ph; pc[2] = -1.0f; pc[3] = -1.0f;
        }
        rbi.renderArea.extent.width = (uint32_t)pw;
        rbi.renderArea.extent.height = (uint32_t)ph;
        rbi.clearValueCount = 2;
        rbi.pClearValues = clears;
        vkCmdBeginRenderPass(cmd, &rbi, VK_SUBPASS_CONTENTS_INLINE);
        VkViewport vp = { 0, 0, (float)pw, (float)ph, 0, 1 };
        vkCmdSetViewport(cmd, 0, 1, &vp);
        bound = VK_NULL_HANDLE;
        bound_set = VK_NULL_HANDLE;
        pushed = VK_NULL_HANDLE;
        use_layout(pipe_layout_);
        stencil_dirty = false;
        in_pass = true;
    };
    for (unsigned v = 0; v < f->view_count; ++v) {
        const ch360_view& view = f->views[v];
        bool first = !in_pass || view.target != cur_target;
        if (first && (skip_target ? view.target != cur_target : true)) begin(view);
        if (skip_target) continue;
        flush();
        VkRect2D sc;
        int x0 = (int)view.x, y0 = view.target ? (int)view.y : ph - (int)(view.y + view.h);
        sc.offset.x = x0 < 0 ? 0 : x0;
        sc.offset.y = y0 < 0 ? 0 : y0;
        sc.extent.width = (uint32_t)(view.w < 0 ? 0 : (view.w > pw ? pw : view.w));
        sc.extent.height = (uint32_t)(view.h < 0 ? 0 : (view.h > ph ? ph : view.h));
        vkCmdSetScissor(cmd, 0, 1, &sc);
        // A camera that clears depth clears the stencil too (Unity): only needed once a mask wrote it.
        if ((view.clear & 2) && stencil_dirty) {
            VkClearAttachment ca;
            memset(&ca, 0, sizeof(ca));
            ca.aspectMask = VK_IMAGE_ASPECT_DEPTH_BIT | VK_IMAGE_ASPECT_STENCIL_BIT;
            ca.clearValue.depthStencil.depth = 1.0f;
            VkClearRect cr;
            cr.rect = sc;
            cr.baseArrayLayer = 0;
            cr.layerCount = 1;
            vkCmdClearAttachments(cmd, 1, &ca, 1, &cr);
            stencil_dirty = false;
        }
        // A render texture's first view, over all of it, was cleared with its pass.
        bool whole = view.x <= 0 && view.y <= 0 && view.x + view.w >= pw && view.y + view.h >= ph;
        if ((view.clear & 1) && !(first && view.target && whole)) {
            VkClearAttachment ca;
            memset(&ca, 0, sizeof(ca));
            ca.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
            ca.clearValue.color.float32[0] = view.r;
            ca.clearValue.color.float32[1] = view.g;
            ca.clearValue.color.float32[2] = view.b;
            ca.clearValue.color.float32[3] = view.target ? view.a : 1.0f;
            VkClearRect cr;
            cr.rect = sc;
            cr.baseArrayLayer = 0;
            cr.layerCount = 1;
            vkCmdClearAttachments(cmd, 1, &ca, 1, &cr);
        }
        unsigned end = view.first_item + view.items;
        for (unsigned i = view.first_item; i < end && i < f->item_count; ++i) {
            const ch360_item& it = f->items[i];
            if (!on_view_[i]) continue;
            Tex* t = it.texture >= 0 ? sampled(it.texture) : &white_;
            if (!t || !t->image) continue;
            unsigned mode = it.mode;
            if (mode >= 20 && mode <= 38) {
                // The game's blend and mask shaders: the item drawn once per pass, each with its state.
                const FxPasses& ps = PASSES[mode - 20];
                if (ps.n == 0) continue;
                if (used + it.count > cap) break;
                flush();
                Tex* g = it.textures[0] >= 0 ? sampled(it.textures[0]) : NULL;   // a grab, the grab shaders'
                VkDescriptorSet sets[2] = { t->set, g && g->image ? g->set : white_.set };
                g_memcpy(&ring[used], &f->vertices[it.first], it.count * sizeof(ch360_vertex));
                for (int k = 0; k < ps.n; ++k) {
                    VkPipeline pp = pass_pipeline(ps.p[k]);
                    if (!pp) continue;
                    vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, pp);
                    if (k == 0) {
                        vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, fx_layout_, 0, 2, sets, 0, NULL);
                        pushed = VK_NULL_HANDLE;
                        use_layout(fx_layout_);
                        vkCmdPushConstants(cmd, fx_layout_, VK_SHADER_STAGE_FRAGMENT_BIT, 16, 48, it.params);
                    }
                    if (ps.p[k].lo != 0.0f || ps.p[k].hi != 0.0f) {   // the pass's band or cap into p2.xy
                        float band[2] = { ps.p[k].lo, ps.p[k].hi };
                        vkCmdPushConstants(cmd, fx_layout_, VK_SHADER_STAGE_FRAGMENT_BIT, 48, sizeof(band), band);
                    }
                    if (ps.p[k].stencil == STENCIL_WRITE) stencil_dirty = true;
                    vkCmdDraw(cmd, it.count, 1, (uint32_t)used, 0);
                    ++draws;
                }
                used += it.count;
                batch_start = used;
                bound = VK_NULL_HANDLE;
                bound_set = VK_NULL_HANDLE;
                continue;
            }
            int m = mode < 20 ? (int)PIPE_MODE[mode] : (int)MODE_SPRITE;
            VkPipeline pipe;
            if (mode == 7) {   // a sprite mask: the stencil alone
                if (it.stencil != 1 && it.stencil != 2) continue;
                pipe = mask_pipes_[it.stencil - 1];
                stencil_dirty = true;
            } else {
                pipe = pipeline(m, it.stencil == 3 ? STENCIL_INSIDE : it.stencil == 4 ? STENCIL_OUTSIDE : STENCIL_NONE);
                if (!pipe) continue;
            }
            if (drop_left > 0 && it.count >= drop_from) {
                --drop_left;
                ++ring_drops;
                continue;
            }
            if (used + it.count > cap) {
                if (!overflow_logged_) logf("vk: vertex ring full (%u vertices)", (unsigned)cap);
                overflow_logged_ = true;
                ++ring_drops;
                continue;
            }
            if (m == MODE_LIGHTRAY || m >= FIRST_FX) {
                // A draw of its own: more textures and its floats (the light ray three textures and
                // three floats; the effects two and twelve).
                flush();
                bool ray = m == MODE_LIGHTRAY;
                VkPipelineLayout layout = ray ? ray_layout_ : fx_layout_;
                Tex* e[2];
                for (int k = 0; k < 2; ++k) {
                    e[k] = it.textures[k] >= 0 ? sampled(it.textures[k]) : NULL;
                    if (!e[k] || !e[k]->image) e[k] = &white_;
                }
                VkDescriptorSet sets[3] = { t->set, e[0]->set, e[1]->set };
                vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, pipe);
                vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, layout, 0, ray ? 3 : 2, sets, 0, NULL);
                pushed = VK_NULL_HANDLE;
                use_layout(layout);
                vkCmdPushConstants(cmd, layout, VK_SHADER_STAGE_FRAGMENT_BIT, 16, ray ? 16 : 48, it.params);
                g_memcpy(&ring[used], &f->vertices[it.first], it.count * sizeof(ch360_vertex));
                vkCmdDraw(cmd, it.count, 1, (uint32_t)used, 0);
                ++draws;
                used += it.count;
                batch_start = used;
                bound = pipe;
                bound_set = VK_NULL_HANDLE;
                continue;
            }
            if (pipe != bound || t->set != bound_set) {
                flush();
                if (pipe != bound) vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, pipe);
                use_layout(pipe_layout_);
                if (t->set != bound_set)
                    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, pipe_layout_, 0, 1, &t->set, 0, NULL);
                bound = pipe;
                bound_set = t->set;
            }
            g_memcpy(&ring[used], &f->vertices[it.first], it.count * sizeof(ch360_vertex));
            used += it.count;
            batch_count += it.count;
        }
    }
    flush();
    if (in_pass) vkCmdEndRenderPass(cmd);
    if (!screen_begun) {   // nothing drew to the screen: present it cleared
        ch360_view none;
        memset(&none, 0, sizeof(none));
        begin(none);
        vkCmdEndRenderPass(cmd);
    }
    stamp(true);
    vkEndCommandBuffer(cmd);
    double t5 = now_ms();
    VkPipelineStageFlags stage = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    VkSubmitInfo si;
    memset(&si, 0, sizeof(si));
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.waitSemaphoreCount = 1;
    si.pWaitSemaphores = &fr.acquired;
    si.pWaitDstStageMask = &stage;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &cmd;
    si.signalSemaphoreCount = 1;
    si.pSignalSemaphores = &fr.rendered;
    g_stage = "vk: submitting";
    vk(vkQueueSubmit(queue_, 1, &si, fr.fence), "vkQueueSubmit");
    double t6 = now_ms();
    VkPresentInfoKHR pi;
    memset(&pi, 0, sizeof(pi));
    pi.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
    pi.waitSemaphoreCount = 1;
    pi.pWaitSemaphores = &fr.rendered;
    pi.swapchainCount = 1;
    pi.pSwapchains = &swapchain_;
    pi.pImageIndices = &image;
    g_stage = "vk: presenting";
    vk(vkQueuePresentKHR(queue_, &pi), "vkQueuePresentKHR");
    double t7 = now_ms();
    present_ms += t7 - t6;
    frame_ = (frame_ + 1) % FRAMES;
    g_stage = "vk: evicting";
    evict();
    g_texture_bytes = texture_bytes_;

    // Where the host's part of a frame goes, averaged over 300 frames (after
    // the player's FRAMES line): the glue writing the list, pass 1 with its
    // texture loads, the GPU fence, uploads, acquiring, recording (vertex
    // copies), vkQueueSubmit and present.
    host_ms_[0] += f->build_ms;
    host_ms_[1] += t1 - t0;
    host_ms_[2] += t2 - t1;
    host_ms_[3] += t3 - t2;
    host_ms_[4] += t4 - t3;
    host_ms_[5] += t5 - t4;
    host_ms_[6] += t6 - t5;
    host_ms_[7] += t7 - t6;
    host_draws_ += draws;
    host_vertices_ += used;
    host_items_ += f->item_count;
    host_passes_ += passes;
    // Forest Follies' frames had 43,000 vertices (the glue's 9 ms) where the PC's bot saw 7,000: the window's
    // densest frame names the textures drawing most of them.
    if (f->vertex_count > 15000 && f->vertex_count > dense_verts_) {
        dense_verts_ = f->vertex_count;
        dense_frame_ = frame_no_;
        std::map<int, unsigned> by;
        for (unsigned i = 0; i < f->item_count; ++i) by[f->items[i].texture] += f->items[i].count;
        std::vector<std::pair<unsigned, int> > top;
        for (std::map<int, unsigned>::iterator i = by.begin(); i != by.end(); ++i) top.push_back(std::make_pair(i->second, i->first));
        std::sort(top.rbegin(), top.rend());
        size_t at = 0;
        dense_top_[0] = 0;
        for (size_t k = 0; k < top.size() && k < 4 && at < sizeof(dense_top_) - 1; ++k) {
            Tex* t = top[k].second < CH360_RT_BASE ? tex(top[k].second) : NULL;
            std::string name = t && !t->key.empty() ? t->key.substr(0, t->key.find("_CAB-")) : "render texture";
            if (t && t->key.size() > 6 && t->key.find("_c", t->key.size() - 6) != std::string::npos) name += t->key.substr(t->key.rfind("_c"));
            at += (size_t)snprintf(dense_top_ + at, sizeof(dense_top_) - at, "%s%s %u", k ? ", " : "", name.c_str(), top[k].first);
        }
    }
    host_fill_[0] += fill[0] / (W * H);
    host_fill_[1] += fill[1] / (W * H);
    stats_.frames += 1;
    stats_.fence_ms += t2 - t1;
    stats_.present_ms += t7 - t6;
    stats_.host_ms += t6 - t0;
    stats_.loads_ms += t1 - t0;
    stats_.uploads_ms += t3 - t2;
    stats_.textures += uploads - uploads0;
    stats_.fill_screen += fill[0] / (W * H);
    stats_.fill_offscreen += fill[1] / (W * H);
    stats_.draws += draws;
    stats_.passes += passes;
    // A host part over 12 ms (texture loads, a GPU behind, uploads...) said frame by frame, six a window.
    if (t6 - t0 > 12.0) {
        stats_.slow += 1;
        ++host_slow_;
        if (host_slow_logged_ < 6 && ch360_verbose) {
            ++host_slow_logged_;
            logf("HOST SLOW frame %u: %.1f ms: boxes+loads %.1f (%u textures), fence %.1f, uploads %.1f, acquire %.1f, record %.1f, "
                 "submit %.1f, present %.1f; %u draws, %u passes, %u items", frame_no_, t6 - t0, t1 - t0, uploads - uploads0, t2 - t1,
                 t3 - t2, t4 - t3, t5 - t4, t6 - t5, t7 - t6, draws, passes, f->item_count);
        }
    }
    if (frame_no_ % 300 == 0) {
        logf("HOST frames %u-%u: glue %.1f, boxes+loads %.1f, fence %.1f, uploads %.1f, acquire %.1f, record %.1f, submit %.1f, "
             "present %.1f ms/frame; %u draws, %u vertices, %u items a frame; %u items left on halved atlas pages so far",
             frame_no_ - 299, frame_no_, host_ms_[0] / 300, host_ms_[1] / 300, host_ms_[2] / 300, host_ms_[3] / 300,
             host_ms_[4] / 300, host_ms_[5] / 300, host_ms_[6] / 300, host_ms_[7] / 300, host_draws_ / 300,
             (unsigned)(host_vertices_ / 300), host_items_ / 300, ch360_remap_misses());
        // The GPU's own time (time stamps on every 4th frame), the passes and the pixels the items cover.
        double gf = host_gpu_frames_ ? host_gpu_frames_ : 1;
        logf("GPU frames %u-%u: %.1f ms/frame (render textures %.1f, effects %.1f, screen %.1f; %u frames timed); %.1f passes a frame; "
             "fill %.2f screens + %.2f in render textures a frame; %u slow host parts (%u said)",
             frame_no_ - 299, frame_no_, host_gpu_[GPU_KINDS] / gf, host_gpu_[GPU_RT] / gf, host_gpu_[GPU_FX] / gf, host_gpu_[GPU_SCREEN] / gf,
             host_gpu_frames_, host_passes_ / 300.0, host_fill_[0] / 300, host_fill_[1] / 300, host_slow_, host_slow_logged_);
        if (dense_verts_) logf("HOST densest frame %u: %u vertices: %s", dense_frame_, dense_verts_, dense_top_);
        dense_verts_ = 0;
        memset(host_ms_, 0, sizeof(host_ms_));
        memset(host_gpu_, 0, sizeof(host_gpu_));
        memset(host_fill_, 0, sizeof(host_fill_));
        host_draws_ = host_items_ = 0;
        host_vertices_ = 0;
        host_gpu_frames_ = host_passes_ = host_slow_ = host_slow_logged_ = 0;
    }
}

Renderer* g_renderer = NULL;

// ------------------------------------------------------------ the loading screen kept moving

// Frames the game marks as its loading screen (the hourglass faded in on a black screen: ConsoleHost's
// LoadingScreen) are kept, and while the game thread is held up past REPLAY_AFTER_MS a thread on
// hardware thread 3 draws them again until the game draws, the kept frame nearest where the
// hourglass's animation would be by then (the game sends where it is in its cycle with each frame;
// the twentieth run's guess at the cycle from repeated frames looped a few frames too fast). The frames
// are kept one per slot of the cycle, from every cycle the screen has shown: the last 96 frames held
// 12-67 of a 2.2 s cycle, so a long hold-up drew its first part over and over (the twenty-first run): a scene's activation
// (its Awakes and Starts) and the full collections of a load held the hourglass still for 0.7-2.2 s
// (the eighteenth and nineteenth runs). The renderer is used by one thread at a time (g_gpu_lock).
pthread_mutex_t g_gpu_lock = PTHREAD_MUTEX_INITIALIZER;
struct GpuLock {
    GpuLock() { pthread_mutex_lock(&g_gpu_lock); }
    ~GpuLock() { pthread_mutex_unlock(&g_gpu_lock); }
};
const unsigned RECORD = 64;            // slots over the hourglass's cycle (~35 ms each for its 2.2 s)
// Two vblanks without a game frame: a load's game frames came 50-100 ms apart and the hourglass stepped
// between them (the twenty-fourth run).
const double REPLAY_AFTER_MS = 34;
struct Recorded {
    bool kept;
    unsigned phase;                    // where the hourglass was in its cycle, ms
    std::vector<ch360_view> views;
    std::vector<ch360_item> items;
    std::vector<ch360_vertex> verts;
};
Recorded g_rec[RECORD];
unsigned g_rec_count = 0, g_rec_head = 0;   // slots holding a frame, the newest frame's slot
// What the kept frames show (their items and modes; not textures: the hourglass's frames may be on pages of their own): the slots are
// kept from one loading screen to the next while it's the same screen, so a hold-up in a load's first frames
// (before the game recorded the cycle again) has the whole cycle to draw from.
unsigned g_rec_shape = 0;

unsigned loading_shape(const ch360_frame* f) {
    unsigned h = f->item_count * 2654435761u;
    for (unsigned k = 0; k < f->item_count; ++k) {
        const ch360_item& it = f->items[k];
        h = (h ^ (it.mode * 131u + 7u)) * 16777619u;
    }
    return h;
}

void forget_loading() {
    for (unsigned k = 0; k < RECORD; ++k) {
        g_rec[k].kept = false;
        std::vector<ch360_view>().swap(g_rec[k].views);
        std::vector<ch360_item>().swap(g_rec[k].items);
        std::vector<ch360_vertex>().swap(g_rec[k].verts);
    }
    g_rec_count = 0;
}
bool g_loading_screen = false;               // the last frame the game drew was the loading screen
unsigned g_rec_period = 0;                   // the hourglass's cycle, ms (0: not known, the newest frame is drawn)
double g_last_draw_ms = 0;

// A loading screen's frame kept: the hourglass and the fader over the load's empty scene draw 6-10 items and
// a few hundred vertices. Behind the fader the scene before it or the level being held can still draw (40-55
// items, 8,000-38,000 vertices): those frames were kept too, each slot keeping its vectors' room after (16 MB
// of the host's heap from Hilda Berg's hold on, the twenty-ninth run's textures 100 MB down to 82), and
// forgot the hourglass's frames (one slot left for a 4 s hold-up). They are shown, and count as the loading
// screen, but aren't kept.
const unsigned RECORD_ITEMS = 24, RECORD_VERTICES = 2048;

// After the game's draw (g_gpu_lock held); a frame recorded ahead (0x40000000: the hourglass's other
// frames, ConsoleHost.RecordHourglass) only fills its slot.
void record_loading(const ch360_frame* f) {
    bool ahead = (f->loading & 0x40000000u) != 0;
    if (!f->loading) {
        g_loading_screen = false;   // the slots stay for the next loading screen
        if (!ahead) g_log_loading = false;
        return;
    }
    if (f->item_count > RECORD_ITEMS || f->vertex_count > RECORD_VERTICES) {
        if (ahead) return;
        // the screen with a scene still drawing behind its fader: the kept frames show it as well
        g_loading_screen = true;
        g_log_loading = true;
        return;
    }
    unsigned period = (f->loading >> 16) & 0x3FFF, shape = loading_shape(f);
    if (period != g_rec_period || shape != g_rec_shape) forget_loading();
    g_rec_period = period;
    g_rec_shape = shape;
    unsigned phase = period ? (f->loading & 0xFFFF) % period : 0;
    unsigned slot = period ? (unsigned)((unsigned long long)phase * RECORD / period) : 0;
    Recorded& r = g_rec[slot];
    if (!r.kept) ++g_rec_count;
    r.kept = true;
    r.phase = phase;
    r.views.assign(f->views, f->views + f->view_count);
    r.items.assign(f->items, f->items + f->item_count);
    r.verts.assign(f->vertices, f->vertices + f->vertex_count);
    if (ahead) return;
    g_rec_head = slot;
    g_loading_screen = true;
    g_log_loading = true;
}

// The kept frame to draw when the hourglass is at phase ms of its cycle: the newest of those
// nearest before it (the newest frame if the cycle isn't known).
unsigned replay_pick(unsigned phase) {
    unsigned best = g_rec_head, gap = ~0u;
    if (!g_rec_period) return g_rec_head;
    for (unsigned i = 0; i < RECORD; ++i) {
        if (!g_rec[i].kept) continue;
        unsigned d = (phase + g_rec_period - g_rec[i].phase) % g_rec_period;
        if (d < gap) { gap = d; best = i; }
    }
    return best;
}

extern bool g_tw_ready;            // the frame in between (below)
static void draw_tween_locked();

void* replay_thread(void*) {
    xsdk_thread_move(3);
    unsigned from_phase = 0, drawn = 0, last = ~0u;
    double stall_from = 0;
    bool replaying = false;
    for (;;) {
        struct timespec ts = { 0, 4000000 };
        g_replay_stage = "sleeping";
        nanosleep(&ts, NULL);
        g_replay_stage = "waiting for the renderer";
        pthread_mutex_lock(&g_gpu_lock);
        g_replay_stage = "checking";
        if (g_tw_ready) {
            g_replay_stage = "drawing the frame in between";
            const char* game_stage = (const char*)g_stage;   // the renderer's markers name the game thread's
            draw_tween_locked();
            g_stage = game_stage;
            pthread_mutex_unlock(&g_gpu_lock);
            continue;
        }
        double t = now_ms();
        bool stalled = g_renderer && g_loading_screen && g_rec_count >= 8 && t - g_last_draw_ms >= REPLAY_AFTER_MS;
        if (stalled) {
            if (!replaying) {
                from_phase = g_rec[g_rec_head].phase;
                drawn = 0;
                last = ~0u;
                stall_from = g_last_draw_ms;
                replaying = true;
            }
            unsigned phase = g_rec_period ? (unsigned)((from_phase + (unsigned long long)(t - stall_from)) % g_rec_period) : 0;
            unsigned pick = replay_pick(phase);
            if (pick == last) {   // the same picture: wait
                pthread_mutex_unlock(&g_gpu_lock);
                continue;
            }
            last = pick;
            const Recorded& r = g_rec[pick];
            ch360_frame f;
            memset(&f, 0, sizeof(f));
            f.views = r.views.empty() ? NULL : &r.views[0];
            f.view_count = (unsigned)r.views.size();
            f.items = r.items.empty() ? NULL : &r.items[0];
            f.item_count = (unsigned)r.items.size();
            f.vertices = r.verts.empty() ? NULL : &r.verts[0];
            f.vertex_count = (unsigned)r.verts.size();
            f.loading = 0x80000000u;
            g_replay_stage = "drawing a kept frame";
            g_renderer->frame(&f);   // presents at the next vblank
            ++drawn;
        } else if (replaying) {
            replaying = false;
            logf("HOST loading screen: %u frames drawn again over a %u ms hold-up (a cycle of %u ms, %u of 64 slots kept)", drawn,
                 (unsigned)(g_last_draw_ms - stall_from), g_rec_period, g_rec_count);
        }
        pthread_mutex_unlock(&g_gpu_lock);
    }
    return NULL;
}

// ------------------------------------------------------------ audio

// The player mixes 48 kHz stereo each frame (ch360_plat_audio); a thread
// feeds xsdk's audio frames (256 samples, six channels) from a ring; a full
// ring drops the oldest samples. The player keeps the ring a little ahead
// (ch360_plat_audio_queued, ConsoleHost.AudioLead), and when a frame comes
// late and the ring runs low the thread mixes the last mix's voices on itself
// (ch360_mix_extend): silence only when nothing plays.
const unsigned AUDIO_RING = 96000; // frames (2 s)
float g_ring[AUDIO_RING * 2];
volatile unsigned g_ring_read = 0, g_ring_write = 0;
pthread_mutex_t g_audio_lock = PTHREAD_MUTEX_INITIALIZER;
bool g_audio_open = false;
unsigned g_audio_underruns = 0;

// Under this many frames left before an output frame, the thread mixes the voices on (ch360_mix_extend),
// 256 at a time. The loading screen's crackle through a load's hold-ups (up to ~4 s with nothing mixed)
// and every slow frame's gap (the twenty-ninth run: a crackle at each) are the voices going on now. It
// replaced a loop of the loading screen's last 0.75 s, which played what was heard last as the screen came
// up (a sound repeating and skipping before Hilda Berg's load) and no other gap.
const unsigned AUDIO_LOW = 512;

void* audio_thread(void*) {
    xsdk_thread_move(4);
    for (;;) {
        float* out = NULL;
        if (xsdk_audio_next_frame(0, &out) != 0 || !out) {
            struct timespec ts = { 0, 2000000 };
            nanosleep(&ts, NULL);
            continue;
        }
        memset(out, 0, XSDK_AUDIO_FRAME_FLOATS * sizeof(float));
        float* left = out + XSDK_AUDIO_FRONT_LEFT * XSDK_AUDIO_FRAME_SAMPLES;
        float* right = out + XSDK_AUDIO_FRONT_RIGHT * XSDK_AUDIO_FRAME_SAMPLES;
        pthread_mutex_lock(&g_audio_lock);
        unsigned avail = g_ring_write - g_ring_read;
        pthread_mutex_unlock(&g_audio_lock);
        // the game's frame is late: its voices go on (not under g_audio_lock: the mixer takes it to write)
        for (int k = 0; k < 8 && avail < AUDIO_LOW; ++k) {
            int n = ch360_mix_extend(XSDK_AUDIO_FRAME_SAMPLES);
            if (n <= 0) break;
            avail += (unsigned)n;
        }
        pthread_mutex_lock(&g_audio_lock);
        if (g_ring_write - g_ring_read < XSDK_AUDIO_FRAME_SAMPLES) ++g_audio_underruns;
        for (int i = 0; i < XSDK_AUDIO_FRAME_SAMPLES && g_ring_read != g_ring_write; ++i) {
            unsigned at = (g_ring_read % AUDIO_RING) * 2;
            float l = g_ring[at], r = g_ring[at + 1];
            ++g_ring_read;
            left[i] = l < -1.0f ? -1.0f : l > 1.0f ? 1.0f : l;
            right[i] = r < -1.0f ? -1.0f : r > 1.0f ? 1.0f : r;
        }
        pthread_mutex_unlock(&g_audio_lock);
        xsdk_audio_queue_frame();
    }
    return NULL;
}

// ------------------------------------------------------------ frame loop state

unsigned g_frames = 0;
double g_frame_start = 0, g_logic_ms = 0, g_worst_ms = 0;
unsigned g_slow = 0, g_held_quit = 0;
bool g_back_was = false;

}  // namespace

// ------------------------------------------------------------ the platform layer

extern "C" int ch360_probe64(void (*log)(const char* line), void (*sync)(void), const char* where, int detail);
static void probe_line(const char* line) { logf("%s", line); }
static void probe_sync() { flush_log(true); }

// The CPU probe on a thread of its own: the hardware thread to move to (or -1),
// created as the runtime creates the game's thread (pthread, its stack size).
struct ProbeThread { int hw; const char* where; };
static void* probe_thread(void* arg) {
    ProbeThread* p = (ProbeThread*)arg;
    if (p->hw >= 0) xsdk_thread_move((uint32_t)p->hw);
    ch360_probe64(probe_line, probe_sync, p->where, 0);
    return NULL;
}
static void probe_on_thread(int hw, size_t stack, const char* where) {
    static ProbeThread arg;
    arg.hw = hw;
    arg.where = where;
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    if (stack) pthread_attr_setstacksize(&attr, stack);
    pthread_t th;
    if (pthread_create(&th, &attr, probe_thread, &arg) == 0) pthread_join(th, NULL);
    pthread_attr_destroy(&attr);
}

// The host's tallies since the last call, for the player's per-scene summaries (u32s: counts, ms x 100, fill x 100).
extern "C" void ch360_plat_host_stats(unsigned* out, int n) {
    if (!g_renderer || n <= 0) return;
    GpuLock lock;
    HostStats& s = g_renderer->stats_;
    static unsigned underruns_seen = 0;
    unsigned f, tot;
    memory_mb(&f, &tot);
    double v[CH360_HOST_STATS] = {
        s.frames, s.gpu_frames, s.gpu_ms * 100, s.gpu_kind[GPU_RT] * 100, s.gpu_kind[GPU_FX] * 100, s.gpu_kind[GPU_SCREEN] * 100,
        s.fence_ms * 100, s.present_ms * 100, s.host_ms * 100, s.loads_ms * 100, s.uploads_ms * 100, s.textures,
        s.fill_screen * 100, s.fill_offscreen * 100, s.draws, s.passes, (double)(g_audio_underruns - underruns_seen),
        (double)(g_renderer->texture_bytes() >> 20), (double)f, s.slow, s.demand, s.again };
    underruns_seen = g_audio_underruns;
    for (int i = 0; i < n && i < CH360_HOST_STATS; ++i) out[i] = v[i] < 0 ? 0 : v[i] > 4294967295.0 ? 0xFFFFFFFFu : (unsigned)v[i];
    memset(&s, 0, sizeof(s));
    Renderer::WaitCauses& w = g_renderer->waits_;
    logf("HOST texture waits: %u never asked ahead, %u asked and held back, %u asked and still being read, %u let go before; "
         "the read-ahead held back by memory in %u frames (%u MB free at the least); pages decoded from their kept stored bytes %u, "
         "from the drive %u", w.never, w.queued, w.reading, w.let_go, w.held_frames, w.held_low_mb, w.from_memory, w.from_drive);
    memset(&w, 0, sizeof(w));
}

namespace {
extern unsigned g_tweens_drawn;   // the frames drawn in between (below)

// The every-600-frames report, on the log's thread: newlib's heap walk (mallinfo) and the renderer's figures (read
// without its lock: a report, not a measurement) cost the game's thread a vblank under the locked 30.
void host_report() {
    if (!g_renderer) {
        g_report_due = false;
        return;
    }
    unsigned f, tot;
    memory_mb(&f, &tot);
    struct mallinfo mi = mallinfo();   // newlib's heap: grown to (arena), in use
    // Times in tenths of a millisecond (integers only in the log).
    logf("frame %u: %u ms/frame x10 (worst %u, %u over 17.5); gpu wait %u, present %u; memory %u MB free of %u, player %u MB, "
         "textures %u MB (%u uploads, %u evicted), stored %u MB; host heap %u MB (%u in use); items left out for the vertex ring %u; audio underruns %u, "
         "%u ms carried on; %u frames drawn in between",
         g_report_frames, (unsigned)(g_report_logic_ms / 60), (unsigned)(g_report_worst_ms * 10), g_report_slow,
         (unsigned)(g_renderer->gpu_wait_ms / 60), (unsigned)(g_renderer->present_ms / 60), f, tot, ch360_memory_kb() / 1024,
         (unsigned)(g_renderer->texture_bytes() >> 20), g_renderer->uploads, g_renderer->evictions,
         (unsigned)(g_renderer->stored_bytes() >> 20), (unsigned)(mi.arena >> 20), (unsigned)(mi.uordblks >> 20), g_renderer->ring_drops,
         g_audio_underruns, (unsigned)(ch360_mix_carried() / 48), g_tweens_drawn);
    unsigned am[7];
    ch360_audio_memory(am);
    logf("audio memory: %u clips decoded %u KB (peak %u), compressed %u KB open + %u KB kept, stream buffers %u KB, %u decoders",
         am[0], am[1], am[2], am[3], am[4], am[5], am[6]);
    g_renderer->gpu_wait_ms = g_renderer->present_ms = 0;
    g_report_due = false;
}
}  // namespace

extern "C" int ch360_plat_frame_begin(void) {
    g_stage = "host: frame begin";
    double t = now_ms();
    if (g_frames > 0) {
        double ms = t - g_frame_start;
        g_logic_ms += ms;
        if (ms > g_worst_ms) g_worst_ms = ms;
        if (ms > 17.5) ++g_slow;
    }
    g_frame_start = t;
    if (g_frames > 0 && g_frames % 600 == 0 && !g_report_due) {
        // the game's figures now; the rest, and the writing, on the log's thread (host_report)
        g_report_frames = g_frames;
        g_report_logic_ms = g_logic_ms;
        g_report_worst_ms = g_worst_ms;
        g_report_slow = g_slow;
        g_report_due = true;
        g_logic_ms = g_worst_ms = 0;
        g_slow = 0;
    }
    ++g_frames;
    g_frames_seen = g_frames;
    if (g_frames <= 3 || g_frames == 10 || g_frames == 60 || g_frames == 300) logf("frame %u", g_frames);
    if (g_frames == 1) ch360_probe64(probe_line, probe_sync, "game thread, frame 1", 0);
    if (g_frames == 600) ch360_probe64(probe_line, probe_sync, "game thread, frame 600", 0);
    // Back + Start held for a second quits; Back alone writes the log now.
    unsigned held = 0;
    for (uint32_t user = 0; user < 4; ++user) {
        INPUT_STATE xs;
        memset(&xs, 0, sizeof(xs));
        if (XamInputGetState(user, 0, &xs) == 0) held |= xs.gamepad.buttons;
    }
    bool back = (held & INPUT_GAMEPAD_BACK) != 0;
    if (back && !g_back_was) flush_log();
    g_back_was = back;
    g_held_quit = back && (held & INPUT_GAMEPAD_START) ? g_held_quit + 1 : 0;
    if (g_held_quit > 60) {
        logf("Back + Start: quit");
        flush_log();
        return 0;
    }
    g_stage = "game";
    return 1;
}

extern "C" int ch360_plat_pad(int index, ch360_pad* out) {
    INPUT_STATE xs;
    memset(&xs, 0, sizeof(xs));
    if (index < 0 || index > 3 || XamInputGetState((uint32_t)index, 0, &xs) != 0) return 0;
    out->buttons = xs.gamepad.buttons;
    out->lx = xs.gamepad.thumb_lx;
    out->ly = xs.gamepad.thumb_ly;
    out->rx = xs.gamepad.thumb_rx;
    out->ry = xs.gamepad.thumb_ry;
    out->lt = xs.gamepad.left_trigger;
    out->rt = xs.gamepad.right_trigger;
    return 1;
}

// ------------------------------------------------------------ the frame drawn in between (the locked 30)

// ConsoleHost.LockedThirty: the run-and-gun levels' game frames come a fixed 1/30 s apart, and after each the host
// draws the same frame again with every matched sprite moved on by half its last move (ch360_imports.c), presented
// at the vblank between the two (the replay thread draws it as soon as it is handed over; a game frame finds one
// still waiting and draws it first, so the order holds). game:\full60.txt turns it off.
namespace {
bool g_tween_off = false;
std::vector<ch360_view> g_tw_views;
std::vector<ch360_item> g_tw_items;
std::vector<ch360_vertex> g_tw_verts;
bool g_tw_ready = false;   // under g_gpu_lock
unsigned g_tweens_drawn = 0;

}  // namespace

extern "C" int ch360_plat_tween(int on) {
    return on && !g_tween_off && g_renderer ? 1 : 0;
}

namespace {

// The frame in between, if one waits (g_gpu_lock held).
static void draw_tween_locked() {
    if (!g_tw_ready) return;
    g_tw_ready = false;
    ch360_frame f;
    memset(&f, 0, sizeof(f));
    f.views = g_tw_views.empty() ? NULL : &g_tw_views[0];
    f.view_count = (unsigned)g_tw_views.size();
    f.items = g_tw_items.empty() ? NULL : &g_tw_items[0];
    f.item_count = (unsigned)g_tw_items.size();
    f.vertices = g_tw_verts.empty() ? NULL : &g_tw_verts[0];
    f.vertex_count = (unsigned)g_tw_verts.size();
    g_renderer->frame(&f);   // presents at the next vblank
    ++g_tweens_drawn;
}
}  // namespace

extern "C" void ch360_plat_draw_tween(const ch360_frame* n, const float* shifts) {
    GpuLock lock;
    g_tw_views.assign(n->views, n->views + n->view_count);
    g_tw_items.assign(n->items, n->items + n->item_count);
    g_tw_verts.assign(n->vertices, n->vertices + n->vertex_count);
    for (unsigned i = 0; i < n->item_count; ++i) {
        float dx = shifts[2 * i], dy = shifts[2 * i + 1];
        if (dx == 0.0f && dy == 0.0f) continue;
        ch360_item& it = g_tw_items[i];
        for (unsigned k = it.first; k < it.first + it.count && k < g_tw_verts.size(); ++k) {
            g_tw_verts[k].x += dx;
            g_tw_verts[k].y += dy;
        }
        if (it.x0 <= it.x1) {   // its screen box, when worked out
            it.x0 += dx;
            it.x1 += dx;
            it.y0 += dy;
            it.y1 += dy;
        }
    }
    g_tw_ready = true;
}

extern "C" void ch360_plat_draw(const ch360_frame* f) {
    if (f->loading & 0x40000000u) {   // the hourglass's frame recorded ahead: kept, not drawn
        GpuLock lock;
        record_loading(f);
        return;
    }
    {
        GpuLock lock;
        draw_tween_locked();   // the last frame's in-between one, if the replay thread hasn't drawn it yet
        g_renderer->frame(f);
        record_loading(f);
        g_last_draw_ms = now_ms();
    }
    g_stage = "game";
}

static void ask(int id, int wanted, const char* key);   // below

extern "C" void ch360_plat_texture_file(int id, const char* key) {
    if (!key || !*key) return;
    ask(id, 0, key);
}
extern "C" int ch360_plat_textures_waited(void) {
    GpuLock lock;
    return g_renderer ? g_renderer->waited() : 0;
}
extern "C" int ch360_plat_textures_pending(void) {
    GpuLock lock;
    return g_renderer ? g_renderer->pending() : 0;
}
// Both made in the game's update: when the replay thread has the renderer (the frame in between, waiting in its
// acquire for the vblank) they are queued (Renderer::take_asks), so the game's thread never waits on it. The every-600-
// frames report took the lock in the thirty-eighth run and Treetop missed its locked 30 by a vblank each time.
static void ask(int id, int wanted, const char* key) {
    if (pthread_mutex_trylock(&g_gpu_lock) == 0) {
        g_renderer->take_asks();   // what was queued before it first
        if (key) g_renderer->texture_file(id, key);
        else if (wanted == 2) g_renderer->fill(id);
        else g_renderer->prefetch(id, wanted != 0);
        pthread_mutex_unlock(&g_gpu_lock);
        return;
    }
    Ask a;
    a.id = id;
    a.wanted = wanted;
    if (key) a.key = key;
    pthread_mutex_lock(&g_ask_lock);
    g_asks.push_back(a);
    pthread_mutex_unlock(&g_ask_lock);
}

extern "C" void ch360_plat_texture_prefetch(int id, int wanted) {
    ask(id, wanted, NULL);
}

extern "C" void ch360_plat_texture_guess(int id) {
    ask(id, 2, NULL);
}
extern "C" void ch360_plat_textures_scene(int begin) {
    GpuLock lock;
    if (g_renderer) g_renderer->scene_read_ahead(begin != 0);
}

extern "C" void ch360_plat_texture_pixels(int id, int width, int height, const unsigned char* bgra) {
    Stage stage("vk: run-time texture");
    GpuLock lock;
    g_renderer->texture_pixels(id, width, height, bgra);
}

extern "C" int ch360_plat_audio_queued(void) {
    if (!g_audio_open) return -1;
    pthread_mutex_lock(&g_audio_lock);
    unsigned n = g_ring_write - g_ring_read;
    pthread_mutex_unlock(&g_audio_lock);
    return (int)n;
}

extern "C" void ch360_plat_audio_native(const float* lr, int frames) {
    if (!g_audio_open) return;
    Stage stage("host: audio");
    pthread_mutex_lock(&g_audio_lock);
    for (int i = 0; i < frames; ++i, lr += 2) {
        if (g_ring_write - g_ring_read >= AUDIO_RING) ++g_ring_read;   // full: drop the oldest
        unsigned at = (g_ring_write % AUDIO_RING) * 2;
        g_ring[at] = lr[0];
        g_ring[at + 1] = lr[1];
        ++g_ring_write;
    }
    pthread_mutex_unlock(&g_audio_lock);
}

extern "C" void ch360_plat_audio(const unsigned char* s, int frames) {
    if (!g_audio_open) return;
    Stage stage("host: audio");
    pthread_mutex_lock(&g_audio_lock);
    // Little-endian floats into the ring as byte-swapped words: no trip
    // through the FPU (each one a load-hit-store stall on this CPU).
    for (int i = 0; i < frames; ++i, s += 8) {
        if (g_ring_write - g_ring_read >= AUDIO_RING) ++g_ring_read;   // full: drop the oldest
        unsigned at = (g_ring_write % AUDIO_RING) * 2;
        uint32_t l = (uint32_t)s[0] | ((uint32_t)s[1] << 8) | ((uint32_t)s[2] << 16) | ((uint32_t)s[3] << 24);
        uint32_t r = (uint32_t)s[4] | ((uint32_t)s[5] << 8) | ((uint32_t)s[6] << 16) | ((uint32_t)s[7] << 24);
        memcpy(&g_ring[at], &l, 4);
        memcpy(&g_ring[at + 1], &r, 4);
        ++g_ring_write;
    }
    pthread_mutex_unlock(&g_audio_lock);
}

extern "C" void ch360_plat_log(const char* text, size_t len) {
    log_text(text, len);
#ifdef CH360_TEST64
    flush_log(true);   // on the drive before the next check, which may stop the console
#endif
}

// The wasm runtime's waits (poll_oneoff, wasi clocks) and sleeps, marked for
// the heartbeat; a sleep over two seconds is logged (in hex).
extern "C" void __real_srt_clock_wait_until(unsigned long long deadline_ns);
extern "C" void __wrap_srt_clock_wait_until(unsigned long long deadline_ns) {
    Stage stage("runtime: waiting for a deadline");
    __real_srt_clock_wait_until(deadline_ns);
}
extern "C" void __real_srt_plat_sleep_ns(unsigned long long ns);
extern "C" void __wrap_srt_plat_sleep_ns(unsigned long long ns) {
    if (ns > 2000000000ull) logf("runtime: sleep of 0x%08x%08x ns", (unsigned)(ns >> 32), (unsigned)ns);
    Stage stage("runtime: sleeping");
    __real_srt_plat_sleep_ns(ns);
}

// The runtime's file calls, counted and timed for the heartbeat.
extern "C" int __real_srt_plat_open(const char* path, int flags, void** out);
extern "C" int __wrap_srt_plat_open(const char* path, int flags, void** out) {
    Stage stage("runtime: opening a file");
    ++g_io_opens;
    double t0 = now_ms();
    int r = __real_srt_plat_open(path, flags, out);
    g_io_ms += now_ms() - t0;
    return r;
}
extern "C" int __real_srt_plat_read(void* f, void* buf, size_t len, size_t* done);
extern "C" int __wrap_srt_plat_read(void* f, void* buf, size_t len, size_t* done) {
    Stage stage("runtime: reading a file");
    ++g_io_reads;
    double t0 = now_ms();
    int r = __real_srt_plat_read(f, buf, len, done);
    g_io_ms += now_ms() - t0;
    g_io_bytes += *done;
    return r;
}
extern "C" int __real_srt_plat_seek(void* f, long long offset, int whence, unsigned long long* pos);
extern "C" int __wrap_srt_plat_seek(void* f, long long offset, int whence, unsigned long long* pos) {
    ++g_io_seeks;
    return __real_srt_plat_seek(f, offset, whence, pos);
}

// The player's linear memory grows by committing pages (physical memory:
// the console has no paging). Textures not drawn lately make room first,
// so the game gets memory the renderer could give back: in the sixth run
// the player reached 183 MB at the map (150 MB on PC) with textures at
// their 96 MB budget and 39 MB free.
extern "C" int __real_srt_plat_commit(void* base, unsigned long long from, unsigned long long to);
// The renderer's lock within ms (false: not got): the commit hook's making room must never wait on a holder that
// waits for the game's thread.
static bool gpu_lock_within(unsigned ms) {
    double until = now_ms() + ms;
    while (pthread_mutex_trylock(&g_gpu_lock) != 0) {
        if (now_ms() > until) return false;
        struct timespec ts = { 0, 1000000 };
        nanosleep(&ts, NULL);
    }
    return true;
}
extern "C" int __wrap_srt_plat_commit(void* base, unsigned long long from, unsigned long long to) {
    if (g_renderer && to > from) {
        unsigned free_mb, total_mb;
        memory_mb(&free_mb, &total_mb);
        // Each growth, to find why the console's player outgrows the PC's.
        logf("memory: player %u -> %u MB at frame %u (%s); %u MB free, textures %u MB", (unsigned)(from >> 20),
             (unsigned)(to >> 20), g_frames_seen, (const char*)g_stage, free_mb, (unsigned)(g_texture_bytes >> 20));
        unsigned need_mb = (unsigned)((to - from) >> 20) + 1 + MEMORY_FLOOR_MB;
        if (free_mb < need_mb) {
            size_t freed = 0;
            if (gpu_lock_within(1000)) {
                freed = g_renderer->make_room((size_t)(need_mb - free_mb) << 20);
                pthread_mutex_unlock(&g_gpu_lock);
            } else {
                logf("memory: the renderer's lock stayed held for a second (replay thread: %s), the player grows without "
                     "making room", (const char*)g_replay_stage);
            }
            logf("memory: the player grows to %u MB with %u MB free; %u KB of textures let go", (unsigned)(to >> 20), free_mb,
                 (unsigned)(freed >> 10));
        }
    }
    int r = __real_srt_plat_commit(base, from, to);
    if (r != 0 && g_renderer) {
        size_t freed = 0;
        if (gpu_lock_within(1000)) {
            freed = g_renderer->make_room((size_t)(to - from) * 2);
            pthread_mutex_unlock(&g_gpu_lock);
        }
        if (freed > 0) r = __real_srt_plat_commit(base, from, to);
    }
    if (r != 0) logf("memory: the player couldn't grow to %u MB", (unsigned)(to >> 20));
    return r;
}


int main() {
    FILE* fresh = fopen(CH360_LOG_PATH, "wb");
    if (fresh) fclose(fresh);
    logf("Cuphead360 (xSDK) %s", CH360_GIT_COMMIT);
    {
#ifdef CH360_TEST64
        FILE* q = NULL;   // the test build logs whatever nolog.txt says
#else
        FILE* q = fopen("game:/nolog.txt", "rb");
#endif
        if (q) {
            fclose(q);
            ch360_quiet = 1;
            logf("no log (nolog.txt): nothing more is written this session; no timing of phases or scripts");
            flush_log(true);
            ch360_nolog = 1;
        } else if ((q = fopen("game:/quiet.txt", "rb")) != NULL) {
            fclose(q);
            ch360_quiet = 1;
            logf("quiet run (quiet.txt): no timing of phases or scripts, no texture or slow-part lines");
        } else if ((q = fopen("game:/profile.txt", "rb")) != NULL) {
            fclose(q);
            ch360_verbose = 1;
            logf("profile run (profile.txt): scripts timed, texture and slow-part lines");
        }
        if ((q = fopen("game:/full60.txt", "rb")) != NULL) {
            fclose(q);
            g_tween_off = true;
            logf("full60.txt: the run-and-gun levels run as fast as they can, without the locked 30 and its frames in between");
        }
    }
    unsigned f, t;
    memory_mb(&f, &t);
    logf("memory at start: %u MB free of %u MB", f, t);
    // The second to fourth runs: compares right here, wrong in the game (VERIFICATION rounds 9-10).
    ch360_probe64(probe_line, probe_sync, "main thread", 1);
    probe_on_thread(0, 16u << 20, "new thread like the game's (hw 0, 16 MB stack)");
    probe_on_thread(2, 0, "new thread on hw 2");
    flush_log(true);
    // The player's linear memory is reserved first (committed as it grows):
    // xsdk's heap takes the free address space at its first growth.
#ifdef CH360_TEST64
    static const char* const argv[] = { "cuphead360", "run", "--console", "1", "--data", "/GameData", "--save", "/save",
                                        "--test64", "1" };
#else
    static const char* const argv[] = { "cuphead360", "run", "--console", "1", "--data", "/GameData", "--save", "/save" };
#endif
    ch360_staged_names = 1;   // GameData/ctex and audio as stage_console.py writes them
#ifdef CH360_TEST64
    // Its own data segments file, so a test build from another wasm build sits beside the play build.
    ch360_player_data_at("game:/GameData/PLAYER_TEST.DAT");
#endif
    if (ch360_prepare("game:/", (int)(sizeof(argv) / sizeof(argv[0])), argv, 448) != 0) {
        logf("player set-up failed");
        flush_log();
        return 1;
    }
    memory_mb(&f, &t);
    logf("player module ready: %u MB free", f);
    {
        unsigned boxes, targets, pages = ch360_remap_counts(&boxes, &targets);
        logf("packs: textures %s; sprite pages for %u atlas pages (%u sprites, %u pages)",
             ch360_ctex_pack >= 0 ? "CTEX*.PAK" : "none (loose files)", pages, boxes, targets);
    }
    pthread_t th;
    if (pthread_create(&th, NULL, log_writer, NULL) == 0) g_log_thread = true;
    static Renderer renderer;
    g_renderer = &renderer;
    if (!renderer.init()) {
        logf("renderer set-up failed");
        flush_log();
        return 1;
    }
    renderer.start_loader();
    {
        pthread_t rth;
        pthread_create(&rth, NULL, replay_thread, NULL);
    }
    if (xsdk_audio_open(0) == 0) {
        g_audio_open = true;
        pthread_t ath;
        // a big stack: carrying a streamed voice on decodes it here, and stb_vorbis's scratch is alloca'd
        pthread_attr_t aattr;
        pthread_attr_init(&aattr);
        pthread_attr_setstacksize(&aattr, 512 * 1024);
        pthread_create(&ath, &aattr, audio_thread, NULL);
        pthread_attr_destroy(&aattr);
    } else {
        logf("no audio output");
    }
    g_stage = "game";
    ch360_frame_drain = 0;   // the log thread drains the player's output instead
    g_player_started = true;
    int code = ch360_start(16, 0);
    memory_mb(&f, &t);
    logf("player exit %d after %u frames; memory %u MB free, player %u MB", code, g_frames, f, ch360_memory_kb() / 1024);
    flush_log();
    return 0;
}
