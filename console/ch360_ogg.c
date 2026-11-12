/* The player's "ch360" ogg imports (managed ConsoleHost.cs, HostClip): the
 * converted clips' Ogg Vorbis decoded natively by stb_vorbis. The engine's
 * own decoder, NVorbis, runs as translated wasm; on the console the world
 * map's music and ambience decoding took most of a frame (the seventh
 * run). A clip's file is read whole into memory
 * (music is 1-3 MB) through the runtime's file calls, so the heartbeat
 * counts its reads. Samples go to guest memory as little-endian floats.
 * C89, like ch360_imports.c. */
#include "w2c2_base.h"
#include <stdio.h>
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
    int size;
    int channels;
    int file;            /* its pack entry (file -1: a loose file) */
    unsigned offset;
    float* ring;         /* frames decoded ahead (long clips), AHEAD_FRAMES of them */
    unsigned head, count;
    int ended;           /* the decoder has reached the end */
    float* sbuf;         /* the native mixer's frames of it (mix_voice), STREAM_FRAMES of them */
    unsigned sstart, sframes;
    int pcm_owner;       /* a decoded clip still being decoded from it (pcm_decode), else -1: closed after */
    int close_asked;     /* ogg_close came while that clip held it (or while it was being read) */
    int loading;         /* its bytes are being read on the clip reader's thread (v is NULL until then) */
    int borrowed;        /* its bytes are a kept clip's (cache_borrow), given back at its close */
    int seek_asked;      /* ogg_seek while loading: where to start once it's open */
    U32 seek_frame;
} ch360_ogg;

static ch360_ogg oggs[OGG_MAX];
static void pcm_finish_one(void);   /* a clip still decoding finished, its handle let go */
static void ogg_close_now(U32 h);
static void game_lock(void);        /* the mixer held by the game's thread (the mix carried on, below) */
static void game_unlock(void);
static void conts_forget(U32 kind, U32 id);
static const char* ogg_root = ".";

/* Decoding ahead. A streamed clip's Vorbis decoding ran on the game's thread
 * inside ogg_read: 4.8 ms of each run-and-gun frame and 2-3 ms of the map's
 * on the console (the fourteenth run). A thread on another core (hardware
 * thread 2; the game runs on 0) keeps each long clip's ring of decoded frames
 * full, and ogg_read copies from it, decoding whatever the ring lacks itself.
 * Each clip's decoder is used in the same order either way, so the samples are
 * the same. Short clips are decoded whole when first played, as before. */
enum { AHEAD_FRAMES = 16384, AHEAD_CHUNK = 1024, AHEAD_MIN_LENGTH = 65536, AHEAD_HW_THREAD = 2 };
static srt_mutex* ogg_locks[OGG_MAX];   /* a clip's decoder and ring */
static srt_mutex* wake_lock;
static srt_cond* wake_cond;
static int wake_pending, ahead_state;   /* 0 not started, 1 running, -1 failed */

static void ahead_wake(void) {
    if (ahead_state <= 0) return;
    srt_mutex_lock(wake_lock);
    wake_pending = 1;
    srt_cond_broadcast(wake_cond);
    srt_mutex_unlock(wake_lock);
}

/* One chunk into the clip's ring if it has room (its lock held): 1 if it decoded. */
static int ahead_fill(ch360_ogg* o) {
    unsigned w, room;
    int got;
    if (o->v == NULL || o->ring == NULL || o->ended || o->count + AHEAD_CHUNK > AHEAD_FRAMES) return 0;
    w = (o->head + o->count) % AHEAD_FRAMES;
    room = AHEAD_FRAMES - w;   /* to the ring's end: the next chunk wraps */
    if (room > AHEAD_CHUNK) room = AHEAD_CHUNK;
    got = stb_vorbis_get_samples_float_interleaved(o->v, o->channels, o->ring + (size_t)w * o->channels,
                                                   (int)room * o->channels);
    if (got <= 0) {
        o->ended = 1;
        return 0;
    }
    o->count += (unsigned)got;
    return 1;
}

static void ahead_main(void* arg) {
    (void)arg;
    for (;;) {
        int h, busy = 0;
        for (h = 0; h < OGG_MAX; h++) {
            srt_mutex_lock(ogg_locks[h]);
            busy |= ahead_fill(&oggs[h]);
            srt_mutex_unlock(ogg_locks[h]);
        }
        if (!busy) {
            srt_mutex_lock(wake_lock);
            while (!wake_pending) srt_cond_wait(wake_cond, wake_lock);
            wake_pending = 0;
            srt_mutex_unlock(wake_lock);
        }
    }
}

static void ahead_start(void) {
    int h;
    if (ahead_state != 0) return;
    ahead_state = -1;
    for (h = 0; h < OGG_MAX; h++)
        if ((ogg_locks[h] = srt_mutex_create()) == NULL) return;
    wake_lock = srt_mutex_create();
    wake_cond = srt_cond_create();
    if (wake_lock == NULL || wake_cond == NULL) return;
    /* stb_vorbis decodes with alloca'd scratch: a generous stack */
    if (srt_thread_start(ahead_main, NULL, 256 * 1024, AHEAD_HW_THREAD) != NULL) ahead_state = 1;
}

static void ogg_lock(U32 h) {
    if (ahead_state > 0) srt_mutex_lock(ogg_locks[h]);
}
static void ogg_unlock(U32 h) {
    if (ahead_state > 0) srt_mutex_unlock(ogg_locks[h]);
}

/* Clips from the audio pack (ch360_pack.c) when there is one. The bytes of
 * clips closed lately are kept (CACHE_BYTES) for their next play: a clip
 * longer than the engine's StreamOver streams, opened again each time it
 * sounds (the tenth run's fights opened ~30 files a minute). A scene's load
 * also reads its short clips in ahead (ogg_preload): a clip's first play read
 * it from the drive in the frame. */
enum { CACHE_SLOTS = 512, CACHE_BYTES = 8 * 1024 * 1024 };   /* the Frogs' load read 333 short clips ahead (5.5 MB) */
/* Played clips larger than this aren't kept: music and long loops, read off the game's thread anyway
 * (ASYNC_MIN_BYTES), whose bytes kept after a restart pushed the load's short clips out (the player's hit
 * sounds read from the drive at the next first hit). */
enum { KEEP_PLAYED_MAX = 1024 * 1024 };
typedef struct ogg_cached {
    unsigned char* data;
    int size, file;
    unsigned offset, age, batch;   /* batch: the scene load that read it ahead (0: kept after a play) */
    int users;                     /* plays reading these very bytes (cache_borrow): never let go meanwhile */
} ogg_cached;
static ogg_cached cache[CACHE_SLOTS];
static unsigned cache_bytes, cache_clock;
static int audio_pack = -2;
static srt_mutex* cache_lock;   /* once the clip reader runs (it fills the cache too) */

static unsigned char* cache_take(int file, unsigned offset) {
    int i;
    unsigned char* d = NULL;
    if (cache_lock) srt_mutex_lock(cache_lock);
    for (i = 0; i < CACHE_SLOTS; i++)
        if (cache[i].data != NULL && cache[i].file == file && cache[i].offset == offset) {
            if (cache[i].users > 0) break;   /* played from where it is: not taken */
            d = cache[i].data;
            cache_bytes -= (unsigned)cache[i].size;
            cache[i].data = NULL;
            break;
        }
    if (cache_lock) srt_mutex_unlock(cache_lock);
    return d;
}

