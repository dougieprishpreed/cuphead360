/* The player's "ch360" ogg imports (managed ConsoleHost.cs, HostClip): the
 * converted clips' Ogg Vorbis decoded natively by stb_vorbis. The engine's
 * own decoder, NVorbis, runs as translated wasm; on the console the world
 * map's music and ambience decoding took most of a frame (the seventh
 * run, docs/VERIFICATION.md). A clip's file is read whole into memory
 * (music is 1-3 MB) through the runtime's file calls, so the heartbeat
 * counts its reads. Samples go to guest memory as little-endian floats.
 * C89, like ch360_imports.c. */
#include "w2c2_base.h"
#include <stdlib.h>
#include <string.h>
#include "srt.h"
#include "srt_plat.h"
#include "player.h"
#include "ch360_host.h"

#define STB_VORBIS_NO_PUSHDATA_API
#define STB_VORBIS_NO_STDIO
#define STB_VORBIS_NO_INTEGER_CONVERSION
#include "third_party/stb_vorbis/stb_vorbis.c"

int srt_mem_ok(U32 ptr, U32 len);
U8* srt_mem_ptr(U32 ptr);

enum { OGG_MAX = 32 };

typedef struct ch360_ogg {
    stb_vorbis* v;
    unsigned char* data;
    int channels;
} ch360_ogg;

static ch360_ogg oggs[OGG_MAX];
static const char* ogg_root = ".";

void ch360_ogg_root(const char* root_dir) {
    ogg_root = root_dir;
}

static void put_u32(U8* p, U32 u) {
    p[0] = (U8)u;
    p[1] = (U8)(u >> 8);
    p[2] = (U8)(u >> 16);
    p[3] = (U8)(u >> 24);
}

static unsigned char* read_whole(const char* path, int* size) {
    srt_file* f = NULL;
    srt_stat st;
    unsigned char* data;
    size_t got = 0, n;
    if (srt_plat_open(path, SRT_OPEN_READ, &f) != 0) return NULL;
    if (srt_plat_fstat(f, &st) != 0 || st.size == 0 || st.size > (srt_u64)64 * 1024 * 1024) {
        srt_plat_close(f);
        return NULL;
    }
    data = (unsigned char*)malloc((size_t)st.size);
    while (data != NULL && got < (size_t)st.size) {
        size_t want = (size_t)st.size - got;
        if (want > 256 * 1024) want = 256 * 1024;
        if (srt_plat_read(f, data + got, want, &n) != 0 || n == 0) break;
        got += n;
    }
    srt_plat_close(f);
    if (data != NULL && got != (size_t)st.size) {
        free(data);
        data = NULL;
    }
    *size = (int)got;
    return data;
}

/* ogg_open(path, length, info): a handle, info = channels, rate, frames; -1 if it can't be read. */
U32 i5_ch3608_oggX5Fopen(wasmModuleInstance* inst, U32 path, U32 len, U32 info) {
    char full[1024];
    size_t root = strlen(ogg_root);
    unsigned char* data;
    int size = 0, err = 0, h;
    stb_vorbis* v;
    stb_vorbis_info vi;
    (void)inst;
    if (len == 0 || root + len + 2 > sizeof(full) || !srt_mem_ok(path, len) || !srt_mem_ok(info, 12)) return (U32)-1;
    for (h = 0; h < OGG_MAX && oggs[h].v != NULL; h++) {
    }
    if (h == OGG_MAX) return (U32)-1;
    /* The guest's "/" is the runtime's root folder. */
    memcpy(full, ogg_root, root);
    if (root > 0 && full[root - 1] == '/' && srt_mem_ptr(path)[0] == '/') root--;
    memcpy(full + root, srt_mem_ptr(path), len);
    full[root + len] = 0;
    if (ch360_staged_names) {
        /* <folder>/<key>.ogg is staged as <folder>/XX/YYYYYYYY.OGG */
        size_t slash = root + len, end = root + len;
        while (slash > 0 && full[slash - 1] != '/') slash--;
        if (end - slash > 4 && memcmp(full + end - 4, ".ogg", 4) == 0) end -= 4;
        if (slash + 16 > sizeof(full)) return (U32)-1;
        ch360_staged_name(full + slash, end - slash, full + slash);
        memcpy(full + slash + 11, ".OGG", 5);
    }
    data = read_whole(full, &size);
    if (data == NULL) return (U32)-1;
    v = stb_vorbis_open_memory(data, size, &err, NULL);
    if (v == NULL) {
        free(data);
        return (U32)-1;
    }
    vi = stb_vorbis_get_info(v);
    oggs[h].v = v;
    oggs[h].data = data;
    oggs[h].channels = vi.channels;
    put_u32(srt_mem_ptr(info), (U32)vi.channels);
    put_u32(srt_mem_ptr(info) + 4, (U32)vi.sample_rate);
    put_u32(srt_mem_ptr(info) + 8, (U32)stb_vorbis_stream_length_in_samples(v));
    return (U32)h;
}

/* ogg_read(handle, samples, frames): interleaved floats decoded into samples; the frames read (0 at the end). */
U32 i5_ch3608_oggX5Fread(wasmModuleInstance* inst, U32 h, U32 samples, U32 frames) {
    float tmp[4096];
    ch360_ogg* o;
    U32 done = 0;
    U8* dst;
    (void)inst;
    if (h >= OGG_MAX || oggs[h].v == NULL) return 0;
    o = &oggs[h];
    if (frames == 0 || frames > 1u << 20 || !srt_mem_ok(samples, frames * (U32)o->channels * 4)) return 0;
    dst = srt_mem_ptr(samples);
    while (done < frames) {
        int want = (int)(frames - done), n, i;
        if (want > 4096 / o->channels) want = 4096 / o->channels;
        n = stb_vorbis_get_samples_float_interleaved(o->v, o->channels, tmp, want * o->channels);
        if (n <= 0) break;
        for (i = 0; i < n * o->channels; i++) {
            union { float f; U32 u; } s;
            s.f = tmp[i];
            put_u32(dst, s.u);
            dst += 4;
        }
        done += (U32)n;
    }
    return done;
}

/* ogg_seek(handle, frame): 1 if the next read starts there. */
U32 i5_ch3608_oggX5Fseek(wasmModuleInstance* inst, U32 h, U32 frame) {
    (void)inst;
    if (h >= OGG_MAX || oggs[h].v == NULL) return 0;
    return frame == 0 ? (U32)stb_vorbis_seek_start(oggs[h].v) : (U32)stb_vorbis_seek(oggs[h].v, frame);
}

void i5_ch3609_oggX5Fclose(wasmModuleInstance* inst, U32 h) {
    (void)inst;
    if (h >= OGG_MAX || oggs[h].v == NULL) return;
    stb_vorbis_close(oggs[h].v);
    free(oggs[h].data);
    oggs[h].v = NULL;
    oggs[h].data = NULL;
}
