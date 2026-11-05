#!/usr/bin/env python3
"""A console run's performance by scene, from its log (cuphead360.txt).

    perf_report.py work/console/hwNN/cuphead360.txt [--top 8]

Reads what the console build logs for this (ConsoleHost.PerfLog, console/xsdk/main.cpp):
SCENE SUMMARY / GPU / TOP at each scene change; FRAMES, TOP and the host's HOST and GPU
lines every 300 frames (the fallback for a scene the run ended in); HITCH lines (a frame
over 50 ms) and HOST SLOW lines (a host part over 12 ms).

Prints a table per scene (time between frames, fps shares, the game's phases, scripts,
GC, the GPU by kind of pass, fill, the host's part), what bounds each scene (the game
on the CPU, the GPU, the host, or hitches), the hitches by cause, and the costliest
scripts over the run weighted by frames.
"""
import argparse, collections, re, sys

NUM = r"(-?[\d.]+)"


def floats(pattern, text):
    m = re.search(pattern, text)
    return [float(x) for x in m.groups()] if m else None


def parse_phases(text):
    return {k: float(v) for k, v in re.findall(r"\b(pump|fixed|physics|update|animators|late|ui|end|audio|list|submit)=([\d.]+)", text)}


def parse_top(text):
    return [(name, float(ms)) for name, ms in re.findall(r"([\w.`<>+\[\]]+) ([\d.]+)(?:,|$)", text)]


def scene_rows(lines):
    rows = collections.OrderedDict()
    order = 0
    for ln in lines:
        m = re.match(r"SCENE SUMMARY (\S+): (\d+) frames in ([\d.]+) s \(([\d.]+) fps\); (.*)", ln)
        if m:
            name = m.group(1)
            order += 1
            key = "%s#%d" % (name, order)
            rest = m.group(5)
            r = rows.setdefault(key, {"scene": name})
            r["frames"] = int(m.group(2)); r["secs"] = float(m.group(3)); r["fps"] = float(m.group(4))
            p = re.search(r"p50 (\S+) p95 (\S+) p99 (\S+) max (\S+) ms, 60fps (\d+)% 30fps (\d+)% 20fps (\d+)% slower (\d+)%", rest)
            if p:
                r.update(p50=p.group(1), p95=p.group(2), p99=p.group(3), max=p.group(4), s60=int(p.group(5)), s30=int(p.group(6)), s20=int(p.group(7)), slower=int(p.group(8)))
            g = floats(r"game ([\d.]+) ms/frame", rest)
            r["game"] = g[0] if g else 0
            r["phases"] = parse_phases(rest)
            s = floats(r"scripts ([\d.]+)", rest)
            r["scripts"] = s[0] if s else 0
            gc = re.search(r"GC (\d+)/(\d+)/(\d+) paused (\d+) ms", rest)
            r["gc"] = (int(gc.group(1)), int(gc.group(2)), int(gc.group(3)), int(gc.group(4))) if gc else (0, 0, 0, 0)
            a = floats(r"allocated ([\d.]+) MB/frame", rest)
            r["alloc"] = a[0] if a else None
            h = floats(r"hitches (\d+)", rest)
            r["hitches"] = int(h[0]) if h else 0
            hb = re.search(r"heap (\d+) MB, (\d+) behaviours", rest)
            if hb:
                r["heap"], r["behaviours"] = int(hb.group(1)), int(hb.group(2))
            continue
        m = re.match(r"SCENE GPU (\S+): ([\d.]+) ms/frame \(render textures ([\d.]+), effects ([\d.]+), screen ([\d.]+); (\d+) frames timed\); ([\d.]+) passes, (\d+) draws a frame; "
                     r"fill ([\d.]+) screens \+ ([\d.]+) in render textures; host part ([\d.]+) ms/frame \(texture loads ([\d.]+), uploads ([\d.]+); (\d+) textures loaded\), "
                     r"fence ([\d.]+), present ([\d.]+); (\d+) slow host parts; audio underruns (\d+)", ln)
        if m and rows:
            key = next(reversed(rows))
            v = [float(x) for x in m.groups()[1:]]
            rows[key].update(gpu=v[0], gpu_rt=v[1], gpu_fx=v[2], gpu_screen=v[3], gpu_timed=int(v[4]), passes=v[5], draws=v[6], fill=v[7], fill_rt=v[8],
                             host=v[9], loads=v[10], uploads=v[11], textures=int(v[12]), fence=v[13], present=v[14], host_slow=int(v[15]), underruns=int(v[16]))
            continue
        m = re.match(r"SCENE TOP (\S+) ms/frame:(.*)", ln)
        if m and rows:
            rows[next(reversed(rows))]["top"] = parse_top(m.group(2))
    return list(rows.values())