/* A kept clip's bytes for a play (NULL: not kept), left in place, kept as if just used and never let go while any play
 * reads them (cache_return at its close). Taken out while it played, a second play of the same clip meanwhile found
 * nothing and read it from the drive in the frame (Treetop Trouble's ladybugs bounce on top of each other: 15 reads
 * of the same few sounds in one visit); a copy for each play doubled what the sounds held. */
static unsigned char* cache_borrow(int file, unsigned offset, int size) {
    int i;
    unsigned char* d = NULL;
    if (cache_lock) srt_mutex_lock(cache_lock);
    for (i = 0; i < CACHE_SLOTS; i++)
        if (cache[i].data != NULL && cache[i].file == file && cache[i].offset == offset && cache[i].size == size) {
            d = cache[i].data;
            cache[i].users++;
            cache[i].age = ++cache_clock;
            break;
        }
    if (cache_lock) srt_mutex_unlock(cache_lock);
    return d;
}

static void cache_return(int file, unsigned offset) {
    int i;
    if (cache_lock) srt_mutex_lock(cache_lock);
    for (i = 0; i < CACHE_SLOTS; i++)
        if (cache[i].data != NULL && cache[i].file == file && cache[i].offset == offset && cache[i].users > 0) {
            cache[i].users--;
            cache[i].age = ++cache_clock;
            break;
        }
    if (cache_lock) srt_mutex_unlock(cache_lock);
}

/* Keeps a clip's bytes, making room from the least recently kept; a clip read ahead (batch) makes none
 * from its own load's others (0 when it doesn't fit then, and the bytes are freed). */
static int cache_put_batch(unsigned char* data, int size, int file, unsigned offset, unsigned batch) {
    int i, slot = -1, kept = 1;
    if (file < 0 || size > CACHE_BYTES / 2 || (batch == 0 && size > KEEP_PLAYED_MAX)) {
        free(data);
        return 0;
    }
    if (cache_lock) srt_mutex_lock(cache_lock);
    for (i = 0; i < CACHE_SLOTS; i++)   /* already there (read ahead twice, or played meanwhile) */
        if (cache[i].data != NULL && cache[i].file == file && cache[i].offset == offset) {
            if (cache_lock) srt_mutex_unlock(cache_lock);
            free(data);
            return 1;
        }
    while (cache_bytes + (unsigned)size > CACHE_BYTES || slot < 0) {
        int oldest = -1;
        for (i = 0; i < CACHE_SLOTS; i++) {
            if (cache[i].data == NULL) {
                if (slot < 0) slot = i;
                continue;
            }
            if (cache[i].users > 0) continue;   /* being played from */
            if (oldest < 0 || cache[i].age < cache[oldest].age) oldest = i;
        }
        if (slot >= 0 && cache_bytes + (unsigned)size <= CACHE_BYTES) break;
        if (oldest < 0 || (batch != 0 && cache[oldest].batch == batch)) {
            slot = -1;
            break;
        }
        free(cache[oldest].data);
        cache_bytes -= (unsigned)cache[oldest].size;
        cache[oldest].data = NULL;
        if (slot < 0) slot = oldest;
    }
    if (slot < 0) {
        free(data);
        kept = 0;
    } else {
        cache[slot].data = data;
        cache[slot].size = size;
        cache[slot].file = file;
        cache[slot].offset = offset;
        cache[slot].age = ++cache_clock;
        cache[slot].batch = batch;
        cache[slot].users = 0;
        cache_bytes += (unsigned)size;
    }
    if (cache_lock) srt_mutex_unlock(cache_lock);
    return kept;
}

static void cache_put(unsigned char* data, int size, int file, unsigned offset) {
    cache_put_batch(data, size, file, offset, 0);
}

/* Clips read from the drive in the frame since the last load (a first play that neither the load's read-ahead
 * nor the bytes kept from a play covered), told with the next load's read-ahead tally; CH360_TRACE_CLIPS=1 names
 * each as it is read (a PC check of what the read-ahead misses). */
static unsigned frame_reads, frame_read_bytes;
static int trace_clips = -1;

/* The clip's bytes from the pack: its key is the file name without ".ogg". */
static unsigned char* read_packed(const U8* path, U32 len, int* size, int* file, unsigned* offset, int* borrowed) {
    ch360_pack_entry e;
    U32 slash = len, end = len;
    unsigned char* data;
    while (slash > 0 && path[slash - 1] != '/') slash--;
    if (end - slash > 4 && memcmp(path + end - 4, ".ogg", 4) == 0) end -= 4;
    if (!ch360_pack_find(audio_pack, (const char*)path + slash, end - slash, &e) || e.size == 0 || e.size > 64u * 1024 * 1024)
        return NULL;
    *file = e.file;
    *offset = e.offset;
    *size = (int)e.size;
    data = cache_borrow(e.file, e.offset, (int)e.size);
    if (data != NULL) {
        *borrowed = 1;
        return data;
    }
    data = (unsigned char*)malloc(e.size);
    if (data != NULL && ch360_pack_read(&e, 0, data, e.size) != 0) {
        free(data);
        data = NULL;
    }
    if (data != NULL) {
        frame_reads++;
        frame_read_bytes += e.size;
        if (trace_clips < 0) trace_clips = getenv("CH360_TRACE_CLIPS") != NULL;
        if (trace_clips) {
            char msg[300];
            int n = sprintf(msg, "CLIP read in the frame: %.*s (%u KB)\n", (int)(end - slash), (const char*)path + slash,
                            (unsigned)(e.size >> 10));
            ch360_plat_log(msg, (size_t)n);
        }
    }
    return data;
}

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

/* ---- clips read off the game's thread ----
 * A clip's file was read whole inside ogg_open, on the game's thread: the map's music, a few MB from the
 * USB drive, held a frame for 250 ms (the twenty-fifth run). Large clips in the pack now open at once
 * with their channels, rate and length from GameData/AUDIOINFO.BIN (tools/stage_console.py reads them
 * from each file's Ogg headers), and their bytes are read on a thread of their own (hardware thread 5,
 * the log writer's, mostly idle) through a second set of the pack's file handles. Until then the clip is
 * silent and its voice holds its place (mix_voice), a seek waits to be applied, and what needs its
 * samples at once (ogg_read, pcm_decode) waits for the read. */
enum { ASYNC_MIN_BYTES = 256 * 1024, LOAD_QUEUE = 32, CLIP_READER_HW_THREAD = 5 };   /* music, ambience, long loops: a sound effect waits for none */
static unsigned* audio_info;          /* hi, lo, channels, rate, frames per clip, sorted by key */
static unsigned audio_info_count;
static int audio_info_tried, audio_pack_async = -2;
static ch360_pack_reader* async_reader;
static srt_mutex* load_lock;
static srt_cond* load_cond;          /* a job queued, or one done */
static int load_queue[LOAD_QUEUE], load_head, load_count, loader_state;   /* 0 not started, 1 running, -1 failed */
static ch360_pack_entry load_entry[OGG_MAX];
static unsigned load_frames[OGG_MAX];   /* the table's length, checked against the decoder's */
static U32 ogg_seek_locked(ch360_ogg* o, U32 frame);

static unsigned rd_le(const unsigned char* p) { return p[0] | (unsigned)p[1] << 8 | (unsigned)p[2] << 16 | (unsigned)p[3] << 24; }

