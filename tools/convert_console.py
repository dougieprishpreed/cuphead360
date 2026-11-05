#!/usr/bin/env python3
"""Console textures: re-encode the exported textures for the Xbox 360 GPU.

    convert_console.py --data <GameData dir> [--only-missing] [--max-size N] [--keys a,b]
                       [--out DIR] [--halved-only] [--workers N]

The full-size atlas pages tools/cluster_console.py cuts sprites from:
    convert_console.py --data work/GameData --max-size 4096 --out work/GameData/ctex4k --halved-only --workers 2

Reads <data>/textures/*.tex (tools/convert_textures.py) and writes
<data>/ctex/<key>.ctx, the format the console host loads. Xenos has no BC7,
so every texture is decoded (mip 0) and stored as DXT1 (alpha only 0/255),
DXT5 (partial alpha) or BGRA8 (small, single-channel or font textures).
A DXT colour half is fitted to its block (principal axis weighted by alpha,
endpoints refined by least squares), or keeps the bounding-box encoding
where that has less error: the bounding box alone left dark texels where
three colours meet (the title's poker chips).
Pages larger than --max-size (default 2048) are halved until they fit: the
game's atlases are 4096 px for 1080p and up, the console draws 720p. UVs
are normalized, so nothing else changes. Halving is Lanczos-3 on
premultiplied colour (see halve). Each distinct 4x4 block is encoded once
(a sprite page is mostly transparent or flat-filled blocks), the same bytes
in half the time. --workers runs that many processes at below-normal
priority.

File format (little-endian):

  'CTX1' u32 format (1 DXT1, 2 DXT5, 3 BGRA8) u32 width u32 height
  u32 origWidth u32 origHeight u32 byteCount  bytes (one level)

Reports per-format counts and the total size. Deterministic.
"""
import argparse
import os
import struct
import sys
import time

import numpy as np
import texture2ddecoder
from PIL import Image


def decode(fmt, w, h, data):
    """mip 0 as an (h, w, 4) uint8 RGBA array, rows in Unity's stored order (bottom first)."""
    if fmt == 25:
        raw = texture2ddecoder.decode_bc7(data, w, h)
    elif fmt == 12:
        raw = texture2ddecoder.decode_bc3(data, w, h)
    elif fmt == 10:
        raw = texture2ddecoder.decode_bc1(data, w, h)
    elif fmt == 4:   # RGBA32
        a = np.frombuffer(data[:w * h * 4], np.uint8).reshape(h, w, 4)
        return a.copy()
    elif fmt == 5:   # ARGB32
        a = np.frombuffer(data[:w * h * 4], np.uint8).reshape(h, w, 4)
        return a[..., [1, 2, 3, 0]].copy()
    elif fmt == 3:   # RGB24
        a = np.frombuffer(data[:w * h * 3], np.uint8).reshape(h, w, 3)
        return np.concatenate([a, np.full((h, w, 1), 255, np.uint8)], axis=-1)
    elif fmt == 1:   # Alpha8: white with alpha
        a = np.frombuffer(data[:w * h], np.uint8).reshape(h, w, 1)
        return np.concatenate([np.full((h, w, 3), 255, np.uint8), a], axis=-1)
    elif fmt == 2:   # ARGB4444
        v = np.frombuffer(data[:w * h * 2], "<u2").reshape(h, w)
        ch = [((v >> s) & 15).astype(np.uint8) * 17 for s in (8, 4, 0, 12)]
        return np.stack(ch, axis=-1)
    elif fmt == 13:  # RGBA4444
        v = np.frombuffer(data[:w * h * 2], "<u2").reshape(h, w)
        ch = [((v >> s) & 15).astype(np.uint8) * 17 for s in (12, 8, 4, 0)]
        return np.stack(ch, axis=-1)
    else:
        raise ValueError("texture format %d not handled" % fmt)
    # texture2ddecoder gives BGRA bytes
    a = np.frombuffer(raw, np.uint8).reshape(h, w, 4)
    return a[..., [2, 1, 0, 3]].copy()