def window_fallback(lines, after_frame):
    """FRAMES/GPU windows after the last summary (the scene the run ended in), added up."""
    frames, gpu = [], {}
    for ln in lines:
        m = re.match(r"FRAMES (\d+)-(\d+): ([\d.]+) ms/frame; (.*)", ln)
        if m and int(m.group(1)) > after_frame:
            frames.append((int(m.group(1)), int(m.group(2)), float(m.group(3)), m.group(4)))
        m = re.match(r"GPU frames (\d+)-(\d+): ([\d.]+) ms/frame \(render textures ([\d.]+), effects ([\d.]+), screen ([\d.]+); (\d+) frames timed\); ([\d.]+) passes a frame; "
                     r"fill ([\d.]+) screens \+ ([\d.]+)", ln)
        if m:
            gpu[int(m.group(1))] = [float(x) for x in m.groups()[2:]]
    if not frames:
        return None
    n = len(frames)
    scenes = collections.Counter()
    for _, _, _, rest in frames:
        scenes[rest.rsplit("; ", 1)[-1]] += 1
    r = {"scene": scenes.most_common(1)[0][0] + " (windows)", "frames": 300 * n, "secs": 0, "fps": 0, "game": sum(f[2] for f in frames) / n}
    ph = collections.Counter()
    for f in frames:
        for k, v in parse_phases(f[3]).items():
            ph[k] += v / n
    r["phases"] = dict(ph)
    # The windows' spreads averaged (percentiles of 300-frame windows, not of the whole stretch).
    spreads = [re.search(r"p50 (\d+) p95 (\d+) p99 (\d+) max (\S+) ms, 60fps (\d+)% 30fps (\d+)% 20fps (\d+)% slower (\d+)%", f[3]) for f in frames]
    spreads = [s for s in spreads if s]
    if spreads:
        avg = lambda i: round(sum(float(s.group(i)) for s in spreads) / len(spreads))
        r.update(p50=avg(1), p95=avg(2), p99=avg(3), s60=avg(5), s30=avg(6), s20=avg(7), slower=avg(8))
    g = [gpu[f[0]] for f in frames if f[0] in gpu]
    if g:
        r.update(gpu=sum(x[0] for x in g) / len(g), gpu_rt=sum(x[1] for x in g) / len(g), gpu_fx=sum(x[2] for x in g) / len(g),
                 gpu_screen=sum(x[3] for x in g) / len(g), passes=sum(x[5] for x in g) / len(g), fill=sum(x[6] for x in g) / len(g), fill_rt=sum(x[7] for x in g) / len(g))
    return r


def bound(r):
    """What bounds a scene: the slowest of the game (CPU), the GPU and the host's part, against a 60 fps frame."""
    parts = {"game (CPU)": r.get("game", 0), "GPU": r.get("gpu", 0), "host": r.get("host", 0)}
    name, ms = max(parts.items(), key=lambda kv: kv[1])
    if ms <= 16.7:
        return "fits 60" + (" (hitches)" if r.get("hitches", 0) else "")
    return "%s %.1f ms" % (name, ms)