static void info_load(void) {
    char path[1024];
    unsigned char* raw;
    int size = 0;
    size_t rl = strlen(ogg_root);
    unsigned i;
    audio_info_tried = 1;
    if (rl > 0 && ogg_root[rl - 1] == '/') rl--;
    if (rl + 32 > sizeof(path)) return;
    sprintf(path, "%.*s/GameData/AUDIOINFO.BIN", (int)rl, ogg_root);
    raw = read_whole(path, &size);
    if (raw == NULL) return;
    if (size >= 8 && rd_le(raw) == 0x49414843u && (unsigned)size >= 8 + rd_le(raw + 4) * 20u) {   /* "CHAI" */
        audio_info_count = rd_le(raw + 4);
        audio_info = (unsigned*)malloc((size_t)audio_info_count * 20 + 1);
        if (audio_info == NULL) audio_info_count = 0;
        for (i = 0; audio_info != NULL && i < audio_info_count * 5; i++) audio_info[i] = rd_le(raw + 8 + 4 * i);
    }
    free(raw);
}

/* The clip's channels, rate and frames from the table: 1 if it has the clip. */
static int info_find(const char* key, size_t len, unsigned* channels, unsigned* rate, unsigned* frames) {
    unsigned hi, lo, a = 0, b;
    if (!audio_info_tried) info_load();
    if (audio_info == NULL) return 0;
    ch360_fnv64(key, len, &hi, &lo);
    b = audio_info_count;
    while (a < b) {
        unsigned m = (a + b) / 2, mh = audio_info[m * 5], ml = audio_info[m * 5 + 1];
        if (mh < hi || (mh == hi && ml < lo)) a = m + 1; else b = m;
    }
    if (a >= audio_info_count || audio_info[a * 5] != hi || audio_info[a * 5 + 1] != lo) return 0;
    *channels = audio_info[a * 5 + 2];
    *rate = audio_info[a * 5 + 3];
    *frames = audio_info[a * 5 + 4];
    return *channels >= 1 && *channels <= 2;
}

/* The clip's bytes, in pieces: the drive takes one request at a time, and a clip read in one went ahead
 * of everything else for its whole length (a 5 MB track ~550 ms): a sound effect read on the game's thread
 * meanwhile (the twenty-sixth run's Frogs fight: a 338 ms frame in the mix) and the texture loader's reads
 * waited for all of it. */
enum { LOAD_PIECE = 64 * 1024 };

/* Short clips a load asked to read ahead (ogg_preload): read when no large clip waits, into the cache. */
enum { PRELOAD_QUEUE = 1024 };   /* a load's clips and every other still built (the Frogs': ~350 and the player's) */
static ch360_pack_entry pre_key[PRELOAD_QUEUE], pre_from[PRELOAD_QUEUE];
static unsigned pre_head, pre_count, pre_batch, pre_batch_of[PRELOAD_QUEUE];
static unsigned pre_done, pre_kept, pre_bytes;

static void preload_one(const ch360_pack_entry* key, const ch360_pack_entry* from, unsigned batch) {
    unsigned char* data;
    unsigned at = 0;
    int i, have = 0;
    srt_mutex_lock(cache_lock);
    for (i = 0; i < CACHE_SLOTS && !have; i++)
        if (cache[i].data != NULL && cache[i].file == key->file && cache[i].offset == key->offset) {
            /* kept already: this load's now (its others don't push it out) and kept as if just read */
            have = 1;
            cache[i].batch = batch;
            cache[i].age = ++cache_clock;
        }
    srt_mutex_unlock(cache_lock);
    if (have || (data = (unsigned char*)malloc(key->size)) == NULL) return;
    while (at < key->size) {
        unsigned n = key->size - at < LOAD_PIECE ? key->size - at : LOAD_PIECE;
        if (ch360_pack_read_with(async_reader, from, at, data + at, n) != 0) {
            free(data);
            return;
        }
        at += n;
    }
    pre_done++;
    pre_bytes += key->size;
    pre_kept += (unsigned)cache_put_batch(data, (int)key->size, key->file, key->offset, batch);
}
static int read_pieces(int h) {
    ch360_ogg* o = &oggs[h];
    unsigned at = 0, size = (unsigned)o->size;
    while (at < size) {
        unsigned n = size - at < LOAD_PIECE ? size - at : LOAD_PIECE;
        if (o->close_asked) return 0;   /* closed meanwhile: the rest isn't wanted */
        if (ch360_pack_read_with(async_reader, &load_entry[h], at, o->data + at, n) != 0) return 0;
        at += n;
    }
    return 1;
}

/* The clip's bytes read and its decoder opened (or, closed meanwhile, let go). */
static void load_one(int h) {
    ch360_ogg* o = &oggs[h];
    int err = 0, ok = read_pieces(h);
    stb_vorbis* v = ok ? stb_vorbis_open_memory(o->data, o->size, &err, NULL) : NULL;
    ogg_lock((U32)h);
    if (o->close_asked || v == NULL) {
        if (v != NULL) stb_vorbis_close(v);
        if (!o->close_asked) {
            char msg[96];
            int n = sprintf(msg, "audio: a clip read off the game's thread didn't open (read %d, error %d)\n", ok, err);
            ch360_plat_log(msg, (size_t)n);
        }
        free(o->data);
        o->data = NULL;
        o->close_asked = 0;
        o->seek_asked = 0;
        o->loading = 0;   /* the slot is free again (v NULL): the clip stays silent */
    } else {
        stb_vorbis_info vi = stb_vorbis_get_info(v);
        unsigned length = stb_vorbis_stream_length_in_samples(v);
        if ((int)vi.channels != o->channels || length != load_frames[h]) {
            char msg[160];
            int n = sprintf(msg, "audio: AUDIOINFO.BIN said %d channels and %u frames, the clip has %d and %u\n", o->channels,
                            load_frames[h], vi.channels, length);
            ch360_plat_log(msg, (size_t)n);
            o->channels = vi.channels;
        }
        o->v = v;
        o->ring = length >= AHEAD_MIN_LENGTH && ahead_state > 0 ? (float*)malloc((size_t)AHEAD_FRAMES * vi.channels * sizeof(float)) : NULL;
        if (o->seek_asked) {
            ogg_seek_locked(o, o->seek_frame);
            o->sstart = o->seek_frame;
            o->sframes = 0;
            o->seek_asked = 0;
        }
        o->loading = 0;
    }
    ogg_unlock((U32)h);
    if (o->ring != NULL) ahead_wake();
}

volatile const char* ch360_clip_reader_stage = "not started";

static void loader_main(void* arg) {
    (void)arg;
    for (;;) {
        int h;
        ch360_clip_reader_stage = "waiting for its lock";
        srt_mutex_lock(load_lock);
        ch360_clip_reader_stage = "waiting for a clip";
        while (load_count == 0 && pre_count == 0) srt_cond_wait(load_cond, load_lock);
        if (load_count == 0) {   /* nothing that plays waits: a clip read ahead */
            ch360_pack_entry key = pre_key[pre_head], from = pre_from[pre_head];
            unsigned batch = pre_batch_of[pre_head];
            pre_head = (pre_head + 1) % PRELOAD_QUEUE;
            pre_count--;
            srt_mutex_unlock(load_lock);
            ch360_clip_reader_stage = "reading a clip ahead";
            preload_one(&key, &from, batch);
            continue;
        }
        h = load_queue[load_head];
        load_head = (load_head + 1) % LOAD_QUEUE;
        load_count--;
        srt_mutex_unlock(load_lock);
        ch360_clip_reader_stage = "reading a clip that plays";
        load_one(h);
        srt_mutex_lock(load_lock);
        srt_cond_broadcast(load_cond);   /* waiters for this clip */
        srt_mutex_unlock(load_lock);
    }
}

