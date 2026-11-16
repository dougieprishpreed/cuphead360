#!/usr/bin/env python3
"""level_textures.py: how much texture memory each level needs at once, and the screen effects it runs.

    python tools/level_textures.py [--levels all|OldMan,FlyingBlimp,...] [--jobs 5] [--room 180] [--report]

Each level is played by the bot on host_native (hidden, muted; sweep_levels.py's forced script: every boss state and
a push into the next) with CH360_TRACE_DRAWN, each job from a root of its own (work/t34root_jobK: a junction to the
packs' GameData, its own save; its learned/ lists put back after). From the traces (work/leveltex/drawn_<Level>.txt):
the decoded bytes of the sprite pages a frame draws, and of those drawn within 2 s and 5 s (a looping animation's
frames all come round within its loop: what must be in memory together for the level to play without reading), against
the room the console's textures have (--room MB); and the image effects the level's cameras run besides the two every
scene has (EFFECT lines: a full-screen blur on a camera of its own is several passes of GPU time).
--report: only the table, from the traces already there. A level takes 1-3 minutes.
--cover: each play also writes what its sprites' triangles read (CH360_TRACE_COVER, work/leveltex/cover_<name>.bin),
and the scenes (maps, shops, menus, cutscenes: --scenes) are visited too; --merge-cover writes them all as one,
work/console/cover.bin, for tools/cluster_console.py --cover.
"""
import argparse, concurrent.futures, os, re, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import learned_lists as ll  # noqa: E402
import sweep_levels as sw  # noqa: E402
import texture_cache_sim as sim  # noqa: E402

OUT = os.path.join(ROOT, "work", "leveltex")
EVERY = ("BlurGamma on RenderCamera", "ChromaticAberrationFilmGrain on RenderCamera")


def play(level, root, fight, phase, cover=False, visit=2400, dump=0):
    if level in sw.LEVELS:
        n = sw.LEVELS.index(level)
        lo = sw.LOADOUTS[n % len(sw.LOADOUTS)]
        load, setup = sw.SETUPS.get(level, (level, ""))
        text = ll.LEVEL_SCRIPT.format(name=level, map="scene_map_world_1", primary=lo[0], secondary=lo[1], super=lo[2],
                                      charm=lo[3], mugman="on" if lo[4] == "mugman" else "off", mode="Normal", level=load,
                                      setup=setup, fight=fight, phase=phase)
        frames = 1800 + 900 + fight + 1500
    else:   # a scene ("name" or "name:quiet")
        scene, _, quiet = level.partition(":")
        text = ll.SCENE_SCRIPT.format(name=scene, map="scene_map_world_1", scene=scene, frames=visit, quiet=" quiet" if quiet else "")
        frames = 1800 + 900 + visit + 600
        level = scene
    save = os.path.join(root, "save_trace")
    shutil.rmtree(save, ignore_errors=True)
    shutil.copytree(os.path.join(ROOT, "work", "console", "pcroot", "save_pc"), save)
    with open(os.path.join(root, "bot_trace.txt"), "w", encoding="utf-8") as f:
        f.write(text)
    env = dict(os.environ, CH360_GUEST_ENV=ll.GUEST_ENV, CH360_TRACE_DRAWN=os.path.join(OUT, "drawn_%s.txt" % level))
    if cover:
        env["CH360_TRACE_COVER"] = os.path.join(OUT, "cover_%s.bin" % level)
    if dump:
        env["CH360_DUMP_DRAW"] = "%s:%d" % (os.path.join(OUT, "dump_%s.bin" % level), dump)
    t0 = time.time()
    with open(os.path.join(OUT, "trace_%s.log" % level), "w", encoding="utf-8") as f:
        subprocess.run([ll.HOST, "--root", root, "--frames", str(frames), "--", "run", "--console", "1",
                        "--data", "/GameData", "--save", "/save_trace", "--bot", "/bot_trace.txt"], stdout=f,
                       stderr=subprocess.STDOUT, cwd=ROOT, env=env, creationflags=0x08000000 if os.name == "nt" else 0)
    return time.time() - t0


