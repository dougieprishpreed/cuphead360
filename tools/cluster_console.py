#!/usr/bin/env python3
"""Full-size sprite pages for the console: the atlas pages' sprites, cut out
at full size and packed by animation, so a scene loads the sprites it draws
rather than whole 4096 px pages.

    cluster_console.py --data work/GameData [--page 2048] [--report]

The game's atlas pages are 4096 px. At 720p their sprites are drawn at 1 to
1.45 screen pixels a texel, so convert_console.py's pages halved to 2048
(to fit the console's memory: 16 MB a page at full size in DXT5) blurred
them, the Root Pack's most. A scene draws a fraction of its pages: the
potato's fight drew 18 MB of sprites from 208 MB of pages.

Reads the SpriteAtlas objects (<data>/objects, convert_objects.py) and the
full-size pages (<data>/ctex4k: convert_console.py --max-size 4096 --out
<data>/ctex4k --halved-only). Each sprite's textureRect, widened by 2
texels and out to whole 4x4 blocks (a bilinear sample at its edge reads
half a texel beyond it), is copied as DXT blocks: the same bytes the full
page holds, nothing re-encoded. A sprite's animation is its name without
the frame number (veggie_onion_idle_0004 -> veggie_onion_idle); each
atlas's animations, in name order, fill pages of --page px (a bigger
animation takes pages of its own, a sprite wider than a page a page as
wide as it). Writes <data>/ctexc/<atlas>_c<N>.ctx (convert_console.py's
CTX1 format) and <data>/ctexc/ctexmap.bin, which console/ch360_imports.c
(remap_load) reads from the texture pack:

  'CHRM' u32 version (1) pages boxes targets string-bytes
  pages:   u32 hash_hi hash_lo first count   (FNV-1a 64 of the page's key, sorted)
  boxes:   f32 u0 v0 u1 v1 (the sprite's rect and a texel, in the page's uv)
           f32 su ou sv ov (u' = u * su + ou on the target), u32 target
  targets: u32 offset of the target's key in the strings
  strings: the keys, each NUL-terminated

Deterministic. Everything written comes from the player's own game files.
"""
import argparse
import os
import re
import struct
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import chob  # noqa: E402

MARGIN = 2
BIN_FILL = 0.85   # of a page a bin of animations fills before the next one starts