static int loader_ready(void) {
    if (loader_state != 0) return loader_state > 0;
    loader_state = -1;
    ahead_start();   /* the clips' locks */
    if (ahead_state <= 0) return 0;
    if (audio_pack_async == -2) audio_pack_async = ch360_pack_open(ogg_root, "AUDIO");
    async_reader = ch360_pack_reader_new();
    load_lock = srt_mutex_create();
    load_cond = srt_cond_create();
    cache_lock = srt_mutex_create();
    if (audio_pack_async < 0 || async_reader == NULL || load_lock == NULL || load_cond == NULL || cache_lock == NULL) return 0;
    if (srt_thread_start(loader_main, NULL, 128 * 1024, CLIP_READER_HW_THREAD) == NULL) return 0;
    loader_state = 1;
    return 1;
}

/* The clip being read: wait for it (what needs its samples now). */
static void wait_loaded(U32 h) {
    if (!oggs[h].loading || loader_state <= 0) return;
    srt_mutex_lock(load_lock);
    while (oggs[h].loading) srt_cond_wait(load_cond, load_lock);
    srt_mutex_unlock(load_lock);
}

/* A large clip in the pack with its numbers in the table: its handle now, its bytes read on the clip
 * reader's thread. 1 if it was queued (info filled), 0 to read it here. */
static int open_async(int h, const U8* path, U32 len, U32 info) {
    ch360_pack_entry e, ea;
    U32 slash = len, end = len;
    unsigned channels, rate, frames;
    unsigned char* data;
    while (slash > 0 && path[slash - 1] != '/') slash--;
    if (end - slash > 4 && memcmp(path + end - 4, ".ogg", 4) == 0) end -= 4;
    if (!ch360_pack_find(audio_pack, (const char*)path + slash, end - slash, &e) || e.size < ASYNC_MIN_BYTES || e.size > 64u * 1024 * 1024)
        return 0;
    if (!info_find((const char*)path + slash, end - slash, &channels, &rate, &frames)) return 0;
    if (!loader_ready() || !ch360_pack_find(audio_pack_async, (const char*)path + slash, end - slash, &ea)) return 0;
    data = cache_take(e.file, e.offset);
    if (data != NULL) {   /* kept from its last play: opened here after all */
        cache_put(data, (int)e.size, e.file, e.offset);
        return 0;
    }
    data = (unsigned char*)malloc(e.size);
    if (data == NULL) return 0;
    srt_mutex_lock(load_lock);
    if (load_count == LOAD_QUEUE) {
        srt_mutex_unlock(load_lock);
        free(data);
        return 0;
    }
    ogg_lock((U32)h);
    oggs[h].v = NULL;
    oggs[h].data = data;
    oggs[h].size = (int)e.size;
    oggs[h].channels = (int)channels;
    oggs[h].file = e.file;
    oggs[h].offset = e.offset;
    oggs[h].head = oggs[h].count = 0;
    oggs[h].ended = 0;
    oggs[h].sstart = oggs[h].sframes = 0;
    oggs[h].ring = NULL;
    oggs[h].sbuf = NULL;
    oggs[h].loading = 1;
    oggs[h].seek_asked = 0;
    ogg_unlock((U32)h);
    load_entry[h] = ea;
    load_frames[h] = frames;
    load_queue[(load_head + load_count) % LOAD_QUEUE] = h;
    load_count++;
    srt_cond_broadcast(load_cond);
    srt_mutex_unlock(load_lock);
    put_u32(srt_mem_ptr(info), channels);
    put_u32(srt_mem_ptr(info) + 4, rate);
    put_u32(srt_mem_ptr(info) + 8, frames);
    return 1;
}

/* ogg_open(path, length, info): a handle, info = channels, rate, frames; -1 if it can't be read. */
/* ogg_preload(path, len, batch): a short clip of the scene being loaded, read into the cache on the clip
 * reader's thread (when no large clip waits) so its first play finds it in memory; batch numbers the load
 * (a load's clips don't push each other out). Large clips (read off the game's thread anyway) and clips
 * already kept are left. */
void i5_ch36011_oggX5Fpreload(wasmModuleInstance* inst, U32 path, U32 len, U32 batch) {
    ch360_pack_entry e, ea;
    U32 slash = len, end = len;
    const U8* p;
    (void)inst;
    if (len == 0 || len > 1024 || !srt_mem_ok(path, len) || batch == 0) return;
    if (audio_pack == -2) audio_pack = ch360_pack_open(ogg_root, "AUDIO");
    if (audio_pack < 0 || !loader_ready()) return;
    p = srt_mem_ptr(path);
    while (slash > 0 && p[slash - 1] != '/') slash--;
    if (end - slash > 4 && memcmp(p + end - 4, ".ogg", 4) == 0) end -= 4;
    if (!ch360_pack_find(audio_pack, (const char*)p + slash, end - slash, &e) || e.size == 0 || e.size >= ASYNC_MIN_BYTES ||
        !ch360_pack_find(audio_pack_async, (const char*)p + slash, end - slash, &ea))
        return;
    srt_mutex_lock(load_lock);
    if (batch != pre_batch) {   /* a new load: the last one's tally, and what it still had queued goes */
        if (pre_batch != 0 && pre_done > 0) {
            char msg[128];
            int n = sprintf(msg, "audio: %u short clips read ahead (%u KB), %u kept\n", pre_done, pre_bytes >> 10, pre_kept);
            ch360_plat_log(msg, (size_t)n);
        }
        if (frame_reads > 0) {
            char msg[128];
            int n = sprintf(msg, "audio: %u clips read from the drive in the frame since the last load (%u KB)\n", frame_reads,
                            frame_read_bytes >> 10);
            ch360_plat_log(msg, (size_t)n);
            frame_reads = frame_read_bytes = 0;
        }
        pre_batch = batch;
        pre_count = 0;
        pre_done = pre_kept = pre_bytes = 0;
    }
    if (pre_count < PRELOAD_QUEUE) {
        unsigned at = (pre_head + pre_count) % PRELOAD_QUEUE;
        pre_key[at] = e;
        pre_from[at] = ea;
        pre_batch_of[at] = batch;
        pre_count++;
        srt_cond_broadcast(load_cond);
    }
    srt_mutex_unlock(load_lock);
}

