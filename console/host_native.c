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

int ch360_plat_frame_begin(void) {
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
        if (p + 40 > len) { bad_lists++; return; }
        items = ch360_u32(d + p + 36);
        p += 40;
        for (i = 0; i < items; i++) {
            unsigned n;
            if (p + 28 > len) { bad_lists++; return; }
            n = ch360_u32(d + p + 24);
            p += 28;
            if (n % 3 != 0 || p + n * 20 > len) { bad_lists++; return; }
            p += n * 20;
            draw_items++;
            draw_vertices += n;
        }
    }
    if (p != len) bad_lists++;
}

void ch360_plat_texture_file(int id, const char* key) {
    (void)id;
    (void)key;
    textures_files++;
}

void ch360_plat_texture_pixels(int id, int width, int height, const unsigned char* bgra) {
    (void)id;
    (void)width;
    (void)height;
    (void)bgra;
    textures_pixels++;
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
    started = clock();
    code = ch360_run(root, guestc, guest, memory_mb, 64, -1);
    if (wav) { wav_header(wav, audio_frames); fclose(wav); }
    printf("[host] exit %d after %ld frames; linear memory %u MB; bad lists %lu\n", code, frame_no, ch360_memory_kb() / 1024,
           bad_lists);
    return code;
}
