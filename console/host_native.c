/* The translated player (work/managed/c) as a headless PC program: the
 * console build's code path without the console. It checks what the XEX
 * would get each frame (the draw list's structure, textures, audio) and
 * logs the player's output.
 *
 *   host_native [--root DIR] [--frames N] [--memory MB] [--audio-out F.wav] [-- player args...]
 *
 * --root (default ".") is mounted as the player's "/"; the player's own
 * arguments follow "--" (default: run --console 1 --data /work/GameData
 * --save /work/managed/save_native). Build: tools/build_console.py --native. */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "ch360_host.h"

static long frames_left = 3600, frame_no;
static unsigned long draw_items, draw_vertices, views_seen, bad_lists, textures_files, textures_pixels;
static unsigned long audio_frames;
static FILE* wav;
static clock_t started;
static unsigned long loading_frames;   /* frames marked the loading screen (the console's host draws these again during a hold-up) */
static unsigned long ahead_frames;     /* the hourglass's frames recorded ahead (not drawn) */

/* CH360_TRACE_TEX=<ctex dir>: the textures each frame draws and what they
 * cost the console (their .ctx sizes, runtime ones at 4 bytes a pixel), with
 * how much of that only items wholly off their view draw. A line when a
 * texture is first drawn, and one when the frame's set changes by a MB. */
#define TRACE_IDS 65536
static const char* trace_dir;
static char* tex_key[TRACE_IDS];
static long tex_bytes[TRACE_IDS];          /* -1 not known yet */
static unsigned char tex_seen[TRACE_IDS];  /* drawn before */
static long tex_on_frame[TRACE_IDS], tex_any_frame[TRACE_IDS];
static long trace_last_total = -1;

/* CH360_TRACE_DRAWN=<file>: for tools/texture_cache_sim.py, a line a drawn frame ("F frame loading id id ...":
 * the textures its items sample, once each), each read-ahead ask ("A frame id wanted"), each texture named or
 * given pixels ("K id key", "P id") and the scene read-ahead's marks ("S frame begin"). */
static FILE* drawn_trace;
static unsigned long gc_decommits, gc_commits;   /* the collector's pages given back and taken again (calls) */
static long drawn_mark[TRACE_IDS];
/* The most screen pixels a unit of each texture's u and of its v was drawn across ("D id du dv" lines at the end: over
 * the page's width and height, the screen pixels a texel covered at most; tools/texture_density.py). */
static float drawn_du[TRACE_IDS], drawn_dv[TRACE_IDS];

static void drawn_density(const ch360_frame* f, const ch360_item* it) {
    const ch360_vertex* v = f->vertices + it->first;
    unsigned t, n = it->count / 3;
    if (n > 20) n = 3;   /* a mesh: one transform for all of it */
    for (t = 0; t < n; t++, v += 3) {
        double e1x = v[1].x - v[0].x, e1y = v[1].y - v[0].y, e2x = v[2].x - v[0].x, e2y = v[2].y - v[0].y;
        double d1u = v[1].u - v[0].u, d1v = v[1].v - v[0].v, d2u = v[2].u - v[0].u, d2v = v[2].v - v[0].v;
        double det = d1u * d2v - d1v * d2u, jxu, jyu, jxv, jyv, su, sv;
        if (det > -1e-12 && det < 1e-12) continue;
        jxu = (e1x * d2v - e2x * d1v) / det;
        jyu = (e1y * d2v - e2y * d1v) / det;
        jxv = (e2x * d1u - e1x * d2u) / det;
        jyv = (e2y * d1u - e1y * d2u) / det;
        su = sqrt(jxu * jxu + jyu * jyu);
        sv = sqrt(jxv * jxv + jyv * jyv);
        if (su > drawn_du[it->texture]) drawn_du[it->texture] = (float)su;
        if (sv > drawn_dv[it->texture]) drawn_dv[it->texture] = (float)sv;
    }
}

/* CH360_FRAME_MS=n: each frame takes at least n ms, as on the console (its
 * frame step and audio step follow the clock, so the game runs differently). */
static long frame_ms;
static clock_t frame_start;

static long tex_cost(int id) {
    if (tex_bytes[id] < 0 && tex_key[id] && trace_dir) {
        char path[1024];
        FILE* f;
        snprintf(path, sizeof(path), "%s/%s.ctx", trace_dir, tex_key[id]);
        f = fopen(path, "rb");
        tex_bytes[id] = 0;
        if (f) {
            fseek(f, 0, SEEK_END);
            tex_bytes[id] = ftell(f);
            fclose(f);
        }
    }
    return tex_bytes[id] < 0 ? 0 : tex_bytes[id];
}