U32 i5_ch3608_oggX5Fopen(wasmModuleInstance* inst, U32 path, U32 len, U32 info) {
    char full[1024];
    size_t root = strlen(ogg_root);
    unsigned char* data;
    int size = 0, err = 0, h, borrowed = 0;
    unsigned length;
    stb_vorbis* v;
    stb_vorbis_info vi;
    (void)inst;
    if (len == 0 || root + len + 2 > sizeof(full) || !srt_mem_ok(path, len) || !srt_mem_ok(info, 12)) return (U32)-1;
    for (h = 0; h < OGG_MAX && (oggs[h].v != NULL || oggs[h].loading); h++) {
    }
    if (h == OGG_MAX) {   /* clips still decoding hold handles: one finished now lets its go */
        game_lock();
        pcm_finish_one();
        game_unlock();
        for (h = 0; h < OGG_MAX && (oggs[h].v != NULL || oggs[h].loading); h++) {
        }
    }
    if (h == OGG_MAX) return (U32)-1;
    oggs[h].pcm_owner = -1;
    oggs[h].close_asked = 0;
    oggs[h].borrowed = 0;
    if (audio_pack == -2) audio_pack = ch360_pack_open(ogg_root, "AUDIO");
    oggs[h].file = -1;
    oggs[h].offset = 0;
    if (audio_pack >= 0) {
        if (open_async(h, srt_mem_ptr(path), len, info)) return (U32)h;
        data = read_packed(srt_mem_ptr(path), len, &size, &oggs[h].file, &oggs[h].offset, &borrowed);
        if (data == NULL) return (U32)-1;
        goto opened;
    }
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
opened:
    v = stb_vorbis_open_memory(data, size, &err, NULL);
    if (v == NULL) {
        if (borrowed) cache_return(oggs[h].file, oggs[h].offset);
        else free(data);
        return (U32)-1;
    }
    vi = stb_vorbis_get_info(v);
    length = stb_vorbis_stream_length_in_samples(v);
    if (length >= AHEAD_MIN_LENGTH) ahead_start();
    ogg_lock((U32)h);
    oggs[h].v = v;
    oggs[h].data = data;
    oggs[h].borrowed = borrowed;
    oggs[h].size = size;
    oggs[h].channels = vi.channels;
    oggs[h].head = oggs[h].count = 0;
    oggs[h].ended = 0;
    oggs[h].sstart = oggs[h].sframes = 0;
    oggs[h].ring = length >= AHEAD_MIN_LENGTH && ahead_state > 0
                       ? (float*)malloc((size_t)AHEAD_FRAMES * vi.channels * sizeof(float)) : NULL;
    ogg_unlock((U32)h);
    if (oggs[h].ring != NULL) ahead_wake();
    put_u32(srt_mem_ptr(info), (U32)vi.channels);
    put_u32(srt_mem_ptr(info) + 4, (U32)vi.sample_rate);
    put_u32(srt_mem_ptr(info) + 8, (U32)length);
    return (U32)h;
}

/* Up to frames of the clip's frames, native floats, into dst (its lock held): what the thread
 * decoded ahead, then the rest decoded here; the frames read (0 at the end). */
static unsigned ogg_take(ch360_ogg* o, float* dst, unsigned frames) {
    unsigned done = 0;
    while (done < frames && o->count > 0) {
        unsigned n = o->count;
        if (n > frames - done) n = frames - done;
        if (n > AHEAD_FRAMES - o->head) n = AHEAD_FRAMES - o->head;
        memcpy(dst + (size_t)done * o->channels, o->ring + (size_t)o->head * o->channels, (size_t)n * o->channels * sizeof(float));
        o->head = (o->head + n) % AHEAD_FRAMES;
        o->count -= n;
        done += n;
    }
    while (done < frames && !o->ended) {
        int n = stb_vorbis_get_samples_float_interleaved(o->v, o->channels, dst + (size_t)done * o->channels,
                                                         (int)(frames - done) * o->channels);
        if (n <= 0) {
            o->ended = 1;
            break;
        }
        done += (unsigned)n;
    }
    return done;
}

/* ogg_read(handle, samples, frames): interleaved floats decoded into samples; the frames read (0 at the end). */
U32 i5_ch3608_oggX5Fread(wasmModuleInstance* inst, U32 h, U32 samples, U32 frames) {
    float tmp[4096];
    ch360_ogg* o;
    U32 done = 0;
    U8* dst;
    (void)inst;
    if (h < OGG_MAX) wait_loaded(h);
    if (h >= OGG_MAX || oggs[h].v == NULL) return 0;
    o = &oggs[h];
    if (frames == 0 || frames > 1u << 20 || !srt_mem_ok(samples, frames * (U32)o->channels * 4)) return 0;
    dst = srt_mem_ptr(samples);
    game_lock();
    ogg_lock(h);
    while (done < frames) {
        unsigned want = frames - done, n, i;
        if (want > 4096u / (unsigned)o->channels) want = 4096u / (unsigned)o->channels;
        n = ogg_take(o, tmp, want);
        if (n == 0) break;
        for (i = 0; i < n * (unsigned)o->channels; i++) {
            union { float f; U32 u; } s;
            s.f = tmp[i];
            put_u32(dst, s.u);
            dst += 4;
        }
        done += n;
    }
    ogg_unlock(h);
    game_unlock();
    if (o->ring != NULL) ahead_wake();
    return done;
}

/* The next take starts at frame (its lock held): 1 if it does. */
static U32 ogg_seek_locked(ch360_ogg* o, U32 frame) {
    U32 ok = frame == 0 ? (U32)stb_vorbis_seek_start(o->v) : (U32)stb_vorbis_seek(o->v, frame);
    o->head = o->count = 0;
    o->ended = 0;
    return ok;
}

/* ogg_seek(handle, frame): 1 if the next read starts there. */
U32 i5_ch3608_oggX5Fseek(wasmModuleInstance* inst, U32 h, U32 frame) {
    U32 ok;
    (void)inst;
    if (h >= OGG_MAX) return 0;
    game_lock();
    ogg_lock(h);
    if (oggs[h].loading) {
        oggs[h].seek_asked = 1;
        oggs[h].seek_frame = frame;
        ogg_unlock(h);
        game_unlock();
        return 1;
    }
    if (oggs[h].v == NULL) {
        ogg_unlock(h);
        game_unlock();
        return 0;
    }
    ok = ogg_seek_locked(&oggs[h], frame);
    /* the mixer's buffer starts again there too (the engine's Seek) */
    oggs[h].sstart = frame;
    oggs[h].sframes = 0;
    ogg_unlock(h);
    game_unlock();
    if (oggs[h].ring != NULL) ahead_wake();
    return ok;
}

static void ogg_close_now(U32 h) {
    ogg_lock(h);
    stb_vorbis_close(oggs[h].v);
    if (oggs[h].borrowed) cache_return(oggs[h].file, oggs[h].offset);
    else cache_put(oggs[h].data, oggs[h].size, oggs[h].file, oggs[h].offset);
    oggs[h].borrowed = 0;
    free(oggs[h].ring);
    free(oggs[h].sbuf);
    oggs[h].ring = NULL;
    oggs[h].sbuf = NULL;
    oggs[h].v = NULL;
    oggs[h].data = NULL;
    oggs[h].pcm_owner = -1;
    oggs[h].close_asked = 0;
    ogg_unlock(h);
}

void i5_ch3609_oggX5Fclose(wasmModuleInstance* inst, U32 h) {
    (void)inst;
    if (h < OGG_MAX && oggs[h].loading) {
        ogg_lock(h);
        if (oggs[h].loading) {   /* the reader lets it go when it's done */
            oggs[h].close_asked = 1;
            ogg_unlock(h);
            return;
        }
        ogg_unlock(h);
    }
    if (h >= OGG_MAX || oggs[h].v == NULL) return;
    game_lock();
    conts_forget(2, h);
    if (oggs[h].pcm_owner >= 0) {   /* a clip still decoding from it: closed when it's done */
        oggs[h].close_asked = 1;
        game_unlock();
        return;
    }
    ogg_close_now(h);
    game_unlock();
}

