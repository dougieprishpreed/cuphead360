"""remap_check.py dump.bin data_dir (a PC check of console/ch360_imports.c, remap_item): host_native's draw dump (CH360_DUMP_DRAW, with its .keys) against the
full-size pages. Every item on a sprite page (cluster_console.py's <atlas>_c<N>) is mapped back through
ctexmap.bin to its source page: its uvs must be a whole-texel shift of the source's, and the texels under
its vertices must be the source page's, and (version 2, --cover pages) every texel under its triangles and the one
around them that bilinear filtering reads. Items still on an atlas page that has sprite pages are counted."""
import struct, sys, os, collections
import numpy as np
import texture2ddecoder

dump, data = sys.argv[1], sys.argv[2]
keys = {}
for line in open(dump + '.keys', encoding='utf-8'):
    i, k = line.rstrip('\n').split(' ', 1)
    keys[int(i)] = k

m = open(os.path.join(data, 'ctexc', 'ctexmap.bin'), 'rb').read()
_, ver, npages, nboxes, ntargets, sb = struct.unpack_from('<4s5I', m, 0)
BS = 44 if ver == 2 else 36
pages = [struct.unpack_from('<4I', m, 24 + 16 * i) for i in range(npages)]
boxes = [struct.unpack_from('<8fI', m, 24 + 16 * npages + BS * i) for i in range(nboxes)]
toff = 24 + 16 * npages + BS * nboxes
tnames = []
strings = m[toff + 4 * ntargets:]
for i in range(ntargets):
    o = struct.unpack_from('<I', m, toff + 4 * i)[0]
    tnames.append(strings[o:strings.index(b'\0', o)].decode())
tindex = {n: i for i, n in enumerate(tnames)}