def job(k, levels, a):
    root = os.path.join(ROOT, "work", "t34root_job%d" % k)
    if not os.path.isdir(os.path.join(root, "GameData")):
        sys.exit("no %s (tools/learned_lists.py --jobs makes the job roots)" % root)
    learned, keep = os.path.join(root, "learned"), os.path.join(root, "learned_kept")
    shutil.rmtree(keep, ignore_errors=True)
    if os.path.exists(learned):
        shutil.copytree(learned, keep)
    try:
        for level in levels:
            secs = play(level, root, a.fight, a.phase, a.cover, dump=a.dump)
            print("%-28s %4.0f s" % (level, secs), flush=True)
    finally:
        if os.path.exists(keep):
            shutil.rmtree(learned, ignore_errors=True)
            os.rename(keep, learned)


def measure(level, sizes):
    """(frames, one frame's median and most MB, 2 s p90 and most, 5 s p90 and most, distinct MB, pages) of the level's
    play frames."""
    path = os.path.join(OUT, "drawn_%s.txt" % level)
    if not os.path.exists(path):
        return None
    key, frames, marks = {}, [], []
    with open(path, encoding="utf-8", errors="replace") as f:
        for ln in f:
            c = ln[0]
            if c == "K":
                p = ln.split()
                if len(p) >= 3:
                    key[int(p[1])] = p[2]
            elif c == "S":
                p = ln.split()
                if p[2] == "1":
                    marks.append(int(p[1]))
            elif c == "F":
                p = ln.split()
                if p[2] == "0":
                    frames.append((int(p[1]), [int(x) for x in p[3:]]))
    if not frames:
        return None
    # the level: from the third load's start (the slots, the map, then the level the script asks for) to the next
    marks = sorted(set(marks)) + [frames[-1][0] + 1]
    if len(marks) < 4:
        return None
    frames = [(f, ids) for f, ids in frames if marks[2] <= f < marks[3]]
    if not frames:
        return None
    size = {}

    def sz(i):
        v = size.get(i)
        if v is None:
            k = key.get(i)
            r = sizes.get(sim.fnv64(k)) if k else None
            v = size[i] = r[1] if r else 0
        return v

    per = sorted(sum(sz(i) for i in set(ids)) for _, ids in frames)
    out = [len(frames), per[len(per) // 2], per[-1]]
    for w in (120, 300):
        last, tot, sums = {}, 0, []
        ring = []   # (frame index, id) in draw order, to expire
        for n, (_, ids) in enumerate(frames):
            for i in set(ids):
                if i not in last:
                    tot += sz(i)
                last[i] = n
            if n % 15 == 0:
                gone = [i for i, m in last.items() if n - m >= w]
                for i in gone:
                    tot -= sz(i)
                    del last[i]
                sums.append(tot)
        sums.sort()
        out += [sums[int(len(sums) * 0.9)], sums[-1]]
    pages = set(i for _, ids in frames for i in ids if sz(i))
    out += [sum(sz(i) for i in pages), len(pages)]
    return out


def effects(level):
    path = os.path.join(OUT, "trace_%s.log" % level)
    found = []
    if os.path.exists(path):
        with open(path, encoding="utf-8", errors="replace") as f:
            for ln in f:
                if ln.startswith("EFFECT ") and not any(e in ln for e in EVERY):
                    m = re.match(r"EFFECT (\S+ on [^:]+):(.*)", ln)
                    if m:
                        d = m.group(1)
                        ds = re.search(r"downsample=(\d+)", m.group(2))
                        it = re.search(r"blurIterations=(\d+)", m.group(2))
                        if ds or it:
                            d += " (%s%s)" % ("downsample %s" % ds.group(1) if ds else "", ", %s iterations" % it.group(1) if it else "")
                        if d not in found:
                            found.append(d)
    return found


def report(levels, room):
    sizes = sim.pack_sizes()
    rows = []
    for level in levels:
        m = measure(level, sizes)
        if m is not None:
            rows.append((level, m, effects(level)))
    mb = lambda b: b / 2**20
    rows.sort(key=lambda r: -r[1][4])
    print("%-26s %6s %12s %14s %14s %9s  %s" % ("level", "frames", "a frame MB", "within 2 s MB", "within 5 s MB", "all MB", "effects of its own"))
    print("%-26s %6s %12s %14s %14s %9s" % ("", "", "median/most", "p90/most", "p90/most", "(pages)"))
    over2 = over5 = 0
    for level, m, fx in rows:
        flag = "!!" if mb(m[3]) > room else "! " if mb(m[5]) > room else "  "
        over2 += mb(m[3]) > room
        over5 += mb(m[5]) > room and mb(m[3]) <= room
        print("%s %-23s %6d %5.0f /%5.0f %6.0f /%6.0f %6.0f /%6.0f %5.0f (%3d)  %s"
              % (flag, level, m[0], mb(m[1]), mb(m[2]), mb(m[3]), mb(m[4]), mb(m[5]), mb(m[6]), mb(m[7]), m[8], "; ".join(fx)))
    print("%d levels; !! %d draw more than the room (%d MB) within 2 s nine times in ten; ! %d more do within 5 s" % (len(rows), over2, room, over5))


def merge_cover(out):
    """Every cover_*.bin in OUT as one: the pages' grids OR'd, the drawn boxes put together."""
    import glob, struct
    grids, boxes = {}, set()
    n = 1024 * 1024 // 8
    for path in sorted(glob.glob(os.path.join(OUT, "cover_*.bin"))):
        d = open(path, "rb").read()
        p = 0
        while p + 8 <= len(d):
            hi, lo = struct.unpack_from("<II", d, p)
            if hi == 0xFFFFFFFF:
                count = lo
                p += 8
                for i in range(count):
                    boxes.add(d[p:p + 24])
                    p += 24
                break
            g = grids.get((hi, lo))
            chunk = d[p + 8:p + 8 + n]
            grids[(hi, lo)] = chunk if g is None else bytes(x | y for x, y in zip(g, chunk)) if False else (
                (int.from_bytes(g, "little") | int.from_bytes(chunk, "little")).to_bytes(n, "little"))
            p += 8 + n
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "wb") as f:
        for (hi, lo), g in sorted(grids.items()):
            f.write(struct.pack("<II", hi, lo))
            f.write(g)
        f.write(struct.pack("<II", 0xFFFFFFFF, len(boxes)))
        for b in sorted(boxes):
            f.write(b)
    print("cover: %d atlas pages, %d sprites drawn -> %s" % (len(grids), len(boxes), out))


# The console's GPU time a frame from its draw list, fitted to the forty-first run's measured scenes (SCENE GPU lines,
# 11 scenes from 5.0 to 14.2 ms, 0.2 ms off on average): a pass (a render target begun), a screen of pixels covered in
# render textures (the game's cameras draw into them; the screen's own fill is the last blit's, the same everywhere).
GPU_BASE, GPU_PASS, GPU_RT_SCREEN, GPU_DRAW = 1.06, 0.74, 0.228, 0.006


def gpu_frames(level):
    """[(frame, passes, render texture fill in screens, items, blits, effects of its own)] from the level's draw dumps."""
    import struct
    path = os.path.join(OUT, "dump_%s.bin" % level)
    if not os.path.exists(path):
        return None
    d = open(path, "rb").read()
    p, out = 0, []
    while p + 8 <= len(d):
        f, n = struct.unpack_from("<II", d, p)
        body = d[p + 8:p + 8 + n]
        p += 8 + n
        nv, ni, nx = struct.unpack_from("<III", body, 0)
        io = 12 + 56 * nv
        cur, passes, fill, items, blits = None, 0, 0.0, 0, 0
        for v in range(nv):
            x, y, w, h, clear, r, g, b, first, cnt, target, tw, th, a = struct.unpack_from("<4fI3fIIIIIf", body, 12 + 56 * v)
            if target != cur:
                passes += 1
                cur = target
            for i in range(first, min(first + cnt, ni)):
                tex, mode, vf, vc, x0, y0, x1, y1 = struct.unpack_from("<iIIIffff", body, io + 92 * i)
                if vc == 0:
                    continue
                items += 1
                if 11 <= mode <= 18:
                    blits += 1
                if target:
                    ww, hh = (tw or 1280), (th or 720)
                    fill += max(0.0, min(x1, ww) - max(x0, 0.0)) * max(0.0, min(y1, hh) - max(y0, 0.0)) / (1280.0 * 720.0)
        out.append((f, passes, fill, items, blits))
    return out


def level_range(level):
    """The level's own frames (from its drawn trace's scene marks: the third load's start to the next), or None."""
    path = os.path.join(OUT, "drawn_%s.txt" % level)
    if not os.path.exists(path):
        return None
    marks = []
    with open(path, encoding="utf-8", errors="replace") as f:
        for ln in f:
            if ln.startswith("S "):
                p = ln.split()
                if p[2] == "1":
                    marks.append(int(p[1]))
    marks = sorted(set(marks))
    return (marks[2], marks[3] if len(marks) > 3 else 1 << 30) if len(marks) > 2 else None


def gpu_report(levels):
    rows = []
    for level in levels:
        fr = gpu_frames(level)
        rng = level_range(level)
        if not fr or rng is None:
            continue
        play = [r for r in fr if rng[0] + 120 <= r[0] < rng[1]]
        if not play:
            continue
        est = [GPU_BASE + GPU_PASS * r[1] + GPU_RT_SCREEN * r[2] + GPU_DRAW * r[3] for r in play]
        k = len(play)
        rows.append((level, k, sum(r[1] for r in play) / k, sum(r[2] for r in play) / k, sum(r[3] for r in play) / k,
                     sum(r[4] for r in play) / k, sum(est) / k, sorted(est)[int(k * 0.95)] if k > 1 else est[0], max(est), effects(level)))
    rows.sort(key=lambda r: -r[7])
    print("%-26s %6s %7s %8s %6s %6s  %s" % ("level", "frames", "passes", "rt fill", "items", "blits", "GPU ms a frame, estimated: mean / 95% / most"))
    for level, k, ps, fl, it, bl, mean, p95, mx, fx in rows:
        flag = "!!" if p95 > 16.0 else "! " if p95 > 12.0 else "  "
        print("%s %-23s %6d %7.1f %8.1f %6.0f %6.1f  %5.1f / %5.1f / %5.1f  %s" % (flag, level, k, ps, fl, it, bl, mean, p95, mx, "; ".join(fx)))
    print("!! over 16 ms (a 60 Hz frame) in 5% of frames; ! over 12")


def phase_report(levels, warn=110, bad=135):
    """Per boss phase (the bot's "BOT phase" lines; a run-and-gun level in 20 s stretches): the MB of sprite pages drawn
    within any 2 s, median / 90% / most, and its largest atlases. Glumstone's first phase (median 149, 90% 182 at the
    forty-third run's ~195 MB of room) kept letting pages go it needed again; Hilda Berg, Cagney and Moonshine Mob's
    first phases (50-80) played smoothly. A phase whose median passes --warn or whose 90% passes --bad is marked."""
    import re, collections
    sizes = sim.pack_sizes()
    print("%-26s %-16s %6s  %s" % ("level", "phase", "frames", "MB within 2 s: median / 90% / most; largest atlases at 90%"))
    flagged = []
    for level in levels:
        path, log = os.path.join(OUT, "drawn_%s.txt" % level), os.path.join(OUT, "trace_%s.log" % level)
        if not os.path.exists(path) or not os.path.exists(log):
            continue
        rng = level_range(level)
        if rng is None:
            continue
        marks = []
        with open(log, encoding="utf-8", errors="replace") as f:
            for ln in f:
                m = re.match(r"BOT phase (\S+) at frame (\d+)", ln)
                if m:
                    marks.append((int(m.group(2)), m.group(1)))
                m = re.match(r"BOT won at frame (\d+)", ln)
                if m:
                    marks.append((int(m.group(1)), None))
        key, frames = {}, []
        with open(path, encoding="utf-8", errors="replace") as f:
            for ln in f:
                if ln[0] == "K":
                    q = ln.split()
                    if len(q) >= 3:
                        key[int(q[1])] = q[2]
                elif ln[0] == "F":
                    q = ln.split()
                    if q[2] == "0" and rng[0] <= int(q[1]) < rng[1]:
                        frames.append((int(q[1]), [int(x) for x in q[3:]]))
        if not frames:
            continue
        if not [m for m in marks if m[1]]:   # no phases: 20 s stretches
            f0, f1 = frames[0][0], frames[-1][0] + 1
            marks = [(f, "%d-%ds" % ((f - f0) // 60, (f - f0) // 60 + 20)) for f in range(f0, f1, 1200)] + [(f1, None)]
        marks.sort()
        size = {}

        def sz(i):
            v = size.get(i)
            if v is None:
                k = key.get(i)
                r = sizes.get(fnv(k)) if k else None
                v = size[i] = r[1] if r else 0
            return v

        for (a0, name), (a1, _) in zip(marks, marks[1:]):
            if name is None:
                continue
            fr = [x for x in frames if a0 + 60 <= x[0] < a1]
            last, sets = {}, []
            for n, (fno, ids) in enumerate(fr):
                for i in ids:
                    last[i] = n
                if n % 15 == 0 and n >= 120:
                    cur = [i for i, m in last.items() if n - m < 120]
                    sets.append((sum(sz(i) for i in cur), cur))
            if len(sets) < 4:
                continue
            sets.sort(key=lambda t: t[0])
            p50, p90, mx = sets[len(sets) // 2][0] / 2**20, sets[int(len(sets) * 0.9)][0] / 2**20, sets[-1][0] / 2**20
            g = collections.Counter()
            for i in sets[int(len(sets) * 0.9)][1]:
                m = re.match(r"bundles_atlas_(.*?)_CAB", key.get(i, ""))
                g[m.group(1) if m else "other"] += sz(i)
            mark = "!!" if p50 > warn or p90 > bad else "  "
            if mark == "!!":
                flagged.append((level, name, p50, p90))
            print("%s %-23s %-16s %6d  %4.0f / %4.0f / %4.0f  %s" % (mark, level, name[:16], len(fr), p50, p90, mx,
                  ", ".join("%s %.0f" % (k, v / 2**20) for k, v in g.most_common(3))))
    print("!! median over %d MB or 90%% over %d MB (Glumstone's first phase: 149 / 182 at ~195 MB of room): %d phases" % (warn, bad, len(flagged)))
    return flagged


def fnv(k):
    return sim.fnv64(k)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--levels", default="all")
    ap.add_argument("--jobs", type=int, default=5)
    ap.add_argument("--fight", type=int, default=18000)
    ap.add_argument("--phase", type=int, default=1200)
    ap.add_argument("--room", type=int, default=180, help="MB the console's textures have in a level")
    ap.add_argument("--report", action="store_true")
    ap.add_argument("--cover", action="store_true", help="also trace what the sprites' triangles read, and visit the scenes")
    ap.add_argument("--scenes", default="all", help="with --cover: the scenes visited (all, none or a list)")
    ap.add_argument("--merge-cover", action="store_true", help="only merge the cover traces into work/console/cover.bin")
    ap.add_argument("--dump", type=int, default=0, help="every this many frames, the draw list (CH360_DUMP_DRAW), for --gpu")
    ap.add_argument("--gpu", action="store_true", help="only the GPU table, from the draw dumps already there")
    ap.add_argument("--phases", action="store_true", help="only the per-phase table, from the traces already there")
    a = ap.parse_args()
    if a.merge_cover:
        merge_cover(os.path.join(ROOT, "work", "console", "cover.bin"))
        return
    if a.gpu:
        gpu_report(sw.LEVELS if a.levels == "all" else [x for x in a.levels.split(",") if x])
        return
    if a.phases:
        phase_report(sw.LEVELS if a.levels == "all" else [x for x in a.levels.split(",") if x])
        return
    levels = sw.LEVELS if a.levels == "all" else [x for x in a.levels.split(",") if x]
    if a.cover and a.scenes != "none":
        levels = levels + (sw.SCENES if a.scenes == "all" else [x for x in a.scenes.split(",") if x])
    os.makedirs(OUT, exist_ok=True)
    if not a.report:
        jobs = max(1, min(a.jobs, len(levels)))
        with concurrent.futures.ThreadPoolExecutor(jobs) as pool:
            for f in [pool.submit(job, k, levels[k::jobs], a) for k in range(jobs)]:
                f.result()
    if a.cover:
        merge_cover(os.path.join(ROOT, "work", "console", "cover.bin"))
    report([x for x in levels if x in sw.LEVELS], a.room)
    phase_report([x for x in levels if x in sw.LEVELS])
    if a.dump:
        gpu_report([x for x in levels if x in sw.LEVELS])


if __name__ == "__main__":
    main()