/* ---- the native mixer ----
 * The engine's mixer (Audio.Step) ran as translated wasm, where each sample's
 * loads and stores of guest floats go through byte-swapped words and the stack:
 * a load-hit-store stall each on the console, 2-3 ms of every frame for 4-12
 * voices (the fifteenth run). Here the samples stay native: short clips are
 * decoded whole into host memory (pcm_decode), long ones stream from their
 * decoder into a buffer per handle, and each voice mixes into a native block
 * (mix_voice) that goes to the platform as it is (mix_end). The engine keeps
 * the voices, gains, pitches and positions; the arithmetic per sample is its
 * Advance's, so the mix is the same. */
enum { PCM_MAX = 4096, STREAM_FRAMES = 8192, MIX_MAX_FRAMES = 16384 };
/* Decoded clips are kept as 16-bit samples (the clips' own depth; the mixer scales them): 32-bit floats
 * held twice the memory, 8.3 MB at their peak in Hilda Berg on the PC host. */
typedef struct ch360_pcm {
    short* data;
    unsigned frames;
    int channels;
    unsigned decoded;   /* frames decoded so far (the rest come in mix_begin, or as a voice reaches them) */
    int h;              /* the handle it decodes from while decoded < frames, else -1 */
} ch360_pcm;
static ch360_pcm pcms[PCM_MAX];
/* A clip's first frames are decoded when it's first played, the rest a share each mix: decoding whole
 * clips at their first play was 22-40 ms of a frame (the twenty-first run: hits, a boss's attacks, with
 * their sounds), Vorbis on the console ~35 ms a second of sound. */
enum { PCM_FIRST_FRAMES = 4096, PCM_STEP_FRAMES = 2048 };
static unsigned pcm_pending;   /* clips not decoded whole yet */
static size_t pcm_live, pcm_peak;   /* decoded clips' bytes now, and their highest */
int ch360_pcm_whole;           /* a check (host_native's CH360_PCM_WHOLE): clips decoded whole at their first play */
static unsigned pcm_next;      /* where mix_begin's share starts looking */

/* Floats to 16-bit samples, rounded and clamped. */
static void to_short(const float* src, short* dst, unsigned n) {
    unsigned i;
    for (i = 0; i < n; i++) {
        float f = src[i] * 32767.0f;
        int v = (int)(f >= 0.0f ? f + 0.5f : f - 0.5f);
        dst[i] = (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : v);
    }
}

/* More of a clip decoded, up to frame upto; the handle let go (closed if asked) when it's whole. */
static void pcm_more(int id, unsigned upto) {
    static float tmp[PCM_STEP_FRAMES * 2];
    ch360_pcm* p = &pcms[id];
    ch360_ogg* o;
    unsigned n;
    if (p->h < 0 || p->data == NULL) return;
    o = &oggs[p->h];
    if (upto > p->frames) upto = p->frames;
    ogg_lock((U32)p->h);
    while (p->decoded < upto) {
        unsigned want = upto - p->decoded, cap = (unsigned)(sizeof(tmp) / sizeof(tmp[0])) / (unsigned)p->channels;
        if (want > cap) want = cap;
        n = ogg_take(o, tmp, want);
        if (n == 0) break;
        to_short(tmp, p->data + (size_t)p->decoded * p->channels, n * (unsigned)p->channels);
        p->decoded += n;
    }
    if (p->decoded < upto && o->ended) {   /* what the decoder didn't give is silence */
        memset(p->data + (size_t)p->decoded * p->channels, 0, (size_t)(p->frames - p->decoded) * p->channels * sizeof(short));
        p->decoded = p->frames;
    }
    ogg_unlock((U32)p->h);
    if (p->decoded >= p->frames) {
        int h = p->h;
        p->h = -1;
        oggs[h].pcm_owner = -1;
        if (oggs[h].close_asked) ogg_close_now((U32)h);
        pcm_pending--;
    }
}

static void pcm_finish_one(void) {
    unsigned id;
    for (id = 0; id < PCM_MAX; id++)
        if (pcms[id].h >= 0 && pcms[id].data != NULL) {
            pcm_more((int)id, pcms[id].frames);
            return;
        }
}
static float mix_block[MIX_MAX_FRAMES * 2];
static unsigned mix_frames;

/* ---- the mix carried on through a late frame ----
 * The game's thread mixes each frame's samples a little ahead of the output (ConsoleHost.AudioLead,
 * 15 ms): a frame later than that ran the output dry, a gap (the twenty-ninth run: the sound crackled at
 * every slow frame, and the loading screen's crackle paused through hold-ups). Each mix's voices are kept
 * as it left them (clip, position, step, gains), and when the output runs low before the next mix the
 * host's audio thread mixes them on from there (ch360_mix_extend), as Unity's own audio thread goes on
 * through the game's slow frames. The next mix starts each voice carried on where it got to, and the
 * voices it starts at once; mix_begin returns the frames carried on, which the engine counts as played. */
enum { CONT_MAX = 128 };
typedef struct mix_cont {
    U32 key, kind, id, loop, total, channels, f0, istep, ended;
    float t, fstep, gl, gr;
} mix_cont;
static mix_cont conts[CONT_MAX], conts_next[CONT_MAX];
static unsigned cont_count, cont_next_count, cont_frames;   /* voices kept; frames carried on since the last mix */
static unsigned long long carried_frames;                   /* all the frames carried on (the frame lines) */
/* The mixer's state, one thread at a time: 0 free, 1 the game's thread (a mix from mix_begin to mix_end, a
 * clip opened or let go), 2 the audio thread carrying the mix on (it never waits: the game's block comes). */
static volatile int mix_owner;
static int game_depth, mix_open;   /* the game's thread's hold on it; a mix begun and not ended */

static void game_lock(void) {
    if (game_depth++ == 0)
        while (!__sync_bool_compare_and_swap(&mix_owner, 0, 1)) {
        }
}

static void game_unlock(void) {
    if (game_depth > 0 && --game_depth == 0) __sync_lock_release(&mix_owner);
}

/* A clip let go: the voices carried on with it stop. */
static void conts_forget(U32 kind, U32 id) {
    unsigned k;
    for (k = 0; k < cont_count; k++)
        if (conts[k].kind == kind && conts[k].id == id) conts[k].ended = 1;
}

/* pcm_decode(handle, frames_out): a clip just opened, decoded whole into host
 * memory: an id for mix_voice (frames_out its frames), -1 without memory. The
 * handle stays open (ogg_close it). */
U32 i5_ch36010_pcmX5Fdecode(wasmModuleInstance* inst, U32 h, U32 out) {
    ch360_ogg* o;
    unsigned length, got = 0, n;
    short* data;
    int id;
    (void)inst;
    if (h < OGG_MAX) wait_loaded(h);
    if (h >= OGG_MAX || oggs[h].v == NULL || !srt_mem_ok(out, 4)) return (U32)-1;
    game_lock();
    for (id = 0; id < PCM_MAX && pcms[id].data != NULL; id++) {
    }
    o = &oggs[h];
    length = id == PCM_MAX ? 0 : stb_vorbis_stream_length_in_samples(o->v);
    data = id == PCM_MAX || length > (1u << 26) / (unsigned)o->channels ? NULL
                                                                        : (short*)malloc((size_t)(length ? length : 1) * o->channels * sizeof(short));
    if (data == NULL) {
        game_unlock();
        return (U32)-1;
    }
    (void)got;
    (void)n;
    pcms[id].data = data;
    pcms[id].frames = length;
    pcms[id].channels = o->channels;
    pcm_live += (size_t)(length ? length : 1) * o->channels * sizeof(short);
    if (pcm_live > pcm_peak) pcm_peak = pcm_live;
    pcms[id].decoded = 0;
    pcms[id].h = (int)h;
    o->pcm_owner = id;
    pcm_pending++;
    /* its first frames now (all of a short clip), the rest as it plays (pcm_more) */
    pcm_more(id, ch360_pcm_whole || length <= 2 * PCM_FIRST_FRAMES ? length : PCM_FIRST_FRAMES);
    game_unlock();
    put_u32(srt_mem_ptr(out), length);
    return (U32)id;
}

