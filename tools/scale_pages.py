#!/usr/bin/env python3
"""scale_pages.py: smaller copies of chosen levels' sprite pages, for levels whose art doesn't fit the console's memory.

    python tools/scale_pages.py --data work/GameData --atlas oldman --scale 75,85 [--workers 2]

Glumstone's fight draws about 150 MB of sprite pages within any two seconds of its first phase (182 at the 90th
percentile) against the ~195 MB the console's textures have in a level, so it keeps letting pages go that it needs
again a moment later; the levels that play smoothly draw 50-80 MB within two seconds. Its pages stored smaller (75%:
56% of the memory) bring that set well under the room.

Each sprite page (<data>/ctexc, tools/cluster_console.py) whose key holds "atlas_<name>_" for a --atlas name is
decoded, resampled as a whole to the scale (Pillow's bicubic, on premultiplied colour: it reaches 2.7 texels of the
full page, inside the 4-texel gap cluster_console.py leaves between two sprites' texels, so no sprite takes a
neighbour's colour), encoded again in its own format (convert_console.py's encoder) and written to
<data>/ctexs/<key>_s<scale>.ctx. UVs are normalized, so ctexmap.bin holds for every scale. tools/stage_console.py puts
them in the texture pack beside the full pages; GameData/artscale.txt (staged from console/artscale.txt) and an
artscale.txt next to default.xex choose which scale a level's atlases draw from (console/ch360_imports.c, art_scale).
Deterministic; a page whose output is up to date is skipped.
"""
import argparse, multiprocessing, os, struct, sys, time

import numpy as np
import texture2ddecoder
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import convert_console as cc  # noqa: E402


def scale_one(job):
    src, dst, scale = job
    try:
        with open(src, "rb") as f:
            magic, kind, w, h, ow, oh, n = struct.unpack("<4s6I", f.read(28))
            data = f.read(n)
        if magic != b"CTX1" or kind not in (1, 2):
            return src, None, "not a DXT page"
        raw = texture2ddecoder.decode_bc1(data, w, h) if kind == 1 else texture2ddecoder.decode_bc3(data, w, h)
        img = np.frombuffer(raw, np.uint8).reshape(h, w, 4)[..., [2, 1, 0, 3]]
        nw, nh = max(4, int(round(w * scale / 400.0)) * 4), max(4, int(round(h * scale / 400.0)) * 4)
        small = np.asarray(Image.fromarray(np.ascontiguousarray(img), "RGBA").resize((nw, nh), Image.BICUBIC))
        payload = cc.encode(small, kind)
        head = b"CTX1" + struct.pack("<6I", kind, nw, nh, w, h, len(payload))
        with open(dst + ".tmp", "wb") as f:
            f.write(head)
            f.write(payload)
        os.replace(dst + ".tmp", dst)
        return src, (w * h, nw * nh, len(payload)), None
    except Exception as e:  # noqa: BLE001 - reported
        return src, None, str(e)


def below_normal():
    if os.name == "nt":
        import ctypes
        k32 = ctypes.windll.kernel32
        k32.SetPriorityClass(k32.GetCurrentProcess(), 0x4000)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", required=True)
    ap.add_argument("--atlas", required=True, help="comma-separated atlas names (oldman: atlas_oldman_*)")
    ap.add_argument("--scale", default="75", help="comma-separated percentages")
    ap.add_argument("--workers", type=int, default=2)
    a = ap.parse_args()
    src_dir, out_dir = os.path.join(a.data, "ctexc"), os.path.join(a.data, "ctexs")
    os.makedirs(out_dir, exist_ok=True)
    names = [x for x in a.atlas.split(",") if x]
    scales = [int(x) for x in a.scale.split(",") if x]
    pages = sorted(f for f in os.listdir(src_dir) if f.endswith(".ctx") and any("atlas_%s_" % n in f for n in names))
    jobs, want = [], set()
    for s in scales:
        for f in pages:
            src = os.path.join(src_dir, f)
            dst = os.path.join(out_dir, "%s_s%d.ctx" % (f[:-4], s))
            want.add(os.path.basename(dst))
            if os.path.exists(dst) and os.path.getmtime(dst) >= os.path.getmtime(src):
                continue
            jobs.append((src, dst, s))
    # scaled pages of atlases or scales no longer asked for
    for f in os.listdir(out_dir):
        if f.endswith(".ctx") and f not in want and any("atlas_%s_" % n in f for n in names):
            os.remove(os.path.join(out_dir, f))
    t0 = time.time()
    pool = multiprocessing.Pool(a.workers, initializer=below_normal) if a.workers > 1 else None
    results = pool.imap(scale_one, jobs, chunksize=1) if pool else map(scale_one, jobs)
    full = small = 0
    errors = []
    for i, (src, r, err) in enumerate(results):
        if err:
            errors.append("%s: %s" % (os.path.basename(src), err))
        else:
            full += r[0]
            small += r[1]
        if (i + 1) % 50 == 0:
            print("  %d/%d pages, %.0f s" % (i + 1, len(jobs), time.time() - t0), flush=True)
    for e in errors[:10]:
        print("  " + e)
    print("scale_pages: %d pages of %s at %s%%: %d made (%.0f -> %.0f Mtexels), %d errors, %.0f s" % (
        len(pages), ",".join(names), ",".join(map(str, scales)), len(jobs), full / 1e6, small / 1e6, len(errors), time.time() - t0))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