static void trace_item(const ch360_item* it, const ch360_view* v) {
    int id = it->texture, on;
    if (id < 0 || id >= TRACE_IDS) return;
    on = !(it->x1 < v->x || it->x0 > v->x + v->w || it->y1 < v->y || it->y0 > v->y + v->h);
    if (on) tex_on_frame[id] = frame_no;
    tex_any_frame[id] = frame_no;
    if (!tex_seen[id]) {
        tex_seen[id] = 1;
        printf("TEX first drawn frame %ld: %s, %ld KB%s\n", frame_no, tex_key[id] ? tex_key[id] : "(runtime)", tex_cost(id) >> 10,
               on ? "" : " (off its view)");
    }
}

static void trace_frame(void) {
    long total = 0, off = 0;
    int id, n = 0, n_off = 0;
    for (id = 0; id < TRACE_IDS; id++) {
        if (tex_any_frame[id] != frame_no || !tex_seen[id]) continue;
        n++;
        total += tex_cost(id);
        if (tex_on_frame[id] != frame_no) {
            n_off++;
            off += tex_cost(id);
        }
    }
    if (trace_last_total < 0 || total > trace_last_total + (1 << 20) || total + (1 << 20) < trace_last_total) {
        printf("TEXSET frame %ld: %d textures, %ld MB; drawn only off view: %d, %ld MB\n", frame_no, n, total >> 20, n_off, off >> 20);
        trace_last_total = total;
    }
}

/* A check of the console's audio carry-on (ch360_mix_extend: its audio thread mixing the last mix's voices on
 * while the game is late): CH360_TEST_CARRY="N,F": every Nth frame, F frames carried on before it, as if
 * that frame had come F frames late. The WAV should run on without a seam (CARRY lines: where). */
static void test_carry(void) {
    static int every = -1, frames = 0;
    if (every < 0) {
        const char* e = getenv("CH360_TEST_CARRY");
        every = 0;
        if (e != NULL && sscanf(e, "%d,%d", &every, &frames) != 2) every = 0;
    }
    if (every > 0 && frame_no > 0 && frame_no % every == 0) {
        unsigned long at = audio_frames;
        int done = 0, n;
        while (done < frames && (n = ch360_mix_extend(frames - done < 256 ? frames - done : 256)) > 0) done += n;
        printf("CARRY frame %ld: %d frames carried on at output frame %lu\n", frame_no, done, at);
    }
}

int ch360_plat_frame_begin(void) {
    test_carry();
    if (frame_ms > 0) {
        while ((clock() - frame_start) * 1000 / CLOCKS_PER_SEC < frame_ms) {
        }
        frame_start = clock();
    }
    if (frame_no > 0 && frame_no % 300 == 0) {
        unsigned reserved, fails, out = ch360_gc_memory(&reserved, &fails);
        static unsigned least = 0xFFFFFFFFu;
        unsigned held = ch360_memory_kb() - out;
        if (held < least) least = held;
        {
            unsigned fc, fi, ff, fd = ch360_fold_counts(&fc, &fi, &ff);
            printf("[host] frame %ld: effects over nothing: %u views left out, %u passes made clears, %u sprites left out, in %u frames\n",
                   frame_no, fd, fc, fi, ff);
        }
        printf("[host] frame %ld: collector's blocks %u MB, %u MB of linear memory given back: %u of %u MB held (least %u); "
               "%lu decommits, %lu commits, %u failed\n", frame_no, reserved >> 10, out >> 10, held >> 10, ch360_memory_kb() >> 10,
               least >> 10, gc_decommits, gc_commits, fails);
    }
    if (frame_no > 0 && frame_no % 600 == 0) {
        double secs = (double)(clock() - started) / CLOCKS_PER_SEC;
        unsigned am[7];
        ch360_audio_memory(am);
        printf("[host] frame %ld: %.1f fps; last frame %lu views, %lu items, %lu vertices; textures %lu files, %lu runtime; "
               "audio %.1f s; memory %u MB; bad lists %lu; items off the sprite pages %u; loading-screen frames %lu (%lu recorded ahead); "
               "audio memory: %u clips decoded %u KB (peak %u), compressed %u KB open + %u KB kept, stream buffers %u KB, %u decoders\n",
               frame_no, frame_no / (secs > 0 ? secs : 1), views_seen, draw_items, draw_vertices, textures_files,
               textures_pixels, audio_frames / 48000.0, ch360_memory_kb() / 1024, bad_lists, ch360_remap_misses(), loading_frames, ahead_frames,
               am[0], am[1], am[2], am[3], am[4], am[5], am[6]);
        fflush(stdout);
    }
    frame_no++;
    return frames_left-- > 0;
}

