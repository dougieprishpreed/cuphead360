/* GameData packs (tools/stage_console.py): the console's converted textures
 * and clips in a few big files, opened once. On the console's USB drive each
 * file opened cost ~110 ms whatever its size (the FAT32 driver walks the
 * path's six folders), which was the floor under every texture load and most
 * of the ~45 s of file time in a fight of the tenth run (clips opened as
 * they first sounded, streamed ones at every play).
 *
 * A pack is <name>0.PAK, <name>1.PAK, ... (each under 2 GB, see read_at):
 *   'CHPK' u32 version (1) u32 count u32 0
 *   count entries, sorted by key hash: u32 hash_hi hash_lo offset stored size 0
 *   the entries' bytes (offsets from the file's start)
 * The hash is FNV-1a 64 of the entry's key (ch360_fnv64). An entry is its
 * bytes as they are (stored == size) or LZ4 blocks: u32 chunk count, each
 * chunk's stored size (u32), then the chunks, each PACK_CHUNK bytes of the
 * entry (the last one less). DXT pages shrink about 4x, so a page also
 * reads ~4x faster; LZ4 decodes far faster than the drive reads.
 *
 * Used from the guest thread only (the texture loader in ch360_plat_draw and
 * the clip opens both run there). C89, like ch360_imports.c. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "srt_plat.h"
#include "ch360_host.h"

#define PACK_CHUNK (256u * 1024u)
#define PACK_FILES 16
#define PACK_SETS 4
#define PACK_IN (1024u * 1024u)

typedef struct pack_file {
    srt_file* f;
    unsigned count;
    unsigned* entries; /* 6 words each, host order */
} pack_file;

static pack_file files[PACK_FILES];
static int file_count;
static int set_first[PACK_SETS], set_files[PACK_SETS];
static int set_count;

struct ch360_pack_reader {
    unsigned char* in_buf;       /* stored chunks, PACK_IN bytes */
    unsigned char* chunk_buf;    /* the last chunk decoded whole */
    int chunk_file;
    unsigned chunk_entry, chunk_index;
    unsigned* table;             /* the last entry's chunk sizes, then their starts */
    unsigned table_cap, table_chunks;
    int table_file;
    unsigned table_entry;
};

static ch360_pack_reader default_reader = { NULL, NULL, -1, 0, 0, NULL, 0, 0, -1, 0 };

/* A reader for another thread, with everything allocated here: it never calls
 * malloc while reading (the table holds an entry of up to 2 GB in 256 KB chunks). */
ch360_pack_reader* ch360_pack_reader_new(void) {
    ch360_pack_reader* r = (ch360_pack_reader*)calloc(1, sizeof(ch360_pack_reader));
    if (r == NULL) return NULL;
    r->chunk_file = -1;
    r->table_file = -1;
    r->in_buf = (unsigned char*)malloc(PACK_IN);
    r->chunk_buf = (unsigned char*)malloc(PACK_CHUNK);
    r->table_cap = 2 * 8192;
    r->table = (unsigned*)malloc(r->table_cap * sizeof(unsigned));
    if (r->in_buf == NULL || r->chunk_buf == NULL || r->table == NULL) {
        free(r->in_buf); free(r->chunk_buf); free(r->table); free(r);
        return NULL;
    }
    return r;
}

static unsigned rd32(const unsigned char* p) {
    return (unsigned)p[0] | ((unsigned)p[1] << 8) | ((unsigned)p[2] << 16) | ((unsigned)p[3] << 24);
}

/* Reads n bytes at offset. Nothing past 2 GB, on every platform: xsdk's lseek
 * takes a 32-bit signed off_t, so the console can't read there (the eleventh
 * run's textures past 2 GB drew black), and the PC check should fail alike;
 * stage_console.py keeps each pack file under it. */
static int read_at(srt_file* f, unsigned offset, void* dst, unsigned n) {
    srt_u64 at;
    size_t done = 0, got;
    if (offset > 0x7fffffffu || n > 0x7fffffffu - offset) return -1;
    if (srt_plat_seek(f, (srt_i64)offset, SRT_SEEK_SET, &at) != 0) return -1;
    while (done < n) {
        if (srt_plat_read(f, (unsigned char*)dst + done, n - done, &got) != 0 || got == 0) return -1;
        done += got;
    }
    return 0;
}

