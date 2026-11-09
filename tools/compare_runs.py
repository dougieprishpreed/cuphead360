#!/usr/bin/env python3
"""Console runs side by side, scene by scene: what a build changed.

    compare_runs.py hw24 hw25 hw26 hw27 [--dir work/console] [--scenes level,map,title,start]

Each run is work/console/<run>/cuphead360.txt. For every visit to a scene (in the order the run
made them) it prints the frame time spread (p50/p95/p99, the share of frames at 60 fps), hitches
per 1,000 frames, the game's and the host's parts, texture loads (on demand or read ahead, first
or again, from the drive or from memory), the memory (player, textures, free) and GC; then per run
the boot (scene_start and the title), audio underruns and the hitches by cause over the play
scenes. Reads only the logs.
"""
import argparse
import collections
import os
import re
import sys

PHASES = ("pump", "fixed", "physics", "update", "animators", "late", "ui", "end", "audio", "list", "submit")


def parse(path):
    lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
    visits = []          # scene summaries in order
    cur = None           # the texture/memory tally of the scene being played
    tally = collections.defaultdict(lambda: collections.Counter())
    seen = collections.defaultdict(set)
    scene = None
    loads_by_visit = []
    out = {"lines": lines, "visits": visits, "boot": {}, "underruns": 0, "holds": [], "audio_ahead": []}
    visit_no = collections.Counter()
    for i, ln in enumerate(lines):
        m = re.match(r"SCENE loaded (\S+)", ln)
        if m:
            scene = m.group(1)
            visit_no[scene] += 1
            cur = (scene, visit_no[scene])
            continue
        m = re.match(r"texture (\S+): format \d+, \d+x\d+, (\d+) KB \((\d+) KB stored\), (read and decode|decoded from memory in) (\d+) ms on the loader, (prefetched, )?ready (\d+) ms", ln)
        if m and cur:
            t = tally[cur]
            first = m.group(1) not in seen[cur[0]]
            seen[cur[0]].add(m.group(1))
            t["loads"] += 1
            t["MB"] += int(m.group(2)) / 1024
            t["prefetched" if m.group(6) else "on demand"] += 1
            t["first" if first else "again"] += 1
            if m.group(4).startswith("decoded"):
                t["from memory"] += 1
            t["late ms"] += int(m.group(7))
            continue
        m = re.match(r"SCENE SUMMARY (\S+): (\d+) frames in ([\d.]+) s \(([\d.]+) fps\); between frames p50 (\S+) p95 (\S+) p99 (\S+) max (\S+) ms, 60fps (\d+)% 30fps (\d+)% 20fps (\d+)% slower (\d+)%; game ([\d.]+) ms/frame \(([^)]*)\).*?GC (\d+)/(\d+)/(\d+) paused (\d+) ms.*?hitches (\d+); heap (\d+) MB", ln)
        if m:
            name = m.group(1)
            v = {
                "scene": name, "visit": visit_no[name], "frames": int(m.group(2)), "fps": float(m.group(4)),
                "p50": m.group(5), "p95": m.group(6), "p99": m.group(7), "max": m.group(8),
                "f60": int(m.group(9)), "f30": int(m.group(10)), "slower": int(m.group(12)), "game": float(m.group(13)),
                "phases": {k: float(x) for k, x in re.findall(r"(\w+)=([\d.]+)", m.group(14))},
                "gc": (int(m.group(15)), int(m.group(16)), int(m.group(17)), int(m.group(18))),
                "hitches": int(m.group(19)), "heap": int(m.group(20)),
                "tex": tally.get((name, visit_no[name]), collections.Counter()),
            }
            for j in range(i + 1, min(i + 4, len(lines))):
                g = re.match(r"SCENE GPU \S+: ([\d.]+) ms/frame.*?host part ([\d.]+) ms/frame \(texture loads ([\d.]+), uploads ([\d.]+);.*?audio underruns (\d+); textures (\d+) MB, (\d+) MB free", lines[j])
                if g:
                    v["gpu"], v["host"], v["loads_ms"], v["uploads_ms"] = (float(g.group(k)) for k in range(1, 5))
                    v["underruns"], v["tex_mb"], v["free_mb"] = int(g.group(5)), int(g.group(6)), int(g.group(7))
                    # without the per-texture lines (the default since the twenty-eighth run): the host's counts
                    c = re.search(r"(\d+) loaded on demand, (\d+) read again", lines[j])
                    n = re.search(r"; (\d+) textures loaded\)", lines[j])
                    if c and n and v["tex"]["loads"] == 0:
                        v["tex"] = collections.Counter({"loads": int(n.group(1)), "on demand": int(c.group(1)), "again": int(c.group(2))})
            visits.append(v)
            continue
        m = re.match(r"(BOOT|LEVEL) hold.*?: (\d+) frames, (\d+) ms", ln)
        if m:
            out["holds"].append(ln)
            continue
        if ln.startswith("audio: ") and "read ahead" in ln:
            out["audio_ahead"].append(ln)
        m = re.search(r"player (\d+) MB, textures (\d+) MB", ln)
        if m and ln.startswith("alive"):
            out.setdefault("player_max", 0)
            out["player_max"] = max(out["player_max"], int(m.group(1)))
    return out