def fnv64(key):
    h = 0xcbf29ce484222325
    for c in key.encode('utf-8'):
        h = ((h ^ c) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h


page_by_hash = {(hi << 32) | lo: (first, count) for hi, lo, first, count in pages}
# boxes per target: (source page hash, box)
by_target = collections.defaultdict(list)
for (hi, lo, first, count) in pages:
    for b in boxes[first:first + count]:
        by_target[b[8]].append(((hi << 32) | lo, b))
src_key_of_hash = {}

cache = {}


def texels(path):
    if path not in cache:
        d = open(path, 'rb').read()
        magic, kind, w, h, ow, oh, n = struct.unpack_from('<4s6I', d, 0)
        f = texture2ddecoder.decode_bc1 if kind == 1 else texture2ddecoder.decode_bc3
        if len(cache) >= 8:
            cache.pop(next(iter(cache)))
        cache[path] = (w, h, np.frombuffer(f(d[28:28 + n], w, h), np.uint8).reshape(h, w, 4))
    return cache[path]


VIEW = np.dtype([('xywh', '<f4', 4), ('clear', '<u4'), ('rgb', '<f4', 3), ('first', '<u4'), ('items', '<u4'),
                 ('target', '<u4'), ('tw', '<u4'), ('th', '<u4'), ('a', '<f4')])
ITEM = np.dtype([('tex', '<i4'), ('mode', '<u4'), ('first', '<u4'), ('count', '<u4'), ('box', '<f4', 4), ('textures', '<i4', 2), ('params', '<f4', 12), ('stencil', '<u4')])
VERT = np.dtype([('xyuv', '<f4', 4), ('color', 'u1', 4), ('blend', 'u1', 4)])
d = open(dump, 'rb').read()
p = 0
checked = bad_shift = bad_texel = on_page = no_box = bad_area = 0
tri_seen = set()
tri_texels = [0]
work = []   # (target key, source hash, uv target array, uv source array)
while p + 8 <= len(d):
    f, n = struct.unpack_from('<II', d, p)
    body = d[p + 8:p + 8 + n]
    nv, ni, nx = struct.unpack_from('<III', body, 0)
    items = np.frombuffer(body, ITEM, ni, 12 + nv * VIEW.itemsize)
    verts = np.frombuffer(body, VERT, nx, 12 + nv * VIEW.itemsize + ni * ITEM.itemsize)
    for it in items:
        k = keys.get(int(it['tex']))
        if k is None:
            continue
        if fnv64(k) in page_by_hash:
            on_page += 1
            continue
        t = tindex.get(k)
        if t is None:
            continue
        uv = verts[it['first']:it['first'] + it['count']]['xyuv'][:, 2:].astype(np.float64)
        # the box whose target region holds these uvs
        hit = None
        for sh, b in by_target[t]:
            u0, v0, u1, v1, su, ou, sv, ov, _ = b
            src = np.stack([(uv[:, 0] - ou) / su, (uv[:, 1] - ov) / sv], axis=1)
            if (src[:, 0] >= u0 - 1e-5).all() and (src[:, 0] <= u1 + 1e-5).all() and (src[:, 1] >= v0 - 1e-5).all() and (src[:, 1] <= v1 + 1e-5).all():
                hit = (sh, b, src)
                break
        if hit is None:
            no_box += 1
            continue
        work.append((k, hit[0], hit[1], uv, hit[2]))
    p += 8 + n

# source keys: the halved page keys (ctex) whose hash is the box's
for fn in os.listdir(os.path.join(data, 'ctex4k')):
    src_key_of_hash[fnv64(fn[:-4])] = fn[:-4]
work.sort(key=lambda w: (w[1], w[0]))
for tkey, sh, b, uv, src in work:
    sk = src_key_of_hash.get(sh)
    W, H, S = texels(os.path.join(data, 'ctex4k', sk + '.ctx'))
    xs, ys = src[:, 0] * W, src[:, 1] * H
    tw, th, T = texels(os.path.join(data, 'ctexc', tkey + '.ctx'))
    xt, yt = uv[:, 0] * tw, uv[:, 1] * th
    sx, sy = np.round(xt - xs), np.round(yt - ys)
    checked += 1
    if np.abs(xt - xs - sx).max() > 1e-2 or np.abs(yt - ys - sy).max() > 1e-2 or len(set(sx)) > 1 or len(set(sy)) > 1:
        bad_shift += 1
        continue
    # texels just inside the vertices (clamped to the page)
    ix = np.clip(np.floor(xs - 0.5 * np.sign(xs - xs.mean())).astype(int), 0, W - 1)
    iy = np.clip(np.floor(ys - 0.5 * np.sign(ys - ys.mean())).astype(int), 0, H - 1)
    jx, jy = ix + int(sx[0]), iy + int(sy[0])
    if (jx < 0).any() or (jx >= tw).any() or (jy < 0).any() or (jy >= th).any() or not np.array_equal(S[iy, ix], T[jy, jx]):
        bad_texel += 1
        continue
    # every texel a triangle's pixels can read: within a texel of the triangle (bilinear reads half one beyond)
    sig = (tkey, uv.tobytes())
    if sig in tri_seen:
        continue
    tri_seen.add(sig)
    bad = False
    for t in range(0, len(xs) - 2, 3):
        ax, ay, bx, by, cx, cy = xs[t], ys[t], xs[t + 1], ys[t + 1], xs[t + 2], ys[t + 2]
        area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay)
        if abs(area) < 1e-9:
            continue
        x0, x1 = int(np.floor(min(ax, bx, cx))) - 1, int(np.ceil(max(ax, bx, cx))) + 1
        y0, y1 = int(np.floor(min(ay, by, cy))) - 1, int(np.ceil(max(ay, by, cy))) + 1
        x0, y0 = max(x0, 0), max(y0, 0)
        x1, y1 = min(x1, W - 1), min(y1, H - 1)
        if x1 < x0 or y1 < y0:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        sgn = 1.0 if area > 0 else -1.0
        # the triangle grown by a texel's square (its edges moved out, and its box): exactly where samples reach
        ok = (gx > min(ax, bx, cx) - 1) & (gx < max(ax, bx, cx) + 1) & (gy > min(ay, by, cy) - 1) & (gy < max(ay, by, cy) + 1)
        for (px, py), (qx, qy) in (((ax, ay), (bx, by)), ((bx, by), (cx, cy)), ((cx, cy), (ax, ay))):
            nx, ny = -(qy - py) * sgn, (qx - px) * sgn
            # bilinear reads a texel when a sample inside the triangle is less than a texel from its centre on both axes
            ok &= nx * (gx - px) + ny * (gy - py) > -(abs(nx) + abs(ny))
        iy2, ix2 = np.nonzero(ok)
        if len(ix2) == 0:
            continue
        sx2, sy2 = ix2 + x0, iy2 + y0
        tx2, ty2 = sx2 + int(sx[0]), sy2 + int(sy[0])
        inside = (tx2 >= 0) & (tx2 < tw) & (ty2 >= 0) & (ty2 < th)   # beyond the page: the sampler clamps to its edge
        sx2, sy2, tx2, ty2 = sx2[inside], sy2[inside], tx2[inside], ty2[inside]
        if not np.array_equal(S[sy2, sx2], T[ty2, tx2]):
            bad = True
            break
        tri_texels[0] += len(sx2)
    if bad:
        bad_area += 1
print('items on sprite pages checked %d: shift not whole %d, texels differ %d; no box for %d; items left on atlas pages %d'
      % (checked, bad_shift, bad_texel, no_box, on_page))
print('under the triangles (each mesh once): %d texels compared, %d meshes with a texel that differs' % (tri_texels[0], bad_area))
