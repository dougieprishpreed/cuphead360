#!/usr/bin/env python3
"""Stage the console build: the XEX and the converted data in one folder.

    stage_console.py --to <folder> [--data work/GameData] [--xex work/console/xsdk/default.xex] [--dry-run]
                     [--workers 2] [--packs <folder>] [--no-xex | --build-only]

--build-only copies only your build into <folder>: default.xex, GameData/PLAYER.DAT and licenses/ (and makes save/),
for a folder whose game data 360convert.exe (tools/convert360.py) made. --no-xex stages the data without them.

Writes <folder>/default.xex, <folder>/GameData/PLAYER.DAT (the module's data
segments, read at start: from the same build as the XEX), <folder>/GameData/{objects,fonts}, the packs
<folder>/GameData/CTEX0.PAK... and AUDIO0.PAK..., and <folder>/licenses/,
the layout console/xsdk/main.cpp reads from game:\\.

The packs (console/ch360_pack.c reads them) hold the converted textures
(<data>/ctex, convert_console.py), the full-size sprite pages and their
table (<data>/ctexc, cluster_console.py) and the clips (<data>/audio,
convert_audio.py), each file under 2 GB (xsdk's file offsets are 32-bit
signed: a read past 2 GB fails on the console). On the console's USB drive
every file opened cost ~110 ms whatever its size: the floor under each
texture load and most of a fight's file time. Textures are LZ4 (DXT pages
shrink about 4x, so they also read about 4x faster); clips are Vorbis
already and stay as they are. Packs are built into work/console/packs, and
again only when what they hold changes. The old layouts' staged ctex/ and
audio/ folders, and packs no longer written, are removed from <folder>.

Copy <folder> to the console (USB drive or FTP) and start default.xex.
Files already there with the same size and time are skipped, so staging
again after a rebuild copies what changed. The PC's own Arial (Windows'
font folder) goes into GameData/fonts: the game falls back to it for
characters its fonts lack, as Unity does on Windows.

Everything staged comes from the player's own game files: keep it to the
player's own console.
"""
import argparse
import multiprocessing
import os
import shutil
import struct
import sys

import bundle_names

# CUPHEAD360_ROOT: where console/artscale.txt and console/learned are (360convert.exe sets it to its own files).
ROOT = os.environ.get("CUPHEAD360_ROOT") or os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PACKS = os.path.join(ROOT, "work", "console", "packs")   # --packs sets another
PACK_CHUNK = 256 * 1024     # console/ch360_pack.c, PACK_CHUNK
# Bytes a pack file holds at most. xsdk's lseek takes a 32-bit off_t, so a
# console read past 2 GB fails (the eleventh run: every texture past it drew
# black, and the sprite table there never loaded).
PACK_LIMIT = 2000 << 20
LZ4_LEVEL = 9               # 4.65x on a DXT5 page at 53 MB/s (level 12: 4.68x at 5 MB/s)


