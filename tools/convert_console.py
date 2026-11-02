#!/usr/bin/env python3
"""Console textures: re-encode the exported textures for the Xbox 360 GPU.

    convert_console.py --data <GameData dir> [--only-missing] [--max-size N] [--keys a,b]

Reads <data>/textures/*.tex (tools/convert_textures.py) and writes
<data>/ctex/<key>.ctx, the format the console host loads. Xenos has no BC7,
so every texture is decoded (mip 0) and stored as DXT1 (alpha only 0/255),
DXT5 (partial alpha) or BGRA8 (small, single-channel or font textures).
Pages larger than --max-size (default 2048) are halved until they fit: the
game's atlases are 4096 px for 1080p and up, the console draws 720p. UVs
are normalized, so nothing else changes.

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
    h, w = img.shape[:2]
    img = img[:h - h % 2, :w - w % 2].astype(np.uint16)
    return ((img[0::2, 0::2] + img[1::2, 0::2] + img[0::2, 1::2] + img[1::2, 1::2] + 2) // 4).astype(np.uint8)


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
    bl = blocks(pad4(img))
    if kind == 1:
        punch = bl[..., 3] < 128
        return color_block(bl, punch if punch.any() else None).tobytes()
    return np.concatenate([alpha_block(bl), color_block(bl, None)], axis=1).tobytes()


def convert(src, dst, max_size):
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


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", required=True)
    ap.add_argument("--only-missing", action="store_true")
    ap.add_argument("--max-size", type=int, default=2048)
    ap.add_argument("--keys", help="comma-separated texture keys (default: all)")
    a = ap.parse_args()
    src_dir = os.path.join(a.data, "textures")
    out_dir = os.path.join(a.data, "ctex")
    os.makedirs(out_dir, exist_ok=True)
    names = sorted(f for f in os.listdir(src_dir) if f.endswith(".tex"))
    if a.keys:
        want = set(k + ".tex" for k in a.keys.split(","))
        names = [n for n in names if n in want]
    counts, sizes, errors = {}, 0, []
    t0 = time.time()
    for i, n in enumerate(names):
        dst = os.path.join(out_dir, n[:-4] + ".ctx")
        if a.only_missing and os.path.exists(dst):
            continue
        try:
            r = convert(os.path.join(src_dir, n), dst, a.max_size)
            if r is None:
                counts["empty"] = counts.get("empty", 0) + 1
                continue
            counts[r[0]] = counts.get(r[0], 0) + 1
            sizes += r[1]
        except Exception as e:  # noqa: BLE001 - recorded, never silent
            errors.append("%s: %s" % (n, e))
        if (i + 1) % 100 == 0:
            print("  %d/%d textures, %.0f s" % (i + 1, len(names), time.time() - t0), flush=True)
    names_k = {1: "DXT1", 2: "DXT5", 3: "BGRA8", "empty": "empty (runtime)"}
    print("convert_console: %s; %.1f MB in %.0f s; %d errors" % (
        ", ".join("%s %d" % (names_k[k], v) for k, v in sorted(counts.items(), key=lambda kv: str(kv[0]))),
        sizes / 1048576, time.time() - t0, len(errors)))
    for e in errors[:30]:
        print("  error:", e)
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
