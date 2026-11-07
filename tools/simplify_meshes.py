#!/usr/bin/env python3
"""Fewer triangles for the sprites with the largest meshes, drawing the same pixels.

    simplify_meshes.py --data work/GameData [--min-corners 3000] [--alpha-max 2] [--only NAME]

Unity's tight sprite meshes outline every island of a sprite (each star of Hilda Berg's night sky,
each puff of Cala Maria's clouds): 35,000-88,000 triangle corners for a sky layer, which the console's
glue writes every frame (~20 ms of a frame, and its vertex ring overflowed: everything after the night
sky went). Here such a sprite is drawn instead with rectangles of 8x8 or 4x4-texel cells (whichever gives
fewer corners), built from its atlas page's own alpha:

  must draw  the texels the mesh covers whose alpha is over --alpha-max (2 of 255)
  may draw   the texels the mesh covers; any of --alpha-max or less; and the sprite's own texels outside
             its mesh up to --own-max (10): the sky round Unity's islands holds DXT noise and the faint
             edges of the stars' glow it trimmed (4-9). Not another sprite's: the other sprites of the
             page whose meshes reach into this one's rectangle (tight packing), two texels round them,
             only as far as --alpha-max. Two texels round what is drawn must be drawable too (bilinear)

A cell holding texels that must be drawn becomes part of a rectangle if all of it may be drawn. An island
some of whose texels lie in a cell that may not be drawn is kept whole as it was, and the cells near it are
left to it (its edge lies in clear texels, so no seam is left where it meets the rectangles). So nothing another sprite or island owns is drawn, every texel over 2 of 255 is drawn once, and
what changes is texels left out at 2 of 255 or less and the sprite's own faint edges drawn at 10 or less.
The uvs follow from the vertices as before (Sprites.RenderUvs).

Reads the SpriteAtlases and their sprites (<data>/objects/bundles/atlas_*, convert_objects.py) and the
atlas pages (<data>/ctex4k, else <data>/ctex: convert_console.py's DXT pages). Writes
<data>/objects/meshes.bin, which the engine reads (Sprites.FromData):

  'CHMS' u32 version (2) u32 count
  count entries: u16 key length, the key (the sprite's render data key, Sprites.Key),
                 u32 vertex count, the vertices (f32 x, y: local units), u32 index count, the indices (u16)

Deterministic. Everything written comes from the player's own game files.
"""
import argparse
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import chob  # noqa: E402

FORMAT_SIZE = [4, 2, 1, 1, 1, 2, 2, 1, 1, 2, 2, 4, 4]
PAD = 2     # texels round what is drawn that must be clear too (bilinear reads)
CELLS = (8, 4)   # the cell sizes tried (texels a side); a sprite takes the one giving fewest corners


def render_key(k):
    g = k[0]
    return "%d-%d-%d-%d:%d" % (g['data[0]'], g['data[1]'], g['data[2]'], g['data[3]'], k[1] if isinstance(k[1], int) else 0)


def mesh(rd):
    """The sprite's vertices (local units) and triangle indices, read as Sprites.ReadMesh does."""
    vd = rd['m_VertexData']
    count = vd['m_VertexCount']
    data = vd['m_DataSize']
    chans = vd['m_Channels']
    if not data or not count or not chans:
        return None, None
    streams = 0
    for c in chans:
        if c['dimension'] > 0:
            streams = max(streams, c['stream'] + 1)
    stride = [0] * streams
    for c in chans:
        dim = c['dimension'] & 0xF
        if dim == 0:
            continue
        s = c['stream']
        stride[s] = max(stride[s], c['offset'] + dim * FORMAT_SIZE[min(12, c['format'])])
    start = [0] * streams
    for s in range(1, streams):
        start[s] = (start[s - 1] + stride[s - 1] * count + 15) & ~15
    ps, po = chans[0]['stream'], chans[0]['offset']
    v = np.zeros((count, 2), np.float64)
    for i in range(count):
        v[i] = struct.unpack_from('<ff', data, start[ps] + i * stride[ps] + po)
    ib = rd['m_IndexBuffer']
    if not isinstance(ib, (bytes, bytearray)):
        ib = bytes(ib)
    return v, np.frombuffer(ib, '<u2').astype(np.int64)


