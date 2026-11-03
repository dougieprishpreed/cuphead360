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

static void trace_item(int id, const unsigned char* vs, unsigned n, float vx, float vy, float vw, float vh) {
    float x0 = 1e30f, y0 = 1e30f, x1 = -1e30f, y1 = -1e30f;
    unsigned k;
    int on;
    if (id < 0 || id >= TRACE_IDS) return;
    for (k = 0; k < n; k++, vs += 20) {
        float x = ch360_f32(vs), y = ch360_f32(vs + 4);
        if (x < x0) x0 = x;
        if (x > x1) x1 = x;
        if (y < y0) y0 = y;
        if (y > y1) y1 = y;
    }
    on = !(x1 < vx || x0 > vx + vw || y1 < vy || y0 > vy + vh);
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
               "audio %.1f s; memory %u MB; bad lists %lu\n",
               frame_no, frame_no / (secs > 0 ? secs : 1), views_seen, draw_items, draw_vertices, textures_files,
               textures_pixels, audio_frames / 48000.0, ch360_memory_kb() / 1024, bad_lists);
        fflush(stdout);
    }
    frame_no++;
    return frames_left-- > 0;
}

int ch360_plat_pad(int index, ch360_pad* out) {
    (void)out;
    return index == 0; /* one connected pad, idle (the bot drives input when given) */
}

void ch360_plat_submit(const unsigned char* d, unsigned len) {
    unsigned p = 8, v, i, views;
    if (len < 8 || ch360_u32(d) != 0x31464843u) { bad_lists++; return; }
    views = ch360_u32(d + 4);
    views_seen = views;
    draw_items = draw_vertices = 0;
    for (v = 0; v < views; v++) {
        unsigned items;
        float vx, vy, vw, vh;
        if (p + 40 > len) { bad_lists++; return; }
        vx = ch360_f32(d + p);
        vy = ch360_f32(d + p + 4);
        vw = ch360_f32(d + p + 8);
        vh = ch360_f32(d + p + 12);
        items = ch360_u32(d + p + 36);
        p += 40;
        for (i = 0; i < items; i++) {
            unsigned n;
            int id;
            if (p + 28 > len) { bad_lists++; return; }
            id = (int)ch360_u32(d + p);
            n = ch360_u32(d + p + 24);
            p += 28;
            if (n % 3 != 0 || p + n * 20 > len) { bad_lists++; return; }
            if (trace_dir) trace_item(id, d + p, n, vx, vy, vw, vh);
            p += n * 20;
            draw_items++;
            draw_vertices += n;
        }
    }
    if (p != len) bad_lists++;
    if (trace_dir) trace_frame();
}

void ch360_plat_texture_file(int id, const char* key) {
    textures_files++;
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

void ch360_plat_audio(const unsigned char* s16le, int frames) {
    audio_frames += (unsigned long)frames;
    if (wav) fwrite(s16le, 4, (size_t)frames, wav);
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
    frame_ms = getenv("CH360_FRAME_MS") ? atol(getenv("CH360_FRAME_MS")) : 0;
    for (i = 0; i < TRACE_IDS; i++) tex_bytes[i] = -1;
    started = clock();
    code = ch360_run(root, guestc, guest, memory_mb, 64, -1);
    if (wav) { wav_header(wav, audio_frames); fclose(wav); }
    printf("[host] exit %d after %ld frames; linear memory %u MB; bad lists %lu\n", code, frame_no, ch360_memory_kb() / 1024,
           bad_lists);
    return code;
}