void ch360_fnv64(const char* key, size_t len, unsigned* hi_out, unsigned* lo_out) {
    unsigned hi = 0xcbf29ce4u, lo = 0x84222325u; /* FNV-1a 64 as two words (C89) */
    size_t i;
    for (i = 0; i < len; i++) {
        unsigned a, b, c;
        lo ^= (unsigned char)key[i];
        /* times 0x100000001b3: lo * 0x1b3 with carry, plus lo << 40 */
        a = (lo & 0xffffu) * 0x1b3u;
        b = (lo >> 16) * 0x1b3u + (a >> 16);
        c = hi * 0x1b3u + (b >> 16);
        hi = c + (lo << 8);
        lo = (a & 0xffffu) | (b << 16);
    }
    *hi_out = hi;
    *lo_out = lo;
}

int ch360_pack_open(const char* root, const char* name) {
    int first = file_count, k;
    if (set_count == PACK_SETS) return -1;
    for (k = 0; k < 10 && file_count < PACK_FILES; k++) {
        char path[1024];
        unsigned char head[16];
        pack_file* p = &files[file_count];
        unsigned char* raw;
        unsigned i;
        size_t rl = strlen(root);
        if (rl > 0 && root[rl - 1] == '/') rl--;
        if (rl + strlen(name) + 24 > sizeof(path)) break;
        sprintf(path, "%.*s/GameData/%s%d.PAK", (int)rl, root, name, k);
        if (srt_plat_open(path, SRT_OPEN_READ, &p->f) != 0) break;
        if (read_at(p->f, 0, head, 16) != 0 || rd32(head) != 0x4b504843u || rd32(head + 4) != 1) {
            srt_plat_close(p->f);
            break;
        }
        p->count = rd32(head + 8);
        raw = (unsigned char*)malloc((size_t)p->count * 24 + 1);
        p->entries = (unsigned*)malloc((size_t)p->count * 24 + 1);
        if (raw == NULL || p->entries == NULL || read_at(p->f, 16, raw, p->count * 24) != 0) {
            free(raw);
            free(p->entries);
            srt_plat_close(p->f);
            break;
        }
        for (i = 0; i < p->count * 6; i++) p->entries[i] = rd32(raw + 4 * i);
        free(raw);
        file_count++;
    }
    if (file_count == first) return -1;
    set_first[set_count] = first;
    set_files[set_count] = file_count - first;
    return set_count++;
}

int ch360_pack_find(int pack, const char* key, size_t len, ch360_pack_entry* out) {
    unsigned hi, lo;
    int k;
    if (pack < 0 || pack >= set_count) return 0;
    ch360_fnv64(key, len, &hi, &lo);
    for (k = set_first[pack]; k < set_first[pack] + set_files[pack]; k++) {
        const pack_file* p = &files[k];
        unsigned a = 0, b = p->count;
        while (a < b) {
            unsigned m = a + (b - a) / 2;
            const unsigned* e = p->entries + 6 * m;
            if (e[0] < hi || (e[0] == hi && e[1] < lo)) a = m + 1;
            else b = m;
        }
        if (a < p->count && p->entries[6 * a] == hi && p->entries[6 * a + 1] == lo) {
            const unsigned* e = p->entries + 6 * a;
            out->file = k;
            out->offset = e[2];
            out->stored = e[3];
            out->size = e[4];
            return 1;
        }
    }
    return 0;
}

/* An LZ4 block (the format's own rules: literals, then a 2-byte offset and a
 * match of 4 or more; the last sequence is literals only), bounds-checked.
 * Returns the bytes written, or -1 for bad data. */
int ch360_lz4_decode(const unsigned char* src, unsigned srclen, unsigned char* dst, unsigned dstlen) {
    const unsigned char* ip = src;
    const unsigned char* iend = src + srclen;
    unsigned char* op = dst;
    unsigned char* oend = dst + dstlen;
    while (ip < iend) {
        unsigned token = *ip++, lit = token >> 4, ml, off, b;
        if (lit == 15)
            do {
                if (ip >= iend) return -1;
                b = *ip++;
                lit += b;
            } while (b == 255);
        if ((unsigned)(iend - ip) < lit || (unsigned)(oend - op) < lit) return -1;
        memcpy(op, ip, lit);
        op += lit;
        ip += lit;
        if (ip == iend) break;
        if (iend - ip < 2) return -1;
        off = (unsigned)ip[0] | ((unsigned)ip[1] << 8);
        ip += 2;
        if (off == 0 || off > (unsigned)(op - dst)) return -1;
        ml = token & 15;
        if (ml == 15)
            do {
                if (ip >= iend) return -1;
                b = *ip++;
                ml += b;
            } while (b == 255);
        ml += 4;
        if ((unsigned)(oend - op) < ml) return -1;
        if (off >= ml) {
            memcpy(op, op - off, ml);
            op += ml;
        } else {
            const unsigned char* m = op - off;
            while (ml--) *op++ = *m++;
        }
    }
    return (int)(op - dst);
}