def hitch_causes(lines):
    """HITCH lines by the part that took most of them, per scene."""
    by_scene = collections.defaultdict(collections.Counter)
    worst = []
    for ln in lines:
        m = re.match(r"HITCH frame (\d+): (\d+) ms to the next frame; game ([\d.]+) \(([^)]*)\) list ([\d.]+) submit ([\d.]+), host and vblank ([\d.]+) ms(.*)", ln)
        if not m:
            continue
        total, game, host = float(m.group(2)), float(m.group(3)), float(m.group(7))
        rest = m.group(8)
        scene = rest.rsplit("; ", 1)[-1]
        ph = parse_phases(m.group(4))
        ph["list"] = float(m.group(5)); ph["submit"] = float(m.group(6)); ph["host/vblank"] = host
        gc = re.search(r"GC (\d+)/(\d+)/(\d+) paused (\d+) ms", rest)
        if gc and int(gc.group(4)) > 0.3 * total:
            cause = "GC pause"
        elif "a scene activated" in rest:
            cause = "scene activation"
        else:
            cause = max(ph.items(), key=lambda kv: kv[1])[0]
        by_scene[scene][cause] += 1
        worst.append((total, int(m.group(1)), scene, cause))
    worst.sort(reverse=True)
    return by_scene, worst


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("log")
    ap.add_argument("--top", type=int, default=8, help="scripts listed per scene and over the run")
    a = ap.parse_args()
    lines = open(a.log, encoding="utf-8", errors="replace").read().splitlines()
    rows = scene_rows(lines)
    summarized = sum(r["frames"] for r in rows)
    fb = window_fallback(lines, summarized)
    if fb and fb["frames"] > 0:
        rows.append(fb)

    print("%-34s %6s %6s %5s %5s %5s | %4s %4s %4s %4s | %5s %5s %5s %5s %5s %5s | %5s %5s %5s %5s %5s | %5s %5s %5s | %-18s" % (
        "scene", "frames", "fps", "p50", "p95", "p99", "60%", "30%", "20%", "slow", "game", "upd", "fixed", "phys", "anim", "list",
        "gpu", "rt", "fx", "scrn", "fill", "host", "loads", "GCms", "bound by"))
    for r in rows:
        ph = r.get("phases", {})
        print("%-34s %6d %6.1f %5s %5s %5s | %4s %4s %4s %4s | %5.1f %5.1f %5.1f %5.1f %5.1f %5.1f | %5.1f %5.1f %5.1f %5.1f %5.2f | %5.1f %5.1f %5s | %-18s" % (
            r["scene"][:34], r.get("frames", 0), r.get("fps", 0), r.get("p50", "-"), r.get("p95", "-"), r.get("p99", "-"),
            r.get("s60", "-"), r.get("s30", "-"), r.get("s20", "-"), r.get("slower", "-"), r.get("game", 0), ph.get("update", 0), ph.get("fixed", 0),
            ph.get("physics", 0), ph.get("animators", 0), ph.get("list", 0), r.get("gpu", 0), r.get("gpu_rt", 0), r.get("gpu_fx", 0), r.get("gpu_screen", 0),
            r.get("fill", 0), r.get("host", 0), r.get("loads", 0), r.get("gc", (0, 0, 0, "-"))[3], bound(r)))

    print("\nMemory and objects at each scene's end:")
    for r in rows:
        if "heap" in r or "textures" in r:
            gc = r.get("gc", ("-",) * 4)
            print("  %-34s heap %s MB, %s behaviours; GC %s/%s/%s paused %s ms, %s MB allocated a frame; %s textures loaded, %s slow host parts, %s audio underruns" % (
                r["scene"][:34], r.get("heap", "-"), r.get("behaviours", "-"), gc[0], gc[1], gc[2], gc[3],
                r.get("alloc", "-"), r.get("textures", "-"), r.get("host_slow", "-"), r.get("underruns", "-")))

    print("\nScripts per scene (ms/frame):")
    for r in rows:
        if r.get("top"):
            print("  %-34s %s" % (r["scene"][:34], ", ".join("%s %.2f" % t for t in r["top"][:a.top])))

    weighted = collections.Counter()
    for r in rows:
        for name, ms in r.get("top", []):
            weighted[name] += ms * r.get("frames", 0)
    total = sum(r.get("frames", 0) for r in rows if r.get("top")) or 1
    if weighted:
        print("\nScripts over the run (ms/frame averaged over summarized frames):")
        print("  " + ", ".join("%s %.2f" % (n, v / total) for n, v in weighted.most_common(a.top * 2)))

    by_scene, worst = hitch_causes(lines)
    if by_scene:
        print("\nHitches (frames over 50 ms) by cause:")
        for scene, c in by_scene.items():
            print("  %-34s %s" % (scene[:34], ", ".join("%s %d" % kv for kv in c.most_common())))
        print("  worst: " + "; ".join("%d ms at frame %d (%s, %s)" % w for w in worst[:8]))

    slow = collections.Counter()
    for ln in lines:
        m = re.match(r"HOST SLOW frame \d+: [\d.]+ ms: boxes\+loads ([\d.]+) \(\d+ textures\), fence ([\d.]+), uploads ([\d.]+), acquire ([\d.]+), record ([\d.]+), submit ([\d.]+)", ln)
        if m:
            parts = dict(zip(["texture loads", "GPU fence", "uploads", "acquire", "record", "submit"], [float(x) for x in m.groups()]))
            slow[max(parts.items(), key=lambda kv: kv[1])[0]] += 1
    if slow:
        print("\nSlow host parts (over 12 ms, six said a window) by cause: " + ", ".join("%s %d" % kv for kv in slow.most_common()))
    if not rows:
        print("no SCENE SUMMARY or FRAMES lines: a log from before the per-scene summaries?", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