void ch360_plat_host_stats(unsigned* out, int n) {
    /* no GPU here: the frame count only */
    static long frames_seen = 0;
    int i;
    for (i = 0; i < n; i++) out[i] = 0;
    if (n > 0) out[0] = (unsigned)(frame_no - frames_seen);
    frames_seen = frame_no;
}

int ch360_plat_pad(int index, ch360_pad* out) {
    (void)out;
    return index == 0; /* one connected pad, idle (the bot drives input when given) */
}

/* CH360_DUMP_DRAW=<file>:<n>: every n-th frame's draw list, as u32 frame, u32 length and the bytes:
 * u32 views, items, vertices, then the ch360_view, ch360_item and ch360_vertex records as they are
 * (tools comparing two ways of writing it, such as ConsoleHost's CH360_DRAW=managed). */
static FILE* dump_file;
static FILE* dump_keys;   /* <file>.keys: "id key" for each texture as the platform got it */
static long dump_every;

void ch360_plat_draw(const ch360_frame* f) {
    unsigned v, i;
    if (f->loading & 0x40000000u) {   /* the console keeps these, it doesn't draw them */
        ++ahead_frames;
        return;
    }
    if (f->loading) ++loading_frames;
    if (drawn_trace) {
        fprintf(drawn_trace, "F %ld %d", frame_no, f->loading ? 1 : 0);
        for (i = 0; i < f->item_count; i++) {
            int id = f->items[i].texture;
            if (id >= 0 && id < TRACE_IDS && !f->loading) drawn_density(f, &f->items[i]);
            if (id < 0 || id >= TRACE_IDS || drawn_mark[id] == frame_no + 1) continue;
            drawn_mark[id] = frame_no + 1;
            fprintf(drawn_trace, " %d", id);
        }
        fputc('\n', drawn_trace);
    }
    views_seen = f->view_count;
    draw_items = draw_vertices = 0;
    if (dump_file && dump_every > 0 && frame_no % dump_every == 0) {
        unsigned head[5];
        head[0] = (unsigned)frame_no;
        head[1] = 12 + f->view_count * (unsigned)sizeof(ch360_view) + f->item_count * (unsigned)sizeof(ch360_item) +
                  f->vertex_count * (unsigned)sizeof(ch360_vertex);
        head[2] = f->view_count;
        head[3] = f->item_count;
        head[4] = f->vertex_count;
        fwrite(head, 4, 5, dump_file);
        fwrite(f->views, sizeof(ch360_view), f->view_count, dump_file);
        fwrite(f->items, sizeof(ch360_item), f->item_count, dump_file);
        fwrite(f->vertices, sizeof(ch360_vertex), f->vertex_count, dump_file);
    }
    for (v = 0; v < f->view_count; v++) {
        const ch360_view* view = &f->views[v];
        if (view->first_item + view->items > f->item_count) { bad_lists++; return; }
        for (i = view->first_item; i < view->first_item + view->items; i++) {
            const ch360_item* it = &f->items[i];
            if (it->count % 3 != 0 || it->first + it->count > f->vertex_count) { bad_lists++; return; }
            if (trace_dir) trace_item(it, view);
            draw_items++;
            draw_vertices += it->count;
        }
    }
    if (trace_dir) trace_frame();
}

/* CH360_TRACE_PREFETCH=1: each read-ahead asked for, with the frame (the console's loader reads them). */
static int trace_prefetch = -1;
void ch360_plat_texture_prefetch(int id, int wanted) {
    if (trace_prefetch < 0) trace_prefetch = getenv("CH360_TRACE_PREFETCH") != NULL;
    if (trace_prefetch && id >= 0 && id < TRACE_IDS)
        printf("PREFETCH frame %ld%s: %s\n", frame_no, wanted ? " wanted" : "", tex_key[id] ? tex_key[id] : "(runtime)");
    if (drawn_trace) fprintf(drawn_trace, "A %ld %d %d\n", frame_no, id, wanted);
}