/* The entry's chunk table: sizes in table[0..n), starts (from the entry's offset) in table[n..2n). */
static int load_table(ch360_pack_reader* r, const ch360_pack_entry* e) {
    unsigned char head[4];
    unsigned n, i, at;
    unsigned char* raw;
    if (r->table_file == e->file && r->table_entry == e->offset) return 0;
    r->table_file = -1;
    if (read_at(files[e->file].f, e->offset, head, 4) != 0) return -1;
    n = rd32(head);
    if (n == 0 || n != (e->size + PACK_CHUNK - 1) / PACK_CHUNK) return -1;
    if (2 * n > r->table_cap) {
        free(r->table);
        r->table_cap = 2 * n;
        r->table = (unsigned*)malloc(r->table_cap * sizeof(unsigned));
        if (r->table == NULL) {
            r->table_cap = 0;
            return -1;
        }
    }
    /* The chunk sizes read into the table's second half and converted in place: no
     * allocation (a reader on another thread never allocates). */
    raw = (unsigned char*)(r->table + n);
    if (read_at(files[e->file].f, e->offset + 4, raw, 4 * n) != 0) return -1;
    for (i = 0; i < n; i++) r->table[i] = rd32(raw + 4 * i);
    at = 4 + 4 * n;
    for (i = 0; i < n; i++) {
        r->table[n + i] = at;
        at += r->table[i];
    }
    if (at > e->stored) return -1;
    r->table_chunks = n;
    r->table_file = e->file;
    r->table_entry = e->offset;
    return 0;
}

int ch360_pack_read(const ch360_pack_entry* e, unsigned from, void* dst_v, unsigned n) {
    return ch360_pack_read_with(&default_reader, e, from, dst_v, n);
}

int ch360_pack_read_with(ch360_pack_reader* r, const ch360_pack_entry* e, unsigned from, void* dst_v, unsigned n) {
    unsigned char* dst = (unsigned char*)dst_v;
    unsigned k, last;
    unsigned char* in_buf;
    unsigned* table;
    if (e->file < 0 || e->file >= file_count || from > e->size || n > e->size - from) return -1;
    if (n == 0) return 0;
    if (e->stored == e->size) return read_at(files[e->file].f, e->offset + from, dst, n);
    if (r->in_buf == NULL) r->in_buf = (unsigned char*)malloc(PACK_IN);
    if (r->chunk_buf == NULL) r->chunk_buf = (unsigned char*)malloc(PACK_CHUNK);
    if (r->in_buf == NULL || r->chunk_buf == NULL || load_table(r, e) != 0) return -1;
    in_buf = r->in_buf;
    table = r->table;
    k = from / PACK_CHUNK;
    last = (from + n - 1) / PACK_CHUNK;
    while (k <= last) {
        /* read a run of whole stored chunks that fits the buffer (at least one) */
        unsigned run = 1, bytes = table[k], j, pos = 0;
        while (k + run <= last && bytes + table[k + run] <= PACK_IN) bytes += table[k + run++];
        if (bytes > PACK_IN) return -1;
        if (!(r->chunk_file == e->file && r->chunk_entry == e->offset && r->chunk_index == k && run == 1 &&
              !(k * PACK_CHUNK >= from && (k + 1) * PACK_CHUNK <= from + n)))
            if (read_at(files[e->file].f, e->offset + table[r->table_chunks + k], in_buf, bytes) != 0) return -1;
        for (j = 0; j < run; j++, k++) {
            unsigned c0 = k * PACK_CHUNK, len = e->size - c0 < PACK_CHUNK ? e->size - c0 : PACK_CHUNK;
            unsigned lo = from > c0 ? from - c0 : 0, hi = from + n - c0 < len ? from + n - c0 : len;
            if (lo == 0 && hi == len) {
                if (ch360_lz4_decode(in_buf + pos, table[k], dst + (c0 - from), len) != (int)len) return -1;
            } else {
                if (!(r->chunk_file == e->file && r->chunk_entry == e->offset && r->chunk_index == k)) {
                    r->chunk_file = -1;
                    if (ch360_lz4_decode(in_buf + pos, table[k], r->chunk_buf, len) != (int)len) return -1;
                    r->chunk_file = e->file;
                    r->chunk_entry = e->offset;
                    r->chunk_index = k;
                }
                memcpy(dst + (c0 + lo - from), r->chunk_buf + lo, hi - lo);
            }
            pos += table[k];
        }
    }
    return 0;
}
