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

int ch360_plat_frame_begin(void) {
    if (frame_ms > 0) {
        while ((clock() - frame_start) * 1000 / CLOCKS_PER_SEC < frame_ms) {
        }
        frame_start = clock();
    }
    if (frame_no > 0 && frame_no % 600 == 0) {
        double secs = (double)(clock() - started) / CLOCKS_PER_SEC;
        printf("[host] frame %ld: %.1f fps; last frame %lu views, %lu items, %lu vertices; textures %lu files, %lu runtime; "
               "audio %.1f s; memory %u MB; bad lists %lu; items off the sprite pages %u; loading-screen frames %lu\n",
               frame_no, frame_no / (secs > 0 ? secs : 1), views_seen, draw_items, draw_vertices, textures_files,
               textures_pixels, audio_frames / 48000.0, ch360_memory_kb() / 1024, bad_lists, ch360_remap_misses(), loading_frames);
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
    if (f->loading) ++loading_frames;
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
void ch360_plat_texture_prefetch(int id) {
    if (trace_prefetch < 0) trace_prefetch = getenv("CH360_TRACE_PREFETCH") != NULL;
    if (trace_prefetch && id >= 0 && id < TRACE_IDS) printf("PREFETCH frame %ld: %s\n", frame_no, tex_key[id] ? tex_key[id] : "(runtime)");
}

void ch360_plat_texture_file(int id, const char* key) {
    textures_files++;
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
    for (i = 0; i < TRACE_IDS; i++) tex_bytes[i] = -1;
    started = clock();
    code = ch360_run(root, guestc, guest, memory_mb, 64, -1);
    if (wav) { wav_header(wav, audio_frames); fclose(wav); }
    if (dump_file) fclose(dump_file);
    printf("[host] exit %d after %ld frames; linear memory %u MB; bad lists %lu\n", code, frame_no, ch360_memory_kb() / 1024,
           bad_lists);
    return code;
}