def fnv64(key):
    h = 0xcbf29ce484222325
    for c in key.encode("utf-8"):
        h = ((h ^ c) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h


def base_name(name):
    b = re.sub(r"[_\-\s.]*\d+$", "", name)
    return b or name


def read_ctx(path):
    with open(path, "rb") as f:
        magic, kind, w, h, ow, oh, n = struct.unpack("<4s6I", f.read(28))
        if magic != b"CTX1":
            raise ValueError("not a CTX1 file: " + path)
        data = f.read(n)
    return kind, w, h, data


def file_key(objects, path):
    k = os.path.relpath(path, objects).replace("\\", "/").replace("/", "_")
    return k[:-5] if k.endswith(".chob") else k


def atlases(objects, only=None):
    """Per SpriteAtlas: (its key, [(sprite name, page key, (x, y, w, h))])."""
    out = []
    for dirpath, _, files in os.walk(objects):
        for f in sorted(files):
            if not f.endswith(".chob"):
                continue
            path = os.path.join(dirpath, f)
            if only and only not in path.replace("\\", "/"):
                continue
            with open(path, "rb") as fh:
                if b"SpriteAtlas" not in fh.read():
                    continue
            c = chob.load(path)
            fk = file_key(objects, path)
            for pid, (cid, tn, tree) in sorted(c.objects.items()):
                if tn != "SpriteAtlas":
                    continue
                rdm = {(tuple(k[0].values()), k[1]): v for k, v in tree["m_RenderDataMap"]}
                sprites = []
                for nm, sp in zip(tree["m_PackedSpriteNamesToIndex"], tree["m_PackedSprites"]):
                    so = c.objects.get(sp.path) if sp.file == 0 else None
                    if so is None:
                        continue
                    rk = so[2].get("m_RenderDataKey")
                    rd = rdm.get((tuple(rk[0].values()), rk[1])) if rk else None
                    if rd is None or rd["texture"].file != 0:
                        continue
                    r = rd["textureRect"]
                    sprites.append((nm, fk + "_" + str(rd["texture"].path), (r["x"], r["y"], r["width"], r["height"])))
                out.append((fk + "_" + str(pid), fk, sprites))
    return out


class Page:
    """A target page packed by MaxRects, bottom-left first (the page is cut to the height it uses).
    Free space is the maximal free rectangles (x0, y0, x1, y1); a skyline left ~45% of the bee
    level's pages empty."""

    def __init__(self, width, height):
        self.w, self.h = width, height
        self.free = np.array([[0, 0, width, height]], np.int32)
        self.top = 0
        self.items = []   # (source key, sx, sy, w, h, dx, dy)

    def place(self, w, h):
        f = self.free
        fits = ((f[:, 2] - f[:, 0]) >= w) & ((f[:, 3] - f[:, 1]) >= h)
        if not fits.any():
            return None
        idx = np.nonzero(fits)[0]
        score = (f[idx, 1].astype(np.int64) + h) * 65536 + f[idx, 0]
        x, y = int(f[idx[np.argmin(score)], 0]), int(f[idx[np.argmin(score)], 1])
        r = np.array([x, y, x + w, y + h], np.int32)
        hit = (f[:, 0] < r[2]) & (f[:, 2] > r[0]) & (f[:, 1] < r[3]) & (f[:, 3] > r[1])
        keep, cut = f[~hit], f[hit]
        parts = [keep]
        for side in range(4):
            c = cut.copy()
            if side == 0:
                c[:, 2] = r[0]          # left of it
            elif side == 1:
                c[:, 0] = r[2]          # right of it
            elif side == 2:
                c[:, 3] = r[1]          # below it
            else:
                c[:, 1] = r[3]          # above it
            parts.append(c[(c[:, 2] > c[:, 0]) & (c[:, 3] > c[:, 1])])
        f = np.concatenate(parts)
        # drop free rectangles inside another one
        inside = ((f[:, None, 0] >= f[None, :, 0]) & (f[:, None, 1] >= f[None, :, 1]) &
                  (f[:, None, 2] <= f[None, :, 2]) & (f[:, None, 3] <= f[None, :, 3]))
        np.fill_diagonal(inside, False)
        same = (f[:, None, :] == f[None, :, :]).all(axis=2)
        later_twin = np.triu(same, 1).any(axis=0)
        strictly = inside & ~same
        self.free = f[~(strictly.any(axis=1) | later_twin)]
        self.top = max(self.top, y + h)
        return x, y

    def used(self):
        return self.top


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", required=True)
    ap.add_argument("--page", type=int, default=2048)
    ap.add_argument("--report", action="store_true", help="per atlas sizes")
    ap.add_argument("--only", help="atlases whose object file path holds this (a check; the table then has only those)")
    a = ap.parse_args()
    objects = os.path.join(a.data, "objects")
    full = os.path.join(a.data, "ctex4k")
    out_dir = os.path.join(a.data, "ctexc")
    os.makedirs(out_dir, exist_ok=True)
    t0 = time.time()
    found = atlases(objects, a.only)
    print("cluster_console: %d atlases, %d sprites (%.0f s)" % (len(found), sum(len(s) for _, _, s in found), time.time() - t0), flush=True)

    pages_cache = {}

    def page(key):
        if key not in pages_cache:
            p = os.path.join(full, key + ".ctx")
            pages_cache[key] = read_ctx(p) if os.path.exists(p) else None   # one atlas's pages at a time
        return pages_cache[key]

    remap = {}       # source page key -> [(u0, v0, u1, v1, su, ou, sv, ov, target)]
    targets = []     # target keys
    written = total_src = 0
    sizes = {}
    for akey, fk, sprites in found:
        # what each sprite's page is, at full size (pages not halved stay as they are)
        info = {}
        for nm, pk, r in sprites:
            if pk not in info:
                p = os.path.join(full, pk + ".ctx")
                if os.path.exists(p):
                    with open(p, "rb") as f:
                        magic, kind, w, h, ow, oh, n = struct.unpack("<4s6I", f.read(28))
                    info[pk] = (kind, w, h) if kind in (1, 2) else None
                else:
                    info[pk] = None
        groups = {}
        for nm, pk, (x, y, w, h) in sprites:
            pi = info.get(pk)
            if pi is None:
                continue
            kind, W, H = pi
            x0 = max(0, int(np.floor((x - MARGIN) / 4)) * 4)
            y0 = max(0, int(np.floor((y - MARGIN) / 4)) * 4)
            x1 = min(W, int(np.ceil((x + w + MARGIN) / 4)) * 4)
            y1 = min(H, int(np.ceil((y + h + MARGIN) / 4)) * 4)
            if x1 <= x0 or y1 <= y0:
                continue
            # not clamped to the page: a sprite at its edge has uvs a rounding below 0 or above 1
            match = ((x - 1) / W, (y - 1) / H, (x + w + 1) / W, (y + h + 1) / H)
            groups.setdefault((kind, base_name(nm)), []).append((pk, x0, y0, x1 - x0, y1 - y0, match, W, H))
        if not groups:
            continue
        # Bins: animations in name order (one character's together) up to BIN_FILL of a page, a
        # bigger animation split over bins of its own; each bin then packed tallest first.
        kpages = {1: [], 2: []}
        placed = []   # (kind, page index, page, source key, sx, sy, dx, dy, match, W, H)
        cap = int(a.page * a.page * BIN_FILL)
        for kind in (1, 2):
            bins, cur, cur_area = [], [], 0
            for (k, base) in sorted(g for g in groups if g[0] == kind):
                uniq = {}
                for b in groups[(k, base)]:
                    uniq.setdefault(b[:5], []).append(b)
                boxes = sorted(uniq, key=lambda b: (b[0], b[1], b[2]))
                area = sum(b[3] * b[4] for b in boxes)
                if cur and cur_area + area > cap:
                    bins.append(cur)
                    cur, cur_area = [], 0
                for b in boxes:
                    if cur and cur_area + b[3] * b[4] > cap:
                        bins.append(cur)
                        cur, cur_area = [], 0
                    cur.append((b, uniq[b]))
                    cur_area += b[3] * b[4]
            if cur:
                bins.append(cur)
            plist = kpages[kind]
            queue = list(bins)
            while queue:
                items = sorted(queue.pop(0), key=lambda e: (-e[0][4], -e[0][3], e[0][0], e[0][1], e[0][2]))
                # The page width that wastes least: frames 1024-1536 px wide were half the bee
                # level's, one to a row of a 2048 px page. Widths in 32s (the GPU's tiling).
                widest = max(e[0][3] for e in items)
                best = None
                for cand in sorted({widest, 2 * widest, 3 * widest, 1024, 1536, 2048, 2560, 3072, 4096}):
                    cw = (cand + 31) // 32 * 32
                    if cw < widest or cw > 4096:
                        continue
                    pg = Page(cw, 4096)
                    got, spill = [], []
                    for e in items:
                        at = pg.place(e[0][3], e[0][4])
                        (spill if at is None else got).append((e, at))
                    area = sum(e[0][3] * e[0][4] for e, _ in got)
                    if not got:
                        continue
                    # fewest spilled boxes first, then the least page area for the boxes placed
                    key = (len(spill), pg.w * max(4, pg.used()) / area)
                    if best is None or key < best[0]:
                        best = (key, pg, got, [e for e, _ in spill])
                if best is None:
                    sys.exit("a %dx%d sprite box doesn't fit a page" % (items[0][0][3], items[0][0][4]))
                _, pg, got, spill = best
                for e, at in got:
                    b = e[0]
                    pg.items.append(b + at)
                    for bb in e[1]:
                        placed.append((kind, len(plist), pg, b[0], b[1], b[2], at[0], at[1], bb[5], bb[6], bb[7]))
                plist.append(pg)
                if spill:
                    queue.insert(0, spill)
        # write the pages, then the boxes against their final heights
        names = {}
        for kind, plist in kpages.items():
            for i, pg in enumerate(plist):
                ph = max(4, pg.used())
                bs = 8 if kind == 1 else 16
                bw, bh = pg.w // 4, ph // 4
                out = np.zeros((bh, bw, bs), np.uint8)
                if kind == 1:
                    out[..., 4:] = 0xFF   # three-colour mode, every texel index 3: transparent
                for pk, sx, sy, w, h, dx, dy in pg.items:
                    skind, W, H, data = page(pk)
                    src = np.frombuffer(data, np.uint8).reshape(H // 4, W // 4, bs)
                    out[dy // 4:(dy + h) // 4, dx // 4:(dx + w) // 4] = src[sy // 4:(sy + h) // 4, sx // 4:(sx + w) // 4]
                tkey = "%s_c%d%03d" % (akey, kind, i)
                payload = out.tobytes()
                head = b"CTX1" + struct.pack("<6I", kind, pg.w, ph, pg.w, ph, len(payload))
                dst = os.path.join(out_dir, tkey + ".ctx")
                # an unchanged page keeps its file (and time: stage_console.py packs again only for changes)
                if not (os.path.exists(dst) and os.path.getsize(dst) == len(head) + len(payload) and open(dst, "rb").read() == head + payload):
                    with open(dst + ".tmp", "wb") as f:
                        f.write(head)
                        f.write(payload)
                    os.replace(dst + ".tmp", dst)
                names[id(pg)] = (len(targets), pg.w, ph)
                targets.append(tkey)
                written += len(payload)
                sizes[akey] = sizes.get(akey, 0) + len(payload)
        for kind, pi, pg, pk, sx, sy, dx, dy, match, W, H in placed:
            t, PW, PH = names[id(pg)]
            su, sv = W / PW, H / PH
            remap.setdefault(pk, []).append(match + (su, (dx - sx) / PW, sv, (dy - sy) / PH, t))
        total_src += sum(w * h // (2 if k == 1 else 1) for k, w, h in (v for v in info.values() if v))
        pages_cache.clear()
        if a.report:
            print("  %-70s %3d pages, %6.1f MB" % (akey[:70], sum(len(p) for p in kpages.values()), sizes.get(akey, 0) / 2**20), flush=True)

    # the table
    pages = sorted((fnv64(k), k) for k in remap)
    if len(set(h for h, _ in pages)) != len(pages):
        sys.exit("two source pages share a hash")
    strings = bytearray()
    offs = []
    for t in targets:
        offs.append(len(strings))
        strings += t.encode("utf-8") + b"\0"
    blob = bytearray(b"CHRM" + struct.pack("<5I", 1, len(pages), sum(len(v) for v in remap.values()), len(targets), len(strings)))
    first = 0
    for h, k in pages:
        blob += struct.pack("<4I", h >> 32, h & 0xFFFFFFFF, first, len(remap[k]))
        first += len(remap[k])
    for h, k in pages:
        for b in remap[k]:
            blob += struct.pack("<8fI", *b)
    for o in offs:
        blob += struct.pack("<I", o)
    blob += strings
    with open(os.path.join(out_dir, "ctexmap.bin.tmp"), "wb") as f:
        f.write(blob)
    os.replace(os.path.join(out_dir, "ctexmap.bin.tmp"), os.path.join(out_dir, "ctexmap.bin"))
    print("cluster_console: %d source pages -> %d sprite pages, %.1f MB (the source pages at full size: %.1f MB); "
          "%d boxes; %.0f s" % (len(pages), len(targets), written / 2**20, total_src / 2**20,
                                 sum(len(v) for v in remap.values()), time.time() - t0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