def halve(img):
    """Half size with Pillow's Lanczos-3, which filters RGBA premultiplied by
    alpha. Against a 2x2 average, on the Root Pack's and the player's atlases
    encoded and magnified back as the console draws them: colour PSNR +0.1 dB,
    3-4% more of the source's edge contrast (a Mitchell cubic was blurrier
    than the average, Catmull-Rom level with it). The halved pages' blur is
    mostly resolution: their one-texel outlines become half a texel."""
    h, w = img.shape[:2]
    im = Image.fromarray(np.ascontiguousarray(img[:h - h % 2, :w - w % 2]), "RGBA")
    return np.asarray(im.resize((w // 2, h // 2), Image.LANCZOS)).copy()


def pad4(img):
    h, w = img.shape[:2]
    ph, pw = (-h) % 4, (-w) % 4
    if ph or pw:
        img = np.pad(img, ((0, ph), (0, pw), (0, 0)), mode="edge")
    return img


def blocks(img):
    h, w = img.shape[:2]
    return img.reshape(h // 4, 4, w // 4, 4, 4).transpose(0, 2, 1, 3, 4).reshape(-1, 16, 4)


def to565(c):
    c = np.clip(np.round(c), 0, 255).astype(np.uint32)
    return ((c[..., 0] >> 3) << 11) | ((c[..., 1] >> 2) << 5) | (c[..., 2] >> 3)


def from565(v):
    r = (v >> 11) & 31
    g = (v >> 5) & 63
    b = v & 31
    return np.stack([(r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2)], axis=-1).astype(np.float32)


def color_block(px, punch):
    """DXT colour half: endpoints from the block's bounding box along its main axis, 2-bit indices."""
    rgb = px[..., :3].astype(np.float32)
    lo, hi = rgb.min(axis=1), rgb.max(axis=1)
    inset = (hi - lo) / 16.0
    lo, hi = lo + inset, hi - inset
    # Order each channel's endpoints along the dominant covariance sign with green.
    mean = rgb.mean(axis=1, keepdims=True)
    d = rgb - mean
    cov_rg = (d[..., 0] * d[..., 1]).sum(axis=1)
    cov_bg = (d[..., 2] * d[..., 1]).sum(axis=1)
    c0, c1 = hi.copy(), lo.copy()
    sw = cov_rg < 0
    c0[sw, 0], c1[sw, 0] = lo[sw, 0], hi[sw, 0]
    sw = cov_bg < 0
    c0[sw, 2], c1[sw, 2] = lo[sw, 2], hi[sw, 2]
    v0, v1 = to565(c0), to565(c1)
    if punch is None:
        # four-colour mode needs v0 > v1
        swap = v0 < v1
        v0, v1 = np.where(swap, v1, v0), np.where(swap, v0, v1)
        e0, e1 = from565(v0), from565(v1)
        pal = np.stack([e0, e1, (2 * e0 + e1) / 3, (e0 + 2 * e1) / 3], axis=1)   # (N,4,3)
    else:
        # three-colour mode (v0 <= v1) with index 3 transparent
        swap = v0 > v1
        v0, v1 = np.where(swap, v1, v0), np.where(swap, v0, v1)
        e0, e1 = from565(v0), from565(v1)
        pal = np.stack([e0, e1, (e0 + e1) / 2, np.full_like(e0, 1e6)], axis=1)
    dist = ((rgb[:, :, None, :] - pal[:, None, :, :]) ** 2).sum(axis=-1)   # (N,16,4)
    idx = dist.argmin(axis=-1).astype(np.uint32)
    if punch is not None:
        idx = np.where(punch, 3, idx)
    eq = v0 == v1
    if punch is None:
        idx = np.where(eq[:, None], 0, idx)
    bits = (idx << (2 * np.arange(16, dtype=np.uint32))).sum(axis=1).astype(np.uint32)
    out = np.zeros((len(px), 8), np.uint8)
    out[:, 0], out[:, 1] = v0 & 255, v0 >> 8
    out[:, 2], out[:, 3] = v1 & 255, v1 >> 8
    for k in range(4):
        out[:, 4 + k] = (bits >> (8 * k)) & 255
    return out


def palette4(v0, v1):
    e0, e1 = from565(v0), from565(v1)
    return np.stack([e0, e1, (2 * e0 + e1) / 3, (e0 + 2 * e1) / 3], axis=1)   # (N,4,3)


def best_indices(rgb, pal):
    dist = ((rgb[:, :, None, :] - pal[:, None, :, :]) ** 2).sum(axis=-1)   # (N,16,4)
    return dist.argmin(axis=-1), dist.min(axis=-1)


def color_block_fit(px):
    """Four-colour DXT colour half fitted to the block: endpoints along the
    principal axis of its pixels (weighted by alpha, so transparent pixels'
    colours don't pull them), refined by least squares on the indices they
    get, three rounds. Returns the 565 endpoints (v0 > v1 or equal) and the
    indices, as color_block's four-colour branch would store them."""
    rgb = px[..., :3].astype(np.float32)
    w = px[..., 3].astype(np.float32) / 255.0 + 1.0 / 255.0                 # (N,16)
    ws = w.sum(axis=1, keepdims=True)
    mean = (rgb * w[..., None]).sum(axis=1, keepdims=True) / ws[..., None]
    d = (rgb - mean) * np.sqrt(w)[..., None]
    cov = np.einsum('nki,nkj->nij', d, d)
    axis = np.ones((len(px), 3), np.float32) / np.sqrt(3)
    for _ in range(8):   # power iteration
        axis = np.einsum('nij,nj->ni', cov, axis)
        axis /= np.maximum(np.linalg.norm(axis, axis=1, keepdims=True), 1e-6)
    t = np.einsum('nki,ni->nk', rgb - mean, axis)
    big = np.where(w > 0.5, t, np.nan)   # endpoints from the opaque pixels when there are any
    tmin = np.where(np.isnan(big).all(axis=1), t.min(axis=1), np.nanmin(np.where(np.isnan(big), np.inf, big), axis=1))
    tmax = np.where(np.isnan(big).all(axis=1), t.max(axis=1), np.nanmax(np.where(np.isnan(big), -np.inf, big), axis=1))
    e0 = mean[:, 0] + tmax[:, None] * axis
    e1 = mean[:, 0] + tmin[:, None] * axis
    wa = np.array([1, 0, 2 / 3, 1 / 3], np.float32)
    for _ in range(3):
        pal = np.stack([e0, e1, (2 * e0 + e1) / 3, (e0 + 2 * e1) / 3], axis=1)
        idx, _ = best_indices(rgb, pal)
        a = wa[idx] * w
        b = (1 - wa[idx]) * w
        aa, bb, ab = (wa[idx] * a).sum(1), ((1 - wa[idx]) * b).sum(1), (wa[idx] * b).sum(1)
        ap = (a[..., None] * rgb).sum(1)
        bp = (b[..., None] * rgb).sum(1)
        det = aa * bb - ab * ab
        ok = np.abs(det) > 1e-6
        safe = np.where(ok, det, 1)[:, None]
        n0 = (bb[:, None] * ap - ab[:, None] * bp) / safe
        n1 = (aa[:, None] * bp - ab[:, None] * ap) / safe
        e0 = np.where(ok[:, None], np.clip(n0, 0, 255), e0)
        e1 = np.where(ok[:, None], np.clip(n1, 0, 255), e1)
    v0, v1 = to565(e0), to565(e1)
    swap = v0 < v1
    v0, v1 = np.where(swap, v1, v0), np.where(swap, v0, v1)
    idx, err = best_indices(rgb, palette4(v0, v1))
    idx = np.where((v0 == v1)[:, None], 0, idx)
    return v0, v1, idx.astype(np.uint32), (err * w).sum(axis=1)


def pack_color(v0, v1, idx):
    bits = (idx << (2 * np.arange(16, dtype=np.uint32))).sum(axis=1).astype(np.uint32)
    out = np.zeros((len(v0), 8), np.uint8)
    out[:, 0], out[:, 1] = v0 & 255, v0 >> 8
    out[:, 2], out[:, 3] = v1 & 255, v1 >> 8
    for k in range(4):
        out[:, 4 + k] = (bits >> (8 * k)) & 255
    return out


def color_block_best(px):
    """The four-colour half: the fitted encoding, or color_block's
    bounding-box one where that has less (alpha-weighted) error."""
    old = color_block(px, None)
    ov0 = old[:, 0].astype(np.uint32) | (old[:, 1].astype(np.uint32) << 8)
    ov1 = old[:, 2].astype(np.uint32) | (old[:, 3].astype(np.uint32) << 8)
    obits = old[:, 4:8].astype(np.uint32)
    obits = obits[:, 0] | (obits[:, 1] << 8) | (obits[:, 2] << 16) | (obits[:, 3] << 24)
    oidx = (obits[:, None] >> (2 * np.arange(16, dtype=np.uint32))) & 3
    rgb = px[..., :3].astype(np.float32)
    w = px[..., 3].astype(np.float32) / 255.0 + 1.0 / 255.0
    opal = palette4(ov0, ov1)
    oerr = (((rgb - np.take_along_axis(opal, oidx[..., None].astype(np.int64), axis=1)) ** 2).sum(-1) * w).sum(1)
    # Blocks the old encoding already gets exactly (flat colour, an atlas's empty areas) can't do better: not fitted.
    need = oerr > 0
    out = old.copy()
    if need.any():
        v0, v1, idx, err = color_block_fit(px[need])
        new = pack_color(v0, v1, idx)
        out[need] = np.where((err < oerr[need])[:, None], new, old[need])
    return out


def alpha_block(px):
    a = px[..., 3].astype(np.float32)
    a0, a1 = a.max(axis=1), a.min(axis=1)
    pal = np.stack([a0, a1] + [((7 - i) * a0 + i * a1) / 7 for i in range(1, 7)], axis=1)   # 8-level mode (a0 > a1)
    idx = np.abs(a[:, :, None] - pal[:, None, :]).argmin(axis=-1).astype(np.uint64)
    flat = a0 == a1
    idx[flat] = 0
    bits = (idx << (3 * np.arange(16, dtype=np.uint64))).sum(axis=1)
    out = np.zeros((len(px), 8), np.uint8)
    out[:, 0], out[:, 1] = a0.astype(np.uint8), a1.astype(np.uint8)
    for k in range(6):
        out[:, 2 + k] = ((bits >> np.uint64(8 * k)) & np.uint64(255)).astype(np.uint8)
    return out


def encode(img, kind):
    """DXT1 (kind 1) or DXT5 bytes. Each distinct 4x4 block is encoded once:
    the encoders work block by block, and a sprite page is mostly
    transparent or flat-filled blocks (59% of a Root Pack page), so the
    bytes are the same as encoding every block."""
    bl = blocks(pad4(img))
    key = np.ascontiguousarray(bl).reshape(len(bl), 64).view(np.dtype((np.void, 64))).ravel()
    _, first, inverse = np.unique(key, return_index=True, return_inverse=True)
    return encode_blocks(bl[first], kind)[inverse.ravel()].tobytes()


def encode_blocks(bl, kind):
    if kind == 1:
        # Blocks with transparent texels take three-colour mode; the others the fitted four-colour encoding.
        punch = bl[..., 3] < 128
        out = color_block_best(bl)
        has = punch.any(axis=1)
        if has.any():
            out[has] = color_block(bl[has], punch[has])
        return out
    return np.concatenate([alpha_block(bl), color_block_best(bl)], axis=1)


# Textures kept smaller than --max-size, by key prefix: the film grain's 127
# frames (1024x512, a new one every 25 ms) are all in use every few seconds,
# so the console keeps the 64 the engine shows (Effects.HalfRateSequence) at
# half size: 8 MB rather than 32. Their grain survives (mean error 1 of 255
# on a frame drawn over the screen).
SMALLER = {"bundles_tex_screen_fx_": 512}


def convert(src, dst, max_size):
    for prefix, cap in SMALLER.items():
        if os.path.basename(src).startswith(prefix):
            max_size = min(max_size, cap)
    with open(src, "rb") as f:
        head = f.read(28)
        magic, ver, fmt, w, h, mips, n = struct.unpack("<4s6I", head)
        data = f.read(n)
    if magic != b"CHTX" or n == 0 or w == 0 or h == 0:
        return None
    # Rows stay in Unity's order (bottom first): Vulkan's v = 0 is the first
    # stored row, which is where Unity's v = 0 is, so the UVs need no flip.
    img = decode(fmt, w, h, data)
    ow, oh = w, h
    while max(img.shape[0], img.shape[1]) > max_size:
        img = halve(img)
    sh, sw = img.shape[:2]
    alpha = img[..., 3]
    if fmt == 1 or sw * sh <= 64 * 64 or sw % 4 or sh % 4:
        kind = 3
        payload = img[..., [2, 1, 0, 3]].tobytes()   # BGRA
    elif alpha.min() == 255 or np.all((alpha == 0) | (alpha == 255)):
        kind = 1
        payload = encode(img, 1)
    else:
        kind = 2
        payload = encode(img, 2)
    tmp = dst + ".tmp"
    with open(tmp, "wb") as f:
        f.write(b"CTX1" + struct.pack("<6I", kind, sw, sh, ow, oh, len(payload)))
        f.write(payload)
    os.replace(tmp, dst)
    return kind, len(payload)


def halved(ctx):
    """A converted texture stored smaller than its source."""
    try:
        with open(ctx, "rb") as f:
            magic, kind, w, h, ow, oh, n = struct.unpack("<4s6I", f.read(28))
    except (OSError, struct.error):
        return False
    return magic == b"CTX1" and (w, h) != (ow, oh)


def below_normal():
    if os.name == "nt":
        import ctypes
        k32 = ctypes.windll.kernel32
        k32.SetPriorityClass(k32.GetCurrentProcess(), 0x4000)   # BELOW_NORMAL_PRIORITY_CLASS


def convert_job(job):
    src, dst, max_size = job
    try:
        return src, convert(src, dst, max_size), None
    except Exception as e:  # noqa: BLE001 - recorded, never silent
        return src, None, str(e)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", required=True)
    ap.add_argument("--only-missing", action="store_true")
    ap.add_argument("--max-size", type=int, default=2048)
    ap.add_argument("--keys", help="comma-separated texture keys (default: all)")
    ap.add_argument("--out", help="output folder (default <data>/ctex)")
    ap.add_argument("--halved-only", action="store_true",
                    help="only the textures <data>/ctex holds halved (the full-size atlas pages tools/cluster_console.py cuts up)")
    ap.add_argument("--workers", type=int, default=1, help="processes, at below-normal priority")
    a = ap.parse_args()
    src_dir = os.path.join(a.data, "textures")
    out_dir = a.out or os.path.join(a.data, "ctex")
    os.makedirs(out_dir, exist_ok=True)
    names = sorted(f for f in os.listdir(src_dir) if f.endswith(".tex"))
    if a.keys:
        want = set(k + ".tex" for k in a.keys.split(","))
        names = [n for n in names if n in want]
    if a.halved_only:
        names = [n for n in names if halved(os.path.join(a.data, "ctex", n[:-4] + ".ctx"))]
    todo = [n for n in names if not (a.only_missing and os.path.exists(os.path.join(out_dir, n[:-4] + ".ctx")))]
    jobs = [(os.path.join(src_dir, n), os.path.join(out_dir, n[:-4] + ".ctx"), a.max_size) for n in todo]
    counts, sizes, errors = {}, 0, []
    t0 = time.time()
    if a.workers > 1:
        import multiprocessing
        pool = multiprocessing.Pool(a.workers, initializer=below_normal)
        results = pool.imap(convert_job, jobs, chunksize=1)
    else:
        results = map(convert_job, jobs)
    for i, (n, r, err) in enumerate(results):
        if err:
            errors.append("%s: %s" % (os.path.basename(n), err))
        elif r is None:
            counts["empty"] = counts.get("empty", 0) + 1
        else:
            counts[r[0]] = counts.get(r[0], 0) + 1
            sizes += r[1]
        if (i + 1) % 100 == 0:
            print("  %d/%d textures, %.0f s" % (i + 1, len(jobs), time.time() - t0), flush=True)
    names_k = {1: "DXT1", 2: "DXT5", 3: "BGRA8", "empty": "empty (runtime)"}
    print("convert_console: %s; %.1f MB in %.0f s; %d errors" % (
        ", ".join("%s %d" % (names_k[k], v) for k, v in sorted(counts.items(), key=lambda kv: str(kv[0]))),
        sizes / 1048576, time.time() - t0, len(errors)))
    for e in errors[:30]:
        print("  error:", e)
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