/* pcm_free(id) */
void i5_ch3608_pcmX5Ffree(wasmModuleInstance* inst, U32 id) {
    (void)inst;
    if (id >= PCM_MAX || pcms[id].data == NULL) return;
    game_lock();
    conts_forget(1, id);
    if (pcms[id].h >= 0) {   /* still decoding: its handle let go */
        int h = pcms[id].h;
        pcms[id].h = -1;
        oggs[h].pcm_owner = -1;
        if (oggs[h].close_asked) ogg_close_now((U32)h);
        pcm_pending--;
    }
    pcm_live -= (size_t)(pcms[id].frames ? pcms[id].frames : 1) * pcms[id].channels * sizeof(short);
    free(pcms[id].data);
    pcms[id].data = NULL;
    game_unlock();
}

/* The audio's memory (the frame lines): out[0] decoded clips, [1] their KB (32-bit floats), [2] the most
 * they reached (16-bit samples), [3] KB of open clips' compressed bytes, [4] KB of closed ones kept for their next play,
 * [5] KB of the streams' buffers, [6] open decoders (stb_vorbis's own state not counted). */
void ch360_audio_memory(unsigned out[7]) {
    unsigned k, clips = 0, decoders = 0;
    size_t open = 0, bufs = 0;
    for (k = 0; k < PCM_MAX; k++)
        if (pcms[k].data != NULL) clips++;
    for (k = 0; k < OGG_MAX; k++) {
        if (oggs[k].v == NULL) continue;
        decoders++;
        open += (size_t)oggs[k].size;
        if (oggs[k].ring != NULL) bufs += (size_t)AHEAD_FRAMES * oggs[k].channels * sizeof(float);
        if (oggs[k].sbuf != NULL) bufs += (size_t)STREAM_FRAMES * oggs[k].channels * sizeof(float);
    }
    out[0] = clips;
    out[1] = (unsigned)(pcm_live >> 10);
    out[2] = (unsigned)(pcm_peak >> 10);
    out[3] = (unsigned)(open >> 10);
    out[4] = cache_bytes >> 10;
    out[5] = (unsigned)(bufs >> 10);
    out[6] = decoders;
}

/* mix_begin(frames): the block to mix into, silent; the frames the audio thread carried the last mix on
 * since (its voices go on from there in this one). The mixer is the game's until mix_end. */
U32 i5_ch3609_mixX5Fbegin(wasmModuleInstance* inst, U32 frames) {
    (void)inst;
    if (mix_open) game_unlock();   /* one begun and never ended */
    game_lock();
    mix_open = 1;
    cont_next_count = 0;
    mix_frames = frames > MIX_MAX_FRAMES ? MIX_MAX_FRAMES : frames;
    memset(mix_block, 0, (size_t)mix_frames * 2 * sizeof(float));
    /* the clips still decoding: this mix's share, one clip after another */
    if (pcm_pending > 0) {
        unsigned left = PCM_STEP_FRAMES, k;
        for (k = 0; k < PCM_MAX && left > 0 && pcm_pending > 0; k++) {
            unsigned id = (pcm_next + k) % PCM_MAX, before;
            if (pcms[id].h < 0 || pcms[id].data == NULL) continue;
            before = pcms[id].decoded;
            pcm_more((int)id, before + left);
            left -= pcms[id].decoded - before < left ? pcms[id].decoded - before : left;
            pcm_next = id;
        }
    }
    return cont_frames;
}

/* The stream buffer holds frame f0 and, when the clip has it, the next one (the engine's Fill). */
static int stream_fill(ch360_ogg* o, U32 f0) {
    unsigned ch = (unsigned)o->channels, room, got;
    U32 end = o->sstart + o->sframes;
    if (f0 >= o->sstart && f0 + 1 < end) return 1;
    if (f0 < o->sstart || f0 > end) {
        ogg_seek_locked(o, f0);
        o->sstart = f0;
        o->sframes = 0;
    }
    if (f0 > o->sstart) {   /* frames already played */
        unsigned drop = f0 - o->sstart;
        memmove(o->sbuf, o->sbuf + (size_t)drop * ch, (size_t)(o->sframes - drop) * ch * sizeof(float));
        o->sstart = f0;
        o->sframes -= drop;
    }
    room = STREAM_FRAMES - o->sframes;
    got = room > 0 ? ogg_take(o, o->sbuf + (size_t)o->sframes * ch, room) : 0;
    o->sframes += got;
    return f0 < o->sstart + o->sframes;
}

/* One voice into dst's samples [start, frames) (the mixer held): kind 1 a decoded clip, 2 a streamed
 * clip's handle, 0 silence (it only moves on). The position *pf0 + *pt steps istep + fstep each sample
 * and is left where it ended; *pstepped counts the samples since the start or the last wrap (*pwrapped
 * then 1, at *pwrap_f0 + *pwrap_t). Returns 1 when a clip that doesn't loop ended. */