/* A level's page kept as stored bytes on the console (its fill under the hourglass): traced ("G frame id"). */
/* The collector's pages: decommitted and committed in the linear memory's reservation (VirtualAlloc'd by
 * srt_plat_win32.c), so a touch of a page given back faults here as it would freeze the console. */
#ifdef _WIN32
__declspec(dllimport) void* __stdcall VirtualAlloc(void* p, size_t n, unsigned long type, unsigned long protect);
__declspec(dllimport) int __stdcall VirtualFree(void* p, size_t n, unsigned long type);
int ch360_plat_memory_decommit(void* p, size_t n) {
    ++gc_decommits;
    return VirtualFree(p, n, 0x4000 /* MEM_DECOMMIT */) ? 0 : -1;
}
int ch360_plat_memory_commit(void* p, size_t n) {
    ++gc_commits;
    return VirtualAlloc(p, n, 0x1000 /* MEM_COMMIT */, 0x04 /* PAGE_READWRITE */) != NULL ? 0 : -1;
}
#else
int ch360_plat_memory_decommit(void* p, size_t n) { (void)p; (void)n; return -1; }
int ch360_plat_memory_commit(void* p, size_t n) { (void)p; (void)n; return 0; }
#endif

int ch360_plat_file_write(const char* path, const void* data, size_t n) {
    FILE* f = getenv("CH360_FILE_WRITE_FAILS") ? NULL : fopen(path, "wb");   /* the env: the fallback, tested */
    int ok;
    if (f == NULL) {
        printf("[host] file write: cannot open %s (left to the game)\n", path);
        return 0;
    }
    ok = n == 0 || fwrite(data, 1, n, f) == n;
    fclose(f);
    printf("[host] file write: %s, %u bytes%s\n", path, (unsigned)n, ok ? "" : " (short: left to the game)");
    return ok;
}

int ch360_plat_texture_tight(void) {
    static int tight = -1;   /* asked every frame: getenv once */
    if (tight < 0) tight = getenv("CH360_TEXTURE_TIGHT") != NULL;
    return tight;
}

void ch360_plat_texture_guess(int id) {
    if (drawn_trace) fprintf(drawn_trace, "G %ld %d\n", frame_no, id);
}

/* A scene's first seconds' page (its learned list, read under the hold on the console): traced ("A frame id 3"), and
 * counted (CH360_TRACE_PREFETCH=1 names them). */
static unsigned first_asked;
void ch360_plat_texture_first(int id) {
    if (id < 0) {
        if (first_asked) printf("[host] first seconds' pages asked for: %u\n", first_asked);
        first_asked = 0;
        return;
    }
    ++first_asked;
    if (trace_prefetch < 0) trace_prefetch = getenv("CH360_TRACE_PREFETCH") != NULL;
    if (trace_prefetch && id < TRACE_IDS) printf("PREFETCH frame %ld first: %s\n", frame_no, tex_key[id] ? tex_key[id] : "(runtime)");
    if (drawn_trace) fprintf(drawn_trace, "A %ld %d 3\n", frame_no, id);
}

/* The locked 30's frame in between: not drawn here; CH360_TWEEN=1 says yes when asked, 2 for every frame (the bot's
 * scripted runs never ask), and CH360_TRACE_TWEEN=1 tells, every 300 frames, how many items moved and the largest
 * move (a PC check of the matching). */
int ch360_plat_tween(int on) {
    static int allowed = -1;
    if (allowed < 0) allowed = getenv("CH360_TWEEN") != NULL ? atoi(getenv("CH360_TWEEN")) : 0;
    return allowed == 2 || (on && allowed == 1);
}
void ch360_plat_draw_tween(const ch360_frame* f, const float* shifts) {
    static int trace = -1;
    static unsigned frames, items, moved;
    static float most;
    unsigned i;
    if (trace < 0) trace = getenv("CH360_TRACE_TWEEN") != NULL;
    if (!trace) return;
    frames++;
    for (i = 0; i < f->item_count; i++) {
        float dx = shifts[2 * i], dy = shifts[2 * i + 1], d = dx * dx + dy * dy;
        items++;
        if (d > 0.0f) moved++;
        if (d > most) most = d;
    }
    if (frames % 300 == 0) {
        printf("TWEEN frame %ld: %u frames, %u items, %u moved, the largest half-move %.1f px\n", frame_no, frames, items, moved,
               (double)sqrtf(most));
        items = moved = 0;
        most = 0.0f;
    }
}