def label(v):
    s = v["scene"].replace("scene_level_", "").replace("scene_", "")
    return "%s#%d" % (s, v["visit"])


def row(v):
    t = v["tex"]
    ph = v["phases"]
    per_k = 1000.0 * v["hitches"] / max(1, v["frames"])
    return ("%5d fr %5.1f fps 60%%=%3d p95 %4s p99 %4s hitch/1k %5.1f | game %5.1f (fix %4.1f upd %4.1f anim %4.1f ui %4.1f list %4.1f submit %5.1f) "
            "gpu %4.1f host %4.1f (loads %4.1f) | tex %3d MB free %3d heap %3d | loads %3d (demand %3d, again %3d, mem %3d) GC %d/%d/%d %d ms" % (
                v["frames"], v["fps"], v["f60"], v["p95"], v["p99"], per_k, v["game"], ph.get("fixed", 0), ph.get("update", 0),
                ph.get("animators", 0), ph.get("ui", 0), ph.get("list", 0), ph.get("submit", 0), v.get("gpu", 0), v.get("host", 0),
                v.get("loads_ms", 0), v.get("tex_mb", 0), v.get("free_mb", 0), v["heap"], t["loads"], t["on demand"], t["again"],
                t["from memory"], v["gc"][0], v["gc"][1], v["gc"][2], v["gc"][3]))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("runs", nargs="+")
    ap.add_argument("--dir", default=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "work", "console"))
    ap.add_argument("--scenes", default="level,map,title,start,slot")
    a = ap.parse_args()
    keys = a.scenes.split(",")
    runs = [(r, parse(os.path.join(a.dir, r, "cuphead360.txt"))) for r in a.runs]
    names = []
    for _, p in runs:
        for v in p["visits"]:
            if any(k in v["scene"] for k in keys) and v["scene"] not in names:
                names.append(v["scene"])
    for name in names:
        print("== %s" % name)
        for r, p in runs:
            for v in p["visits"]:
                if v["scene"] == name:
                    print("  %-5s %-16s %s" % (r, label(v), row(v)))
    print()
    for r, p in runs:
        play = [v for v in p["visits"] if "level_" in v["scene"] or "map_" in v["scene"]]
        fr = sum(v["frames"] for v in play)
        hi = sum(v["hitches"] for v in play)
        un = sum(v.get("underruns", 0) for v in p["visits"])
        print("%-5s play scenes: %d frames, %d hitches (%.1f per 1,000); audio underruns %d; player memory at most %s MB" % (
            r, fr, hi, 1000.0 * hi / max(1, fr), un, p.get("player_max")))
        for h in p["holds"]:
            print("      " + h[:160])
        for h in p["audio_ahead"][:6]:
            print("      " + h[:160])
    return 0


if __name__ == "__main__":
    sys.exit(main())