static U32 mix_one(U32 kind, U32 id, U32 start, U32 frames, U32 loop, U32 total, U32 channels, U32* pf0, float* pt,
                   U32 istep, float fstep, float gl, float gr, float* dst, U32* pstepped, U32* pwrapped, U32* pwrap_f0,
                   float* pwrap_t) {
    const short* pcm = NULL;
    const float k16 = 1.0f / 32767.0f;   /* the decoded clips' samples to the mix's scale */
    ch360_ogg* o = NULL;
    unsigned ch = channels ? channels : 1;
    U32 i, f0 = *pf0, stepped = 0, ended = 0;
    float t = *pt;
    if (kind == 1) {
        if (id < PCM_MAX && pcms[id].data != NULL) {
            /* the frames this block can reach, decoded now if they weren't yet */
            if (pcms[id].h >= 0) {
                unsigned long long reach = (unsigned long long)f0 + (unsigned long long)(frames - (start < frames ? start : frames)) * (istep + 1u) + 2u;
                if (loop || reach >= pcms[id].frames) pcm_more((int)id, pcms[id].frames);
                else pcm_more((int)id, (unsigned)reach);
            }
            pcm = pcms[id].data;
            ch = (unsigned)pcms[id].channels;
            if (total > pcms[id].frames) total = pcms[id].frames;
        }
    } else if (kind == 2) {
        if (id < OGG_MAX && oggs[id].v != NULL) {
            o = &oggs[id];
            ch = (unsigned)o->channels;
            ogg_lock(id);
            if (o->sbuf == NULL) o->sbuf = (float*)malloc((size_t)STREAM_FRAMES * ch * sizeof(float));
            if (o->sbuf == NULL) {
                ogg_unlock(id);
                o = NULL;
            }
        }
    }
    if (total == 0) ended = 1;
    for (i = start; i < frames && !ended; i++) {
        float l, r;
        if (f0 >= total) {
            if (!loop) {
                ended = 1;
                break;
            }
            f0 -= total;
            *pwrapped = 1;
            *pwrap_f0 = f0;
            *pwrap_t = t;
            stepped = 0;
            if (o != NULL) {
                o->sstart = f0;
                o->sframes = 0;
                ogg_seek_locked(o, f0);
            }
        }
        if (pcm != NULL) {
            U32 f1 = f0 + 1 < total ? f0 + 1 : (loop ? 0 : f0);
            if (ch == 1) {
                float a = pcm[f0] * k16, b = pcm[f1] * k16;
                l = r = a + (b - a) * t;
            } else {
                size_t i0 = (size_t)f0 * ch, i1 = (size_t)f1 * ch;
                float l0 = pcm[i0] * k16, l1 = pcm[i1] * k16, r0 = pcm[i0 + 1] * k16, r1 = pcm[i1 + 1] * k16;
                l = l0 + (l1 - l0) * t;
                r = r0 + (r1 - r0) * t;
            }
            dst[i * 2] += l * gl;
            dst[i * 2 + 1] += r * gr;
        } else if (o != NULL && stream_fill(o, f0)) {
            U32 rel1 = f0 + 1 - o->sstart;
            size_t b0 = (size_t)(f0 - o->sstart) * ch, b1;
            if (rel1 > o->sframes - 1) rel1 = o->sframes - 1;
            b1 = (size_t)rel1 * ch;
            if (ch == 1) {
                float a = o->sbuf[b0], b = o->sbuf[b1];
                l = r = a + (b - a) * t;
            } else {
                l = o->sbuf[b0] + (o->sbuf[b1] - o->sbuf[b0]) * t;
                r = o->sbuf[b0 + 1] + (o->sbuf[b1 + 1] - o->sbuf[b0 + 1]) * t;
            }
            dst[i * 2] += l * gl;
            dst[i * 2 + 1] += r * gr;
        }
        f0 += istep;
        t += fstep;
        if (t >= 1.0f) {
            t -= 1.0f;
            f0++;
        }
        stepped++;
    }
    if (o != NULL) {
        ogg_unlock(id);
        if (o->ring != NULL) ahead_wake();
    }
    *pf0 = f0;
    *pt = t;
    *pstepped = stepped;
    return ended;
}

/* mix_voice(key, kind, id, start, frames, loop, total, channels, f0, t, istep, fstep, gl, gr, out): one
 * voice (key: the engine's, the same each mix while it plays) into the block's samples [start, frames)
 * (mix_one). A voice the audio thread carried on goes on from where it got to. out: f0, t, the samples
 * stepped since the start or the last wrap, 1 if it wrapped, and the position at the wrap (f0, t); a
 * voice carried on is given as wrapped at its new position as the block starts. */
U32 i5_ch3609_mixX5Fvoice(wasmModuleInstance* inst, U32 key, U32 kind, U32 id, U32 start, U32 frames, U32 loop, U32 total,
                          U32 channels, U32 f0, F32 t, U32 istep, F32 fstep, F32 gl, F32 gr, U32 out) {
    U32 stepped = 0, wrapped = 0, wrap_f0 = 0, ended = 0, k;
    float wrap_t = 0.0f;
    union { float f; U32 u; } bits;
    U8* res;
    (void)inst;
    if (!srt_mem_ok(out, 24)) return 1;
    if (frames > mix_frames) frames = mix_frames;
    if (kind == 2 && id < OGG_MAX && oggs[id].loading) {   /* still being read: silent, where it was */
        res = srt_mem_ptr(out);
        put_u32(res, f0);
        bits.f = t;
        put_u32(res + 4, bits.u);
        put_u32(res + 8, 0);
        put_u32(res + 12, 0);
        put_u32(res + 16, 0);
        put_u32(res + 20, 0);
        return 0;
    }
    if (cont_frames > 0)
        for (k = 0; k < cont_count; k++)
            if (conts[k].key == key && conts[k].kind == kind && conts[k].id == id) {
                if (conts[k].ended) ended = 1;
                f0 = conts[k].f0;
                t = conts[k].t;
                wrapped = 1;
                wrap_f0 = f0;
                wrap_t = t;
                start = 0;
                break;
            }
    if (!ended)
        ended = mix_one(kind, id, start, frames, loop, total, channels, &f0, &t, istep, fstep, gl, gr, mix_block, &stepped,
                        &wrapped, &wrap_f0, &wrap_t);
    if (!ended && cont_next_count < CONT_MAX) {
        mix_cont* c = &conts_next[cont_next_count++];
        c->key = key;
        c->kind = kind;
        c->id = id;
        c->loop = loop;
        c->total = total;
        c->channels = channels;
        c->f0 = f0;
        c->t = t;
        c->istep = istep;
        c->fstep = fstep;
        c->gl = gl;
        c->gr = gr;
        c->ended = 0;
    }
    res = srt_mem_ptr(out);
    put_u32(res, f0);
    bits.f = t;
    put_u32(res + 4, bits.u);
    put_u32(res + 8, stepped);
    put_u32(res + 12, wrapped);
    put_u32(res + 16, wrap_f0);
    bits.f = wrap_t;
    put_u32(res + 20, bits.u);
    return ended;
}

/* mix_end(frames): the block to the platform; its voices are what the audio thread carries on. */
void i5_ch3607_mixX5Fend(wasmModuleInstance* inst, U32 frames) {
    (void)inst;
    if (frames > mix_frames) frames = mix_frames;
    if (frames > 0) ch360_plat_audio_native(mix_block, (int)frames);
    memcpy(conts, conts_next, cont_next_count * sizeof(mix_cont));
    cont_count = cont_next_count;
    cont_frames = 0;
    if (mix_open) {
        mix_open = 0;
        game_unlock();
    }
}

/* The audio thread, its output running low: the last mix's voices on for frames more (at most
 * MIX_MAX_FRAMES), straight to the output; the frames, or 0 while the game's thread holds the mixer (its
 * block is coming) or nothing plays. */
int ch360_mix_extend(int frames) {
    static float block[MIX_MAX_FRAMES * 2];
    unsigned k, live = 0;
    if (frames <= 0 || frames > MIX_MAX_FRAMES || !__sync_bool_compare_and_swap(&mix_owner, 0, 2)) return 0;
    memset(block, 0, (size_t)frames * 2 * sizeof(float));
    for (k = 0; k < cont_count; k++) {
        mix_cont* c = &conts[k];
        U32 stepped = 0, wrapped = 0, wrap_f0 = 0;
        float wrap_t = 0.0f;
        if (c->ended) continue;
        /* a clip let go or still being read is not mixed (conts_forget marks the first) */
        if ((c->kind == 1 && (c->id >= PCM_MAX || pcms[c->id].data == NULL)) ||
            (c->kind == 2 && (c->id >= OGG_MAX || oggs[c->id].v == NULL || oggs[c->id].loading))) {
            c->ended = 1;
            continue;
        }
        c->ended = mix_one(c->kind, c->id, 0, (U32)frames, c->loop, c->total, c->channels, &c->f0, &c->t, c->istep, c->fstep,
                           c->gl, c->gr, block, &stepped, &wrapped, &wrap_f0, &wrap_t);
        live++;
    }
    if (live > 0) {
        ch360_plat_audio_native(block, frames);
        cont_frames += (unsigned)frames;
        carried_frames += (unsigned)frames;
    }
    __sync_lock_release(&mix_owner);
    return live > 0 ? frames : 0;
}

/* All the frames the audio thread has carried on (the frame lines). */
unsigned long long ch360_mix_carried(void) { return carried_frames; }
