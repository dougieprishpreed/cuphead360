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
"""
import argparse, concurrent.futures, os, re, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import learned_lists as ll  # noqa: E402
import sweep_levels as sw  # noqa: E402
import texture_cache_sim as sim  # noqa: E402

OUT = os.path.join(ROOT, "work", "leveltex")
EVERY = ("BlurGamma on RenderCamera", "ChromaticAberrationFilmGrain on RenderCamera")


def play(level, root, fight, phase):
    n = sw.LEVELS.index(level)
    lo = sw.LOADOUTS[n % len(sw.LOADOUTS)]
    load, setup = sw.SETUPS.get(level, (level, ""))
    text = ll.LEVEL_SCRIPT.format(name=level, map="scene_map_world_1", primary=lo[0], secondary=lo[1], super=lo[2],
                                  charm=lo[3], mugman="on" if lo[4] == "mugman" else "off", mode="Normal", level=load,
                                  setup=setup, fight=fight, phase=phase)
    save = os.path.join(root, "save_trace")
    shutil.rmtree(save, ignore_errors=True)
    shutil.copytree(os.path.join(ROOT, "work", "console", "pcroot", "save_pc"), save)
    with open(os.path.join(root, "bot_trace.txt"), "w", encoding="utf-8") as f:
        f.write(text)
    env = dict(os.environ, CH360_GUEST_ENV=ll.GUEST_ENV, CH360_TRACE_DRAWN=os.path.join(OUT, "drawn_%s.txt" % level))
    t0 = time.time()
    with open(os.path.join(OUT, "trace_%s.log" % level), "w", encoding="utf-8") as f:
        subprocess.run([ll.HOST, "--root", root, "--frames", str(1800 + 900 + fight + 1500), "--", "run", "--console", "1",
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
            secs = play(level, root, a.fight, a.phase)
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


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--levels", default="all")
    ap.add_argument("--jobs", type=int, default=5)
    ap.add_argument("--fight", type=int, default=18000)
    ap.add_argument("--phase", type=int, default=1200)
    ap.add_argument("--room", type=int, default=180, help="MB the console's textures have in a level")
    ap.add_argument("--report", action="store_true")
    a = ap.parse_args()
    levels = sw.LEVELS if a.levels == "all" else [x for x in a.levels.split(",") if x]
    os.makedirs(OUT, exist_ok=True)
    if not a.report:
        jobs = max(1, min(a.jobs, len(levels)))
        with concurrent.futures.ThreadPoolExecutor(jobs) as pool:
            for f in [pool.submit(job, k, levels[k::jobs], a) for k in range(jobs)]:
                f.result()
    report(levels, a.room)


if __name__ == "__main__":
    main()