class Mapping:
    """Local units to the page's texels (origin bottom left) and back, as Sprites.RenderUvs maps them."""

    def __init__(self, rd):
        u, tr = rd['uvTransform'], rd['textureRect']
        self.sx, self.ox, self.sy, self.oy = u['x'], u['y'], u['z'], u['w']
        self.rot = (rd['settingsRaw'] >> 2) & 0xF
        self.cx, self.cy = tr['x'] + tr['width'] * 0.5, tr['y'] + tr['height'] * 0.5

    def ok(self):
        return self.rot in (0, 1, 2, 3) and self.sx != 0 and self.sy != 0

    def to_texels(self, v):
        tx, ty = v[:, 0] * self.sx + self.ox, v[:, 1] * self.sy + self.oy
        if self.rot in (1, 3):
            tx = 2 * self.cx - tx
        if self.rot in (2, 3):
            ty = 2 * self.cy - ty
        return np.stack([tx, ty], 1)

    def to_local(self, x, y):
        if self.rot in (1, 3):
            x = 2 * self.cx - x
        if self.rot in (2, 3):
            y = 2 * self.cy - y
        return (x - self.ox) / self.sx, (y - self.oy) / self.sy


def ctx_alpha(path, want_w, want_h):
    """A CTX1 page's alpha (rows from the bottom, as the texels), scaled to the texture's size if halved."""
    d = open(path, 'rb').read()
    _, fmt, w, h = struct.unpack_from('<IIII', d, 0)
    n = struct.unpack_from('<I', d, 24)[0]
    blocks = np.frombuffer(d, np.uint8, n, 28)
    bw, bh = (w + 3) // 4, (h + 3) // 4
    if fmt == 2:   # BC3: 8 bytes of alpha a block
        b = blocks.reshape(bh, bw, 16)
        a0, a1 = b[:, :, 0].astype(np.int32), b[:, :, 1].astype(np.int32)
        bits = np.zeros((bh, bw), np.uint64)
        for k in range(6):
            bits |= b[:, :, 2 + k].astype(np.uint64) << np.uint64(8 * k)
        pal = np.zeros((bh, bw, 8), np.int32)
        pal[:, :, 0], pal[:, :, 1] = a0, a1
        big = a0 > a1
        for k in range(1, 7):
            pal[:, :, 1 + k] = np.where(big, ((7 - k) * a0 + k * a1) // 7, 0)
        for k in range(1, 5):
            pal[:, :, 1 + k] = np.where(big, pal[:, :, 1 + k], ((5 - k) * a0 + k * a1) // 5)
        pal[:, :, 6] = np.where(big, pal[:, :, 6], 0)
        pal[:, :, 7] = np.where(big, pal[:, :, 7], 255)
        out = np.zeros((bh * 4, bw * 4), np.uint8)
        for t in range(16):
            idx = ((bits >> np.uint64(3 * t)) & np.uint64(7)).astype(np.int64)
            out[(t // 4)::4, (t % 4)::4] = np.take_along_axis(pal, idx[:, :, None], 2)[:, :, 0]
    elif fmt == 1:   # BC1: transparent where the block is in 3-colour mode and the index is 3
        b = blocks.reshape(bh, bw, 8)
        c0 = b[:, :, 0].astype(np.int32) | (b[:, :, 1].astype(np.int32) << 8)
        c1 = b[:, :, 2].astype(np.int32) | (b[:, :, 3].astype(np.int32) << 8)
        bits = (b[:, :, 4].astype(np.uint32) | (b[:, :, 5].astype(np.uint32) << 8) | (b[:, :, 6].astype(np.uint32) << 16)
                | (b[:, :, 7].astype(np.uint32) << 24))
        out = np.zeros((bh * 4, bw * 4), np.uint8)
        for t in range(16):
            idx = (bits >> np.uint32(2 * t)) & np.uint32(3)
            out[(t // 4)::4, (t % 4)::4] = np.where((c0 <= c1) & (idx == 3), 0, 255)
    else:
        return None
    out = out[:h, :w]
    if (w, h) != (want_w, want_h):   # a halved page: each of its texels stands for a block of the texture's
        sy, sx = want_h // h, want_w // w
        out = np.repeat(np.repeat(out, sy, 0), sx, 1)[:want_h, :want_w]
    return out


def islands(tx, t):
    """Each triangle's island (1-based): triangles joined by shared vertices or vertices at the same place.
    An island's edge lies in clear texels (the mesh outlines what isn't), so one kept whole leaves no seam."""
    n = len(tx)
    parent = list(range(n))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    seen = {}
    for i in range(n):
        k = (round(tx[i][0], 3), round(tx[i][1], 3))
        if k in seen:
            parent[find(i)] = find(seen[k])
        else:
            seen[k] = i
    for a, b, c in t:
        ra = find(a)
        for x in (b, c):
            rx = find(x)
            if rx != ra:
                parent[rx] = ra
    ids, out = {}, []
    for a, _, _ in t:
        out.append(ids.setdefault(find(a), len(ids) + 1))
    return np.array(out, np.int32), len(ids)


def grow(mask, r):
    return np.asarray(Image.fromarray(mask.astype(np.uint8) * 255).filter(ImageFilter.MaxFilter(2 * r + 1))) > 0


def shrink(mask, r):
    return np.asarray(Image.fromarray(mask.astype(np.uint8) * 255).filter(ImageFilter.MinFilter(2 * r + 1))) > 0


def cover(v, tris, mp, alpha, alpha_max, own_max, foreign_tris, CELL):
    """The sprite's new vertices and indices (or None): rectangles of CELL-texel cells, and the islands kept
    whole as they were (those near what may not be drawn; a triangle kept alone left a seam where it met one
    replaced: the edge between them runs through opaque texels that neither then drew). foreign_tris: the
    triangles (texel points) of the page's other sprites that reach into its box."""
    tx = mp.to_texels(v)
    t = tris.reshape(-1, 3)
    lo, hi = np.floor(tx.min(0)).astype(int), np.ceil(tx.max(0)).astype(int)
    x0, y0 = lo[0] - PAD - CELL, lo[1] - PAD - CELL
    W = ((hi[0] + PAD + CELL - x0) // CELL + 1) * CELL
    H = ((hi[1] + PAD + CELL - y0) // CELL + 1) * CELL
    ah, aw = alpha.shape
    region = np.zeros((H, W), np.uint8)   # texels off the page count as clear
    sx0, sy0, sx1, sy1 = max(0, x0), max(0, y0), min(aw, x0 + W), min(ah, y0 + H)
    if sx1 > sx0 and sy1 > sy0:
        region[sy0 - y0:sy1 - y0, sx0 - x0:sx1 - x0] = alpha[sy0:sy1, sx0:sx1]
    tri_island, count = islands(tx, t)
    lab_img = Image.new('I', (W, H), 0)   # each texel's island (1-based)
    dr = ImageDraw.Draw(lab_img)
    for ti, (a, b, c) in enumerate(t):
        dr.polygon([(tx[k][0] - x0, tx[k][1] - y0) for k in (a, b, c)], fill=int(tri_island[ti]), outline=int(tri_island[ti]))
    label = np.asarray(lab_img).astype(np.int32)
    covered = label > 0
    fimg = Image.new('L', (W, H), 0)
    fdr = ImageDraw.Draw(fimg)
    for p in foreign_tris:
        fdr.polygon([(x - x0, y - y0) for x, y in p], fill=255, outline=255)
    foreign = grow(np.asarray(fimg) > 0, PAD)
    may = covered | (region <= alpha_max) | (~foreign & (region <= own_max))
    safe = shrink(may, PAD)
    need = covered & (region > alpha_max)
    gh, gw = H // CELL, W // CELL

    def cells(mask, fn):
        return fn(mask.reshape(gh, CELL, gw, CELL), axis=(1, 3))

    drawable = cells(safe, np.all)
    kept = np.zeros(count + 1, bool)
    while True:
        keptmask = kept[label]
        blocked = cells(grow(keptmask, PAD + 1), np.any) if keptmask.any() else np.zeros((gh, gw), bool)
        want = need & ~keptmask
        wanted = cells(want, np.any)
        bad = wanted & ~(drawable & ~blocked)
        if not bad.any():
            break
        badtexels = np.repeat(np.repeat(bad, CELL, 0), CELL, 1) & want
        more = np.unique(label[badtexels])
        more = more[more > 0]
        if not len(more) or kept[more].all():
            return None
        kept[more] = True
    # the wanted cells as rectangles: runs along each row, then runs of equal rows merged
    rects, open_runs = [], {}
    for gy in range(gh):
        row, gx, runs = wanted[gy], 0, []
        while gx < gw:
            if row[gx]:
                s = gx
                while gx < gw and row[gx]:
                    gx += 1
                runs.append((s, gx))
            else:
                gx += 1
        nxt = {}
        for r in runs:
            if r in open_runs:
                nxt[r] = open_runs.pop(r)
            else:
                nxt[r] = gy
        for (s, e), y_start in open_runs.items():
            rects.append((s, y_start, e, gy))
        open_runs = nxt
    for (s, e), y_start in open_runs.items():
        rects.append((s, y_start, e, gh))
    # the kept triangles, their vertices first, then each rectangle's four (clamped to the mesh's box)
    keep_tris = [ti for ti in range(len(t)) if kept[tri_island[ti]]]
    remap, new_v, new_i = {}, [], []
    for ti in keep_tris:
        for k in t[ti]:
            if k not in remap:
                remap[k] = len(new_v)
                new_v.append(tuple(v[k]))
            new_i.append(remap[k])
    for gx0, gy0, gx1, gy1 in rects:
        ax0 = max(tx[:, 0].min(), x0 + gx0 * CELL)
        ay0 = max(tx[:, 1].min(), y0 + gy0 * CELL)
        ax1 = min(tx[:, 0].max(), x0 + gx1 * CELL)
        ay1 = min(tx[:, 1].max(), y0 + gy1 * CELL)
        if ax1 <= ax0 or ay1 <= ay0:
            continue
        base = len(new_v)
        for x, y in ((ax0, ay0), (ax1, ay0), (ax1, ay1), (ax0, ay1)):
            new_v.append(mp.to_local(x, y))
        new_i += [base, base + 1, base + 2, base, base + 2, base + 3]
    if len(new_v) > 65535 or len(new_i) >= len(tris) * 0.8:
        return None
    return np.array(new_v, np.float32), np.array(new_i, np.int64)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--data', default='work/GameData')
    ap.add_argument('--min-corners', type=int, default=3000)
    ap.add_argument('--alpha-max', type=int, default=2)
    ap.add_argument('--own-max', type=int, default=10)

    ap.add_argument('--only', default=None, help='sprite names containing this (a check: nothing written)')
    a = ap.parse_args()
    objects = os.path.join(a.data, 'objects')
    bundles = os.path.join(objects, 'bundles')
    out, report = {}, []
    for atlas_dir in sorted(os.listdir(bundles)):
        if not atlas_dir.startswith('atlas_'):
            continue
        for f in sorted(os.listdir(os.path.join(bundles, atlas_dir))):
            if not f.endswith('.chob'):
                continue
            c = chob.load(os.path.join(bundles, atlas_dir, f))
            atlases = [tree for _, tn, tree in c.objects.values() if tn == 'SpriteAtlas']
            if not atlases:
                continue
            rdm = {render_key(k): rd for at in atlases for k, rd in at['m_RenderDataMap']}
            textures = {pid: tree for pid, (_, tn, tree) in c.objects.items() if tn == 'Texture2D'}
            sprites = {pid: tree for pid, (_, tn, tree) in c.objects.items() if tn == 'Sprite'}
            placed = {}   # page -> [(texel box, triangles in texels)] of every sprite on it, made when first wanted

            def page_sprites(page):
                if page not in placed:
                    lst = []
                    for st in sprites.values():
                        r2 = rdm.get(render_key(st['m_RenderDataKey']))
                        if r2 is None or r2['texture'].file != 0 or r2['texture'].path != page:
                            continue
                        m2 = Mapping(r2)
                        v2, t2 = mesh(st['m_RD'])
                        if v2 is None or not m2.ok():
                            continue
                        tx2 = m2.to_texels(v2)
                        lst.append((render_key(st['m_RenderDataKey']), tx2.min(0), tx2.max(0), tx2, t2.reshape(-1, 3)))
                    placed[page] = lst
                return placed[page]
            alphas = {}
            for pid, (_, tn, tree) in sorted(c.objects.items()):
                if tn != 'Sprite' or len(tree['m_RD']['m_IndexBuffer']) // 2 < a.min_corners:
                    continue
                if a.only and a.only not in tree['m_Name']:
                    continue
                key = render_key(tree['m_RenderDataKey'])
                rd = rdm.get(key)
                if rd is None or rd['texture'].file != 0:
                    continue
                mp = Mapping(rd)
                v, tris = mesh(tree['m_RD'])
                if v is None or not mp.ok():
                    report.append((len(tris) if tris is not None else 0, None, tree['m_Name'], 'not mapped'))
                    continue
                page = rd['texture'].path
                if page not in alphas:
                    tex, src = textures.get(page), None
                    for d in ('ctex4k', 'ctex'):
                        p = os.path.join(a.data, d, 'bundles_%s_%s_%d.ctx' % (atlas_dir, f[:-5], page))
                        if os.path.exists(p):
                            src = p
                            break
                    alphas[page] = ctx_alpha(src, tex['m_Width'], tex['m_Height']) if src and tex else None
                if alphas[page] is None:
                    report.append((len(tris), None, tree['m_Name'], 'no page'))
                    continue
                tx = mp.to_texels(v)
                lo, hi = tx.min(0) - PAD - 1, tx.max(0) + PAD + 1
                foreign = [[tuple(ftx[k]) for k in tri] for fkey, flo, fhi, ftx, ft in page_sprites(page)
                           if fkey != key and flo[0] < hi[0] and fhi[0] > lo[0] and flo[1] < hi[1] and fhi[1] > lo[1]
                           for tri in ft]
                res = None
                for cell in CELLS:
                    r = cover(v, tris, mp, alphas[page], a.alpha_max, a.own_max, foreign, cell)
                    if r is not None and (res is None or len(r[1]) < len(res[1])):
                        res = r
                if res is not None:
                    out[key] = res
                report.append((len(tris), len(res[1]) if res is not None else None, tree['m_Name'], atlas_dir))
    report.sort(key=lambda r: -r[0])
    for before, after, name, where in report:
        print('%7d -> %6s corners  %-44s %s' % (before, after if after is not None else 'kept', name, where))
    print('%d sprites simplified of %d; corners %d -> %d' % (len(out), len(report), sum(r[0] for r in report),
                                                            sum(r[1] if r[1] is not None else r[0] for r in report)))
    if a.only:
        return
    dst = os.path.join(objects, 'meshes.bin')
    with open(dst, 'wb') as fo:
        fo.write(b'CHMS' + struct.pack('<II', 2, len(out)))
        for key in sorted(out):
            nv, idx = out[key]
            kb = key.encode()
            fo.write(struct.pack('<H', len(kb)) + kb + struct.pack('<I', len(nv)) + nv.astype('<f4').tobytes())
            fo.write(struct.pack('<I', len(idx)) + idx.astype('<u2').tobytes())
    print('wrote', dst)


if __name__ == '__main__':
    main()