/* Textures read at once here: none ever pending. */
int ch360_plat_textures_pending(void) { return 0; }
void ch360_plat_textures_scene(int begin) {
    if (trace_prefetch < 0) trace_prefetch = getenv("CH360_TRACE_PREFETCH") != NULL;
    if (trace_prefetch) printf("PREFETCH frame %ld: scene read-ahead %s\n", frame_no, begin ? "begins" : "ends");
    if (drawn_trace) fprintf(drawn_trace, "S %ld %d\n", frame_no, begin);
}
int ch360_plat_textures_waited(void) { return 0; }
/* No output playing in real time here (the WAV takes what is mixed): the player mixes each frame's time. */
int ch360_plat_audio_queued(void) { return -1; }

void ch360_plat_texture_file(int id, const char* key) {
    textures_files++;
    if (drawn_trace) fprintf(drawn_trace, "K %d %s\n", id, key);
    if (dump_keys) {
        fprintf(dump_keys, "%d %s\n", id, key);
        fflush(dump_keys);
    }
    if (id >= 0 && id < TRACE_IDS) {
        free(tex_key[id]);
        tex_key[id] = (char*)malloc(strlen(key) + 1);
        if (tex_key[id]) strcpy(tex_key[id], key);
        tex_bytes[id] = -1;
        tex_seen[id] = 0;
    }
}

void ch360_plat_texture_pixels(int id, int width, int height, const unsigned char* bgra) {
    (void)bgra;
    textures_pixels++;
    if (drawn_trace) fprintf(drawn_trace, "P %d\n", id);
    if (id >= 0 && id < TRACE_IDS) {
        free(tex_key[id]);
        tex_key[id] = NULL;
        tex_bytes[id] = (long)width * height * 4;
    }
}

void ch360_plat_audio_native(const float* lr, int frames) {
    /* the WAV writer reads little-endian floats: this PC is one */
    ch360_plat_audio((const unsigned char*)lr, frames);
}

void ch360_plat_audio(const unsigned char* f32le, int frames) {
    audio_frames += (unsigned long)frames;
    if (wav) {
        /* 16-bit for the WAV, rounded half to even as the managed writer did (Math.Round) */
        static short pcm[48000 * 2];
        int i;
        for (i = 0; i < frames * 2; i++) {
            float f;
            unsigned u = (unsigned)f32le[4 * i] | ((unsigned)f32le[4 * i + 1] << 8) | ((unsigned)f32le[4 * i + 2] << 16) |
                         ((unsigned)f32le[4 * i + 3] << 24);
            memcpy(&f, &u, 4);
            f = f < -1.0f ? -1.0f : f > 1.0f ? 1.0f : f;
            pcm[i] = (short)lrintf(f * 32767.0f);
        }
        fwrite(pcm, 4, (size_t)frames, wav);
    }
}

void ch360_plat_log(const char* text, size_t len) {
    fwrite(text, 1, len, stdout);
    fflush(stdout);
}

static void wav_header(FILE* f, unsigned long frames) {
    unsigned char h[44];
    unsigned long bytes = frames * 4;
    memcpy(h, "RIFF", 4);
    h[4] = (unsigned char)(bytes + 36); h[5] = (unsigned char)((bytes + 36) >> 8);
    h[6] = (unsigned char)((bytes + 36) >> 16); h[7] = (unsigned char)((bytes + 36) >> 24);
    memcpy(h + 8, "WAVEfmt ", 8);
    h[16] = 16; h[17] = h[18] = h[19] = 0;
    h[20] = 1; h[21] = 0; h[22] = 2; h[23] = 0;
    h[24] = 0x80; h[25] = 0xBB; h[26] = 0; h[27] = 0;          /* 48000 */
    h[28] = 0x00; h[29] = 0xEE; h[30] = 0x02; h[31] = 0;       /* 192000 */
    h[32] = 4; h[33] = 0; h[34] = 16; h[35] = 0;
    memcpy(h + 36, "data", 4);
    h[40] = (unsigned char)bytes; h[41] = (unsigned char)(bytes >> 8);
    h[42] = (unsigned char)(bytes >> 16); h[43] = (unsigned char)(bytes >> 24);
    fseek(f, 0, SEEK_SET);
    fwrite(h, 1, 44, f);
}