def fnv64(key):
    """FNV-1a 64 of a key (console/ch360_pack.c, ch360_fnv64)."""
    h = 0xcbf29ce484222325
    for c in key.encode("utf-8"):
        h = ((h ^ c) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h


def below_normal():
    if os.name == "nt":
        import ctypes
        k32 = ctypes.windll.kernel32
        k32.SetPriorityClass(k32.GetCurrentProcess(), 0x4000)   # BELOW_NORMAL_PRIORITY_CLASS


def pack_bytes(job):
    """An entry as the pack stores it: LZ4 chunks (count, sizes, chunks), or its bytes as they are."""
    path, compress = job
    with open(path, "rb") as f:
        data = f.read()
    if not compress or not data:
        return data, len(data)
    import lz4.block
    chunks = [lz4.block.compress(data[i:i + PACK_CHUNK], mode="high_compression", compression=LZ4_LEVEL, store_size=False)
              for i in range(0, len(data), PACK_CHUNK)]
    out = struct.pack("<I", len(chunks)) + b"".join(struct.pack("<I", len(c)) for c in chunks) + b"".join(chunks)
    if len(out) >= len(data) * 0.95:
        return data, len(data)
    return out, len(data)


def write_packs(name, entries, workers):
    """<PACKS>/<name>0.PAK, 1, ... for entries [(key, path, compress)], unless its manifest is unchanged.
    Returns the pack files' names."""
    os.makedirs(PACKS, exist_ok=True)
    entries = sorted(entries, key=lambda e: fnv64(e[0]))
    hashes = [fnv64(k) for k, _, _ in entries]
    if len(set(hashes)) != len(hashes):
        sys.exit("two %s entries share a key hash" % name)
    manifest = "layout %d %d %d\n" % (PACK_LIMIT, PACK_CHUNK, LZ4_LEVEL)
    manifest += "".join("%s %d %d %d\n" % (k, os.path.getsize(p), int(os.path.getmtime(p)), c) for k, p, c in entries)
    mpath = os.path.join(PACKS, name + ".manifest")
    old = open(mpath, encoding="utf-8").read() if os.path.exists(mpath) else None
    have = sorted(f for f in os.listdir(PACKS) if f.startswith(name) and f.endswith(".PAK"))
    if old == manifest and have:
        print("pack %s: unchanged (%d files)" % (name, len(have)), flush=True)
        return have
    for f in have:
        os.remove(os.path.join(PACKS, f))
    if os.path.exists(mpath):
        os.remove(mpath)
    index_bytes = (16 + 24 * len(entries) + 511) // 512 * 512
    files, rows = [], []
    cur, at = None, 0
    raw = stored_total = 0

    def finish(f, rows):
        f.seek(0)
        f.write(b"CHPK" + struct.pack("<3I", 1, len(rows), 0))
        for h, off, st, sz in rows:
            f.write(struct.pack("<6I", h >> 32, h & 0xFFFFFFFF, off, st, sz, 0))
        f.close()

    pool = multiprocessing.Pool(workers, initializer=below_normal) if workers > 1 else None
    jobs = [(p, c) for _, p, c in entries]
    results = pool.imap(pack_bytes, jobs, chunksize=4) if pool else map(pack_bytes, jobs)
    for i, ((key, path, comp), (data, size)) in enumerate(zip(entries, results)):
        if cur is None or at + len(data) > PACK_LIMIT:
            if cur is not None:
                finish(cur, rows)
            fn = "%s%d.PAK" % (name, len(files))
            files.append(fn)
            cur = open(os.path.join(PACKS, fn + ".tmp"), "wb")
            cur.write(b"\0" * index_bytes)
            rows, at = [], index_bytes
        if at + len(data) > PACK_LIMIT:
            sys.exit("%s: %d bytes don't fit a pack file" % (key, len(data)))
        cur.seek(at)
        cur.write(data)
        rows.append((hashes[i], at, len(data), size))
        at = (at + len(data) + 511) // 512 * 512
        raw += size
        stored_total += len(data)
        if (i + 1) % 1000 == 0:
            print("  pack %s: %d/%d entries" % (name, i + 1, len(entries)), flush=True)
    if pool:
        pool.close()
    if cur is not None:
        finish(cur, rows)
    for fn in files:
        os.replace(os.path.join(PACKS, fn + ".tmp"), os.path.join(PACKS, fn))
    with open(mpath, "w", encoding="utf-8") as f:
        f.write(manifest)
    print("pack %s: %d entries, %.2f GB stored of %.2f GB, %d files" % (name, len(entries), stored_total / 2**30, raw / 2**30,
                                                                        len(files)), flush=True)
    return files


def ogg_info(path):
    """(channels, rate, frames) of an Ogg Vorbis file: its identification header's channels and rate, and the
    granule position of its last page (what stb_vorbis_stream_length_in_samples reads), or None."""
    size = os.path.getsize(path)
    with open(path, "rb") as f:
        head = f.read(512)
        f.seek(max(0, size - 65536))
        tail = f.read()
    if head[:4] != b"OggS":
        return None
    body = 27 + head[26]
    if head[body:body + 7] != b"\x01vorbis":
        return None
    channels, rate = head[body + 11], struct.unpack_from("<I", head, body + 12)[0]
    frames, i = None, tail.rfind(b"OggS")
    while i >= 0:
        if i + 14 <= len(tail) and tail[i + 4] == 0:
            g = struct.unpack_from("<q", tail, i + 6)[0]
            if g >= 0:
                frames = g
                break
        i = tail.rfind(b"OggS", 0, i)
    return (channels, rate, frames) if frames is not None and frames < 2 ** 32 else None


def write_audio_info(entries):
    """<PACKS>/AUDIOINFO.BIN: each clip's channels, rate and frames by key hash ("CHAI", count, then hi, lo,
    channels, rate, frames, sorted): the console opens a large clip without reading it first (ch360_ogg.c)."""
    rows = []
    for key, path, _ in entries:
        info = ogg_info(path)
        if info:
            h = fnv64(key)
            rows.append((h >> 32, h & 0xFFFFFFFF) + info)
    rows.sort()
    data = b"CHAI" + struct.pack("<I", len(rows)) + b"".join(struct.pack("<5I", *r) for r in rows)
    out = os.path.join(PACKS, "AUDIOINFO.BIN")
    if not (os.path.exists(out) and open(out, "rb").read() == data):
        with open(out, "wb") as f:
            f.write(data)
    print("audio info: %d of %d clips" % (len(rows), len(entries)), flush=True)
    return "AUDIOINFO.BIN"


def art_scales(path=None):
    """console/artscale.txt: ({atlas: percent} shipped smaller, {atlas: [percents]} copies to try), "#" notes."""
    shipped, tried = {}, {}
    path = path or os.path.join(ROOT, "console", "artscale.txt")
    if not os.path.exists(path):
        return shipped, tried
    for ln in open(path, encoding="utf-8"):
        w = ln.split("#", 1)[0].split()
        if len(w) == 2 and w[1].isdigit() and 10 <= int(w[1]) < 100:
            shipped[w[0]] = int(w[1])
        elif len(w) == 3 and w[0] == "try":
            tried[w[1]] = [int(x) for x in w[2].split(",") if x.isdigit() and 10 <= int(x) < 100]
        elif w:
            sys.exit("%s: can't read the line %r" % (path, ln.rstrip()))
    return shipped, tried


def atlas_of(key, names):
    """The name (of names) whose atlas the page key belongs to: "atlas_<name>_" in it; the longest such name, so a line
    for one atlas ("oldman_phase1") overrides the line for its group ("oldman")."""
    found = [n for n in names if "atlas_%s_" % n in key]
    return max(found, key=len) if found else None


def sprite_pages(data, shipped, tried):
    """The sprite pages' entries: each page full size from <data>/ctexc, a shipped atlas's from its smaller copy in
    <data>/ctexs under the full page's name instead, and the tried atlases' copies under "<key>_s<percent>"."""
    out, missing = [], []
    counts = dict((n, 0) for n in shipped)
    scaled = os.path.join(data, "ctexs")
    for key, path, comp in entries_of(os.path.join(data, "ctexc"), ".ctx", True):
        n = atlas_of(key, shipped)
        if n:
            small = os.path.join(scaled, "%s_s%d.ctx" % (key, shipped[n]))
            if os.path.exists(small) and os.path.getmtime(small) >= os.path.getmtime(path):
                out.append((key, small, comp))
                counts[n] += 1
                continue
            missing.append(key)
        out.append((key, path, comp))
    if missing:
        sys.exit("%d pages of shipped-smaller atlases have no up-to-date copy (%s...): run tools/scale_pages.py" % (
            len(missing), missing[0]))
    for key, path, comp in entries_of(scaled, ".ctx", True):
        base, _, pct = key.rpartition("_s")
        n = atlas_of(base, tried)
        if n and pct.isdigit() and int(pct) in tried[n]:
            out.append((key, path, comp))
    for n, c in sorted(counts.items()):
        print("artscale: %s shipped at %d%%: %d sprite pages" % (n, shipped[n], c), flush=True)
    return out


def entries_of(folder, ext, compress):
    out = []
    if os.path.isdir(folder):
        for f in sorted(os.listdir(folder)):
            if f.endswith(ext):
                out.append((f[:-len(ext)], os.path.join(folder, f), compress))
    return out


def plan(src, exts):
    """(source path, path under the staged folder) for each file."""
    out = []
    for dirpath, _, files in os.walk(src):
        rel = os.path.relpath(dirpath, src)
        for f in sorted(files):
            if exts and not f.endswith(exts):
                continue
            out.append((os.path.join(dirpath, f), os.path.normpath(os.path.join(rel, f))))
    return out


def copy_tree(files, dst, dry, stats):
    for s, rel in files:
        d = os.path.join(dst, rel)
        st = os.stat(s)
        if st.st_size >= 4 << 30:
            sys.exit("%s is 4 GB or more: FAT32 can't hold it" % s)
        stats["files"] += 1
        stats["bytes"] += st.st_size
        if os.path.exists(d):
            dt = os.stat(d)
            if dt.st_size == st.st_size and int(dt.st_mtime) >= int(st.st_mtime):
                continue
        stats["copied"] += 1
        if not dry:
            os.makedirs(os.path.dirname(d), exist_ok=True)
            shutil.copy2(s, d)


def copy_build(xex, to):
    """default.xex, the module's data segments (GameData/PLAYER.DAT, the XEX's own build's: build.sh copies them
    beside it), the licenses and the save folder."""
    pd = os.path.join(os.path.dirname(xex), "PLAYER.DAT")
    if not os.path.exists(pd):
        sys.exit("no PLAYER.DAT beside %s (console/xsdk/build.sh copies it from the C stage)" % xex)
    gd = os.path.join(to, "GameData")
    os.makedirs(gd, exist_ok=True)
    shutil.copy2(xex, os.path.join(to, "default.xex"))
    shutil.copy2(pd, os.path.join(gd, "PLAYER.DAT"))
    lic = os.path.join(os.path.dirname(xex), "licenses")
    if os.path.isdir(lic):
        shutil.copytree(lic, os.path.join(to, "licenses"), dirs_exist_ok=True)
    os.makedirs(os.path.join(to, "save"), exist_ok=True)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--to", required=True)
    ap.add_argument("--data", default=os.path.join(ROOT, "work", "GameData"))
    ap.add_argument("--xex", default=os.path.join(ROOT, "work", "console", "xsdk", "default.xex"))
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--workers", type=int, default=2, help="processes compressing textures (below-normal priority)")
    ap.add_argument("--no-xex", action="store_true", help="the data only (a PC check of the packs; 360convert.exe)")
    ap.add_argument("--build-only", action="store_true",
                    help="only the build: default.xex, GameData/PLAYER.DAT and licenses/ (the data is 360convert.exe's)")
    ap.add_argument("--packs", help="where the packs are built (default work/console/packs)")
    a = ap.parse_args(argv)
    global PACKS
    if a.packs:
        PACKS = os.path.abspath(a.packs)
    if a.build_only:
        if not os.path.exists(a.xex):
            sys.exit("no XEX at %s (tools/build_console.py --stage xsdk)" % a.xex)
        if not a.dry_run:
            copy_build(a.xex, a.to)
        print("stage_console: the build (default.xex, GameData/PLAYER.DAT, licenses) -> %s" % a.to)
        return 0
    for need in ("objects", "ctex", "audio"):
        if not os.path.isdir(os.path.join(a.data, need)):
            sys.exit("no %s in %s (convert_objects.py, convert_console.py, convert_audio.py)" % (need, a.data))
    # Data converted before bundle folders were kept to 42 characters (the console's hard drive): renamed in place,
    # with the keys made from those names (packed again below) and the read-ahead lists that name them.
    moved = bundle_names.migrate(a.data, (os.path.join(ROOT, "work", "console", "learned"),
                                          os.path.join(ROOT, "console", "learned"), os.path.join(a.to, "learned")), a.dry_run)
    if moved:
        print("long bundle names shortened: %d folders, files and lists" % moved, flush=True)
    if not a.no_xex and not os.path.exists(a.xex):
        sys.exit("no XEX at %s (tools/build_console.py --stage xsdk)" % a.xex)
    stats = {"files": 0, "bytes": 0, "copied": 0}
    gd = os.path.join(a.to, "GameData")
    # The sprite pages: the levels console/artscale.txt ships smaller only at that size (tools/scale_pages.py's copies
    # under the full pages' names), the copies it lists to try beside the full pages ("<key>_s<percent>").
    shipped, tried = art_scales()
    ctex = entries_of(os.path.join(a.data, "ctex"), ".ctx", True) + sprite_pages(a.data, shipped, tried)
    if os.path.exists(os.path.join(a.data, "ctexc", "ctexmap.bin")):
        ctex.append(("ctexmap", os.path.join(a.data, "ctexc", "ctexmap.bin"), True))
    audio = entries_of(os.path.join(a.data, "audio"), ".ogg", False)
    packs = write_packs("CTEX", ctex, a.workers) + write_packs("AUDIO", audio, 1)
    plans = {"objects": plan(os.path.join(a.data, "objects"), (".chob", ".bin")),
             "": [(os.path.join(PACKS, f), f) for f in packs + [write_audio_info(audio)]]}
    # Each scene's sprite pages in first-draw order (the console's learned read-ahead starts from these; it writes its
    # own beside the XEX): the player's console plays' (console/learned, tools/import_learned.py) where there are any,
    # else the PC's bot plays' (work/console/learned, tools/learned_lists.py).
    lists = {}
    for src in (os.path.join(ROOT, "work", "console", "learned"), os.path.join(ROOT, "console", "learned")):
        if os.path.isdir(src):
            for f in sorted(os.listdir(src)):
                if f.endswith(".txt"):
                    lists[f] = os.path.join(src, f)
    if lists:
        plans["learned"] = sorted((p, f) for f, p in lists.items())
        print("learned lists: %d scenes (%d from the console's plays)" % (
            len(lists), sum(1 for p in lists.values() if os.sep + "work" + os.sep not in p)), flush=True)
    # Which atlases are shipped smaller and which copies there are to try (console/ch360_imports.c, art_scale).
    text = "# Written by tools/stage_console.py from console/artscale.txt. An artscale.txt next to default.xex chooses copies.\n"
    text += "".join("shipped %s %d\n" % (n, p) for n, p in sorted(shipped.items()))
    text += "".join("# to try: %s at %s\n" % (n, ",".join(map(str, p))) for n, p in sorted(tried.items()))
    gen = os.path.join(PACKS, "artscale.txt")
    if not (os.path.exists(gen) and open(gen, encoding="utf-8").read() == text):
        os.makedirs(PACKS, exist_ok=True)
        with open(gen, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    plans[""].append((gen, "artscale.txt"))
    # The old layouts' textures and clips (staged names, loose files), and packs not written this time.
    removed = 0
    for sub in ("ctex", "audio"):
        old = os.path.join(gd, sub)
        if os.path.isdir(old):
            for _, _, files in os.walk(old):
                removed += len(files)
            if not a.dry_run:
                shutil.rmtree(old)
    if os.path.isdir(gd):
        for f in os.listdir(gd):
            if f.endswith(".PAK") and f not in packs:
                removed += 1
                if not a.dry_run:
                    os.remove(os.path.join(gd, f))
    # Converted objects the data no longer has (a bundle's old folder name: bundle_names.py), and their folders.
    keep = set(os.path.normcase(rel) for _, rel in plans["objects"])
    sobj = os.path.join(gd, "objects")
    for dirpath, _, files in os.walk(sobj, topdown=False):
        for f in files:
            if os.path.normcase(os.path.relpath(os.path.join(dirpath, f), sobj)) not in keep:
                removed += 1
                if not a.dry_run:
                    os.remove(os.path.join(dirpath, f))
        if dirpath != sobj and not a.dry_run and not os.listdir(dirpath):
            os.rmdir(dirpath)
    # The console's internal drive (FATX) takes names of at most 42 characters: a longer one can't be copied there.
    long_names = sorted(set(part for sub, files in plans.items() for _, rel in files
                            for part in os.path.join(sub, rel).split(os.sep) if len(part) > 42))
    if long_names:
        sys.exit("names over 42 characters (the console's drive can't hold them): " + ", ".join(long_names))
    # Room for what isn't there yet (a file replaced frees its old copy's space).
    need = 0
    if not a.no_xex:
        need = os.path.getsize(a.xex) - (os.path.getsize(os.path.join(a.to, "default.xex"))
                                         if os.path.exists(os.path.join(a.to, "default.xex")) else 0)
    for sub, files in plans.items():
        for s, rel in files:
            d = os.path.join(gd, sub, rel)
            need += os.path.getsize(s) - (os.path.getsize(d) if os.path.exists(d) else 0)
    if not a.dry_run:
        os.makedirs(a.to, exist_ok=True)
        free = shutil.disk_usage(a.to).free
        if free < max(need, 0) + (64 << 20):
            sys.exit("%s has %.1f GB free; staging needs %.1f GB more" % (a.to, free / 2**30, need / 2**30))
    for sub, files in plans.items():
        copy_tree(files, os.path.join(gd, sub), a.dry_run, stats)
    arial = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", "arial.ttf")
    if os.path.exists(arial) and not a.dry_run:
        os.makedirs(os.path.join(gd, "fonts"), exist_ok=True)
        shutil.copy2(arial, os.path.join(gd, "fonts", "arial.ttf"))
    if not a.dry_run and not a.no_xex:
        copy_build(a.xex, a.to)
    elif not a.dry_run:
        os.makedirs(os.path.join(a.to, "save"), exist_ok=True)
    print("stage_console: %d files, %.2f GB; %d copied, %d old files removed%s -> %s" % (
        stats["files"], stats["bytes"] / 2**30, stats["copied"], removed, " (dry run)" if a.dry_run else "", a.to))
    return 0


if __name__ == "__main__":
    sys.exit(main())
