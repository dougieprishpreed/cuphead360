#!/usr/bin/env python3
"""texture_density.py <drawn trace> [...]: how many screen pixels a texel of each drawn texture covered at most.

The traces come from host_native with CH360_TRACE_DRAWN (their "D id du dv" lines: the most screen pixels, at
1280x720, a unit of the texture's u and v was drawn across); the textures' sizes from work/GameData/ctexc and ctex.
Prints the bytes of the drawn pages by density (1.0: a texel a screen pixel; under it the page holds detail the screen
never shows) and what the set would take with each page scaled to what it was drawn at.
"""
import os, struct, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIRS = [os.path.join(ROOT, "work", "GameData", d) for d in ("ctexc", "ctex")]


def header(key):
    for d in DIRS:
        p = os.path.join(d, key + ".ctx")
        if os.path.exists(p):
            with open(p, "rb") as f:
                h = f.read(28)
            if len(h) == 28 and h[:4] == b"CTX1":
                fmt, w, hh = struct.unpack_from("<3I", h, 4)
                return fmt, w, hh, struct.unpack_from("<I", h, 24)[0]
    return None


def read(paths):
    best = {}   # key -> (density u, density v) in pixels a texel
    for path in paths:
        key, d = {}, {}
        with open(path, encoding="utf-8", errors="replace") as f:
            for ln in f:
                if ln[0] == "K":
                    p = ln.split()
                    if len(p) >= 3:
                        key[int(p[1])] = p[2]
                elif ln[0] == "D":
                    p = ln.split()
                    d[int(p[1])] = (float(p[2]), float(p[3]))
        for i, (du, dv) in d.items():
            k = key.get(i)
            if k is None:
                continue
            a = best.get(k, (0.0, 0.0))
            best[k] = (max(a[0], du), max(a[1], dv))
    return best


def main():
    best = read(sys.argv[1:])
    rows = []
    for k, (du, dv) in best.items():
        h = header(k)
        if h is None:
            continue
        fmt, w, hh, n = h
        rows.append((max(du / w, dv / hh), n, k, w, hh, fmt))
    total = sum(r[1] for r in rows)
    print("%d drawn textures with a size, %.0f MB" % (len(rows), total / 2**20))
    edges = [0.34, 0.5, 0.67, 0.8, 0.95, 1.05, 1.25, 1.5, 2.0, 1e9]
    lo = 0.0
    for e in edges:
        b = [r for r in rows if lo <= r[0] < e]
        print("  %4.2f-%-5s px a texel: %5d pages, %7.1f MB" % (lo, ("%.2f" % e) if e < 1e8 else "", len(b), sum(r[1] for r in b) / 2**20))
        lo = e
    for margin in (1.0, 1.15):
        scaled = 0
        for d, n, k, w, hh, fmt in rows:
            s = min(1.0, d * margin)
            scaled += n * s * s
        print("each page scaled to %.2fx what it was drawn at (never up): %.0f MB (%.0f%%)" % (margin, scaled / 2**20, 100.0 * scaled / total))
    rows.sort(key=lambda r: -r[1] * (1 - min(1.0, r[0]) ** 2))
    for d, n, k, w, hh, fmt in rows[:12]:
        print("  %.2f px a texel  %4dx%-4d %5.1f MB  %s" % (d, w, hh, n / 2**20, k[-70:]))


if __name__ == "__main__":
    main()