int main(int argc, char** argv) {
    static const char* defaults[] = { "cuphead360", "run", "--console", "1", "--data", "/work/GameData", "--save",
                                      "/work/managed/save_native" };
    const char* guest[64];
    int guestc = 0, i, code;
    const char* root = ".";
    const char* wav_path = NULL;
    unsigned memory_mb = 1024;
    for (i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--root") && i + 1 < argc) root = argv[++i];
        else if (!strcmp(argv[i], "--frames") && i + 1 < argc) frames_left = atol(argv[++i]);
        else if (!strcmp(argv[i], "--memory") && i + 1 < argc) memory_mb = (unsigned)atoi(argv[++i]);
        else if (!strcmp(argv[i], "--audio-out") && i + 1 < argc) wav_path = argv[++i];
        else if (!strcmp(argv[i], "--")) { i++; break; }
        else { fprintf(stderr, "usage: %s [--root DIR] [--frames N] [--memory MB] [--audio-out F] [-- args]\n", argv[0]); return 2; }
    }
    if (i < argc) {
        guest[guestc++] = "cuphead360";
        for (; i < argc && guestc < 63; i++) guest[guestc++] = argv[i];
    } else {
        for (guestc = 0; guestc < (int)(sizeof(defaults) / sizeof(defaults[0])); guestc++) guest[guestc] = defaults[guestc];
    }
    if (wav_path) {
        wav = fopen(wav_path, "wb");
        if (wav) fwrite("\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0", 1, 44, wav);
    }
    trace_dir = getenv("CH360_TRACE_TEX");
    if (getenv("CH360_TRACE_DRAWN")) drawn_trace = fopen(getenv("CH360_TRACE_DRAWN"), "w");
    if (getenv("CH360_GC_DECOMMIT") && atoi(getenv("CH360_GC_DECOMMIT")) == 0) ch360_gc_memory_off = 1;
    if (getenv("CH360_FOLD_EFFECTS") && atoi(getenv("CH360_FOLD_EFFECTS")) == 0) ch360_fold_effects = 0;
    if (getenv("CH360_TRACE_COVER")) ch360_cover_path = getenv("CH360_TRACE_COVER");
    if (getenv("CH360_TRACE_MISSES")) { extern int rm_trace_misses; rm_trace_misses = atoi(getenv("CH360_TRACE_MISSES")); }
    {
        extern int ch360_pcm_whole;
        ch360_pcm_whole = getenv("CH360_PCM_WHOLE") != NULL;
    }
    if (getenv("CH360_DUMP_DRAW")) {
        static char path[1024];
        char* colon;
        strncpy(path, getenv("CH360_DUMP_DRAW"), sizeof(path) - 1);
        colon = strrchr(path, ':');
        if (colon && colon > path + 1) {
            *colon = 0;
            dump_every = atol(colon + 1);
            dump_file = fopen(path, "wb");
            {
                static char kp[1100];
                sprintf(kp, "%s.keys", path);
                dump_keys = fopen(kp, "w");
            }
        }
    }
    frame_ms = getenv("CH360_FRAME_MS") ? atol(getenv("CH360_FRAME_MS")) : 0;
    /* The module's data segments: the C stage's file (work/managed/c, from the repository's root), or
     * CH360_PLAYER_DATA; the console reads <root>/GameData/PLAYER.DAT (stage_console.py puts it there). */
    ch360_player_data_at(getenv("CH360_PLAYER_DATA") ? getenv("CH360_PLAYER_DATA") : "work/managed/c/PLAYER.DAT");
    for (i = 0; i < TRACE_IDS; i++) tex_bytes[i] = -1;
    started = clock();
    code = ch360_run(root, guestc, guest, memory_mb, 64, -1);
    if (wav) { wav_header(wav, audio_frames); fclose(wav); }
    if (dump_file) fclose(dump_file);
    ch360_cover_write();
    if (drawn_trace) {
        for (i = 0; i < TRACE_IDS; i++)
            if (drawn_du[i] > 0 || drawn_dv[i] > 0) fprintf(drawn_trace, "D %d %.1f %.1f\n", i, drawn_du[i], drawn_dv[i]);
        fclose(drawn_trace);
    }
    printf("[host] exit %d after %ld frames; linear memory %u MB; bad lists %lu\n", code, frame_no, ch360_memory_kb() / 1024,
           bad_lists);
    return code;
}
