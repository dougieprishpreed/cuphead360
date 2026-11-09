#!/usr/bin/env python3
"""level_costs.py: every level and map played on the PC player with its cost counters, for what will be slow on the console.

    python tools/level_costs.py [--levels all|none|Veggies,Platforming_Level_1_2,...] [--scenes all|none|scene_map_world_1,...]
        [--fight 6000] [--save work/console/pcroot/save_pc] [--data work/t34root/GameData] [--out work/costs]
        [--report] [--console hw30]

Each level is played as the forced sweep plays it (tools/sweep_levels.py: the bot's full policy, invincible, every boss
state reached, run-and-gun levels pushed along their camera's path when stuck), each map and hub visited, on the JIT
player with the console's render path (host transforms, large-mesh strips) and these counters:

  PROFILE   the engine's phases, ms a frame on this PC (CH360_PROFILE); TOP and ALLOC: the scripts' time and garbage by type
  PHYS      the contact pass per fixed step (colliders, sweep pairs, shape tests, contacts, collision response tests)
            and the queries per frame (casts and overlaps, and the colliders each looked at) (CH360_PHYS_STATS)
  LIST      what a frame draws (items, vertices, textures, render targets) and the list's build time (CH360_LIST_STATS)

Per 600-frame window inside the level (loads left out) it estimates the console's frame: each phase's PC time times
its console/PC ratio, the fixed-step phases (fixed scripts, physics) once per step and the steps a frame growing as
the frame does (a slow frame runs more of them). The ratios come from console runs of the same levels (--console:
work/console/<run>/cuphead360.txt; the first island's levels and map), so they carry that build's differences.

Writes <out>/<name>.log per run and <out>/report.txt: per level the worst window's estimate and the phase that leads
it, the counters behind it, the scripts and the densest drawing; then the shared causes (a counter or script over its
threshold, with the levels it is in). --report only reads the logs already there. One run at a time.
"""
import argparse, collections, os, re, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sweep_levels import LEVELS, SCENES, SETUPS, LOADOUTS, PROLOGUE, PLAYER  # noqa: E402

PHASES = ("pump", "fixed", "physics", "update", "animators", "late", "ui", "end", "audio")
STEP_PHASES = ("fixed", "physics")      # once per fixed step
FRAME_MS = 1000.0 / 60
CONSOLE_PUMP = 0.5
CONSOLE_AUDIO = 1.5
CONSOLE_HOST = 3.0       # the host's glue, boxes and recording past vblank (Forest Follies: 2.4-4.5 ms of its 7 ms submit)
PRIOR_MS = 0.3

LEVEL_SCRIPT = PROLOGUE + """loadout {primary} {secondary} {super} {charm}
mugman {mugman}
difficulty Normal
level {level} 3600
wait 200
{setup}invincible
fight {fight} all force phase=900
"""

SCENE_SCRIPT = PROLOGUE + """scene {scene} 3600
visit {frames}{quiet}
"""

ENV = {"CH360_PROFILE": "1", "CH360_PHYS_STATS": "600", "CH360_LIST_STATS": "5", "CH360_HOST_TRANSFORMS": "1"}


def run(a, name, text, frames):
    script = os.path.join(a.out, "bot_%s.txt" % name)
    open(script, "w", encoding="utf-8").write(text)
    save = os.path.join(a.out, "save_" + name)
    shutil.rmtree(save, ignore_errors=True)
    shutil.copytree(a.save, save)
    env = dict(os.environ, **ENV)
    with open(os.path.join(a.out, name + ".log"), "wb") as f:
        subprocess.run(["dotnet", PLAYER, "run", "--data", a.data, "--frames", str(frames), "--save", save, "--bot", script],
                       stdout=f, stderr=subprocess.STDOUT, cwd=ROOT, env=env, creationflags=0x08000000 if os.name == "nt" else 0)
    shutil.rmtree(save, ignore_errors=True)


# ---------------------------------------------------------------- reading a PC log

def kv(s):
    return {k: float(v) for k, v in re.findall(r"([\w.`<>]+)=(-?[\d.]+)", s)}


def parse_pc(path):
    """Windows of 600 frames: {end, scene, phases{}, phys{}, list{}, top[(type, ms)], alloc[(type, kb)]}."""
    scene_at = []                                  # (frame, scene)
    done_at = None                                 # the bot's win or the level's forced end: play windows stop there
    wins = {}
    phys, lists, tops, allocs = [], [], {}, {}
    for ln in open(path, encoding="utf-8", errors="replace"):
        m = re.match(r"FRAME (\d+) t=[\d.]+ scene=(\S+)", ln)
        if m:
            scene_at.append((int(m.group(1)), m.group(2)))
            continue
        m = re.match(r"BOT (?:won at frame|force: level ending at frame) (\d+)", ln)
        if m and done_at is None:
            done_at = int(m.group(1))
            continue
        m = re.match(r"frame (\d+) PROFILE ms/frame: (.*)", ln)
        if m:
            wins[int(m.group(1))] = kv(m.group(2))
            continue
        m = re.match(r"PHYS frame (\d+): per step (\d+) colliders, (\d+) sweep pairs, (\d+) shape tests, (\d+) contacts, (\d+) dynamic bodies, "
                     r"(\d+) resolve tests; per frame (\d+) casts looking at (\d+) colliders, (\d+) overlaps looking at (\d+)", ln)
        if m:
            g = [int(x) for x in m.groups()]
            phys.append((g[0], dict(colliders=g[1], pairs=g[2], tests=g[3], contacts=g[4], bodies=g[5], resolve=g[6],
                                    casts=g[7], castLooks=g[8], overlaps=g[9], overlapLooks=g[10])))
            continue
        m = re.match(r"LIST frames (\d+)-(\d+): (\d+) items \(most (\d+)\), (\d+) vertices \(most (\d+)\), (\d+) textures, (\d+) render targets a frame; "
                     r"densest item (.*) (\d+) vertices; built in ([\d.]+) ms(?:; distinct sprite art ([\d.]+) Mtexels)?", ln)
        if m:
            lists.append((int(m.group(2)), dict(items=int(m.group(3)), itemsMax=int(m.group(4)), verts=int(m.group(5)), vertsMax=int(m.group(6)),
                                                textures=int(m.group(7)), targets=int(m.group(8)), densest=m.group(9), densestVerts=int(m.group(10)),
                                                build=float(m.group(11)), art=float(m.group(12) or 0))))
            continue
        m = re.match(r"(TOP|ALLOC) (\d+)-(\d+) (?:ms|KB)/frame: (.*)", ln)
        if m:
            items = []
            for part in m.group(4).split(", "):
                mm = re.match(r"(.+) ([\d.]+)$", part.strip())
                if mm: items.append((mm.group(1), float(mm.group(2))))
            (tops if m.group(1) == "TOP" else allocs)[int(m.group(3))] = items
    def scene(f):
        best = None
        for fr, sc in scene_at:
            if fr <= f: best = sc
            else: break
        return best
    def near(lst, end):
        c = [x for f, x in lst if end - 600 < f <= end + 5]
        return c[-1] if c else None
    out = []
    for end in sorted(wins):
        s0, s1 = scene(end - 599 - 120), scene(end)   # the scene since two seconds before the window: no load in it
        out.append(dict(end=end, scene=s1, whole=s0 == s1 and (done_at is None or end <= done_at or not s1.startswith("scene_level")), phases=wins[end], phys=near(phys, end), list=near(lists, end),
                        top=tops.get(end, []), alloc=allocs.get(end, [])))
    return out


# ---------------------------------------------------------------- the console's side, for the ratios

def parse_console(path):
    """FRAMES windows of a console log: (scene, game phases ms/frame incl. list/submit, between-frames p50 ms)."""
    out = []
    for ln in open(path, encoding="utf-8", errors="replace"):
        m = re.match(r"FRAMES (\d+)-(\d+): ([\d.]+) ms/frame; (.*?); audio decode.*between frames p50 (\d+).*; (\S+)$", ln)
        if not m: continue
        sc = m.group(6).split(",")
        if len(sc) != 1: continue                      # a load inside the window
        ph = kv(m.group(4))
        if ph.get("pump", 0) > 1.0: continue           # loading
        out.append((sc[0], ph, float(m.group(3))))
    return out


def ratios(a, pcs):
    """console ms / PC ms per phase: per reference level (a scene both ran, its play windows averaged; the fixed-step
    phases per step), then the median over the levels. Pooled sums let the scenes whose PC times are tiny (menus,
    the map) set the ratio; per level they agree within 8-20 for every engine phase."""
    cpath = os.path.join(ROOT, "work", "console", a.console, "cuphead360.txt")
    if not os.path.exists(cpath): return None, {}
    con = collections.defaultdict(list)
    for sc, ph, ms in parse_console(cpath):
        if not sc.startswith("scene_level"): continue
        steps = max(1.0, ms / FRAME_MS)
        con[sc].append({p: (ph.get(p, 0) / steps if p in STEP_PHASES else ph.get(p, 0)) for p in PHASES + ("list",)})
    per = collections.defaultdict(list)
    used = []
    skip = a.calib_skip.split(",")
    for sc, wins in pcs.items():
        if sc not in con: continue
        pw = [w for w in wins if w["whole"] and w["scene"] == sc]
        if not pw: continue
        used.append(sc)
        for p in PHASES:
            if p in ("pump", "audio") or (sc in skip and p == "physics"): continue
            c = sum(x[p] for x in con[sc]) / len(con[sc]); v = sum(w["phases"].get(p, 0) for w in pw) / len(pw)
            if v > 0.02: per[p].append(c / v)
        lw = [w["list"]["build"] for w in pw if w["list"]]
        if lw: per["list"].append(sum(x["list"] for x in con[sc]) / len(con[sc]) / (sum(lw) / len(lw)))
    med = lambda l: sorted(l)[len(l) // 2] if l else None
    r = {p: med(l) for p, l in per.items()}
    allr = [v for v in r.values() if v]
    r["all"] = med(allr)
    return r, used


def script_ratio(a, pcs):
    """console ms / PC ms for script types (TOP lines): the median over the types both a profiled console run
    (--console-scripts: one with profile.txt, its TOP lines filled) and these runs time in the same scene."""
    cpath = os.path.join(ROOT, "work", "console", a.console_scripts, "cuphead360.txt")
    if not os.path.exists(cpath): return None
    con = collections.defaultdict(lambda: collections.defaultdict(list))
    scene = None
    for ln in open(cpath, encoding="utf-8", errors="replace"):
        m = re.match(r"FRAMES \d+-\d+: .*; (\S+)$", ln)
        if m: scene = m.group(1).split(",")[-1]; continue
        m = re.match(r"TOP \d+-\d+ ms/frame: (.+)", ln)
        if m and scene and scene.startswith("scene_level"):
            for part in m.group(1).split(", "):
                mm = re.match(r"(.+) ([\d.]+)$", part.strip())
                if mm: con[scene][mm.group(1)].append(float(mm.group(2)))
    rs = []
    for sc, wins in pcs.items():
        if sc not in con: continue
        pc = collections.defaultdict(list)
        for w in wins:
            if w["whole"] and w["scene"] == sc:
                for t, ms in w["top"]: pc[t].append(ms)
        for t, l in pc.items():
            if t in con[sc] and sum(l) / len(l) > 0.02:
                rs.append((sum(con[sc][t]) / len(con[sc][t])) / (sum(l) / len(l)))
    return sorted(rs)[len(rs) // 2] if rs else None


def estimate(w, r):
    """The console's frame for a PC window: the per-frame phases, the list's build, the host's mix (CONSOLE_AUDIO; the PC
    mixes nothing) and its pump in play, plus the fixed-step phases once per step, the steps growing with the frame
    (f = A + B * max(1, f / 16.7)). The console's submit stands in as CONSOLE_HOST: on a fast frame most of it is the wait
    for vblank. Texture pages read on demand (the run-and-gun levels' hitches) are not in it."""
    ph = w["phases"]
    R = lambda p: r.get(p) or r.get("all") or 10.0
    parts = {p: ph.get(p, 0) * R(p) for p in PHASES if p not in STEP_PHASES and p not in ("pump", "audio")}
    parts["audio"] = CONSOLE_AUDIO
    parts["host"] = CONSOLE_HOST
    if w["list"]: parts["list"] = w["list"]["build"] * R("list")
    A = CONSOLE_PUMP + sum(parts.values())
    B = sum(ph.get(p, 0) * R(p) for p in STEP_PHASES)
    frame = A + B
    if frame > FRAME_MS:
        frame = A / (1 - B / FRAME_MS) if B < 0.9 * FRAME_MS else float("inf")
    steps = max(1.0, frame / FRAME_MS) if frame != float("inf") else 10
    for p in STEP_PHASES: parts[p] = ph.get(p, 0) * R(p) * steps
    return frame, parts


# ---------------------------------------------------------------- the report

THRESH = dict(pairs=600, tests=150, contacts=100, resolve=400, castLooks=3000, overlapLooks=1500, verts=20000, items=150, targets=3, art=60)


def report(a, names):
    pcs, runs = {}, {}
    for n in names:
        p = os.path.join(a.out, n + ".log")
        if not os.path.exists(p): continue
        wins = parse_pc(p)
        runs[n] = wins
        for w in wins:
            if w["whole"] and w["scene"]: pcs.setdefault(w["scene"], []).append(w)
    r, used = ratios(a, pcs)
    sr = script_ratio(a, pcs) or 4.0
    lines = []
    P = lines.append
    if r:
        P("console/PC ratios from %s (median over %s): %s; scripts %.1f (%s's TOP lines)" % (
            a.console, ", ".join(used), ", ".join("%s %.1f" % (k, v) for k, v in r.items() if v), sr, a.console_scripts))
    else:
        r = {"all": 10.0}
        P("no console run to calibrate with: ratios 10")
    P("")
    P("%-28s %-30s %7s %8s  %-44s %s" % ("run", "scene @worst window", "avg ms", "worst ms", "worst window's leading parts (console ms)", "counters"))
    causes = collections.defaultdict(list)
    script_by = collections.defaultdict(list)
    rows = []
    for n, wins in runs.items():
        lw = [w for w in wins if w["whole"] and w["scene"] and (w["scene"].startswith("scene_level") or w["scene"].startswith("scene_map")
                                                                or w["scene"] in ("scene_shop", "scene_shop_DLC"))
              ]
        if not lw:
            rows.append((0, "%-28s (no play windows)" % n)); continue
        est = [(estimate(w, r), w) for w in lw]
        (frame, parts), w = max(est, key=lambda x: x[0][0])
        avg = sum(e[0][0] for e in est if e[0][0] != float("inf")) / max(1, len(est))
        lead = sorted(parts.items(), key=lambda kv: -kv[1])[:4]
        ph, ls = w["phys"] or {}, w["list"] or {}
        cnt = []
        if ph: cnt.append("cols %d pairs %d tests %d contacts %d resolve %d casts %d/%d overlaps %d/%d" % (
            ph["colliders"], ph["pairs"], ph["tests"], ph["contacts"], ph["resolve"], ph["casts"], ph["castLooks"], ph["overlaps"], ph["overlapLooks"]))
        if ls: cnt.append("items %d verts %d tex %d rt %d art %.0f Mtx" % (ls["items"], ls["verts"], ls["textures"], ls["targets"], ls["art"]))
        rows.append((avg, "%-28s %-30s %7.1f %8.1f  %-44s %s" % (
            n, "%s @%d" % (w["scene"].replace("scene_", ""), w["end"]), avg, frame,
            ", ".join("%s %.1f" % kv for kv in lead), "; ".join(cnt))))
        top = ", ".join("%s %.2f" % t for t in w["top"][:5])
        if top: rows.append((avg - 1e-6, "%-28s   scripts (PC ms): %s" % ("", top)))
        if ls: rows.append((avg - 2e-6, "%-28s   densest item: %s %d vertices" % ("", ls["densest"][:70], ls["densestVerts"])))
        for k, t in THRESH.items():
            v = (ph.get(k) if k in ph else ls.get(k)) if (ph or ls) else None
            if v is not None and v > t: causes[k].append("%s %d" % (n, v))
        for typ, ms in w["top"][:3]:
            if ms * sr > 0.5: script_by[typ.split(".<")[0]].append("%s %.1f" % (n, ms * sr))
    for _, ln in sorted(rows, key=lambda x: -x[0]): P(ln)
    P("")
    P("SHARED CAUSES (counter over its threshold in the worst window: the runs)")
    for k, lst in sorted(causes.items(), key=lambda kv: -len(kv[1])):
        P("  %-14s > %-6d %d runs: %s" % (k, THRESH[k], len(lst), ", ".join(lst)))
    P("")
    P("SCRIPTS over 0.5 console ms a frame in a worst window (est.)")
    for k, lst in sorted(script_by.items(), key=lambda kv: -len(kv[1])):
        P("  %-50s %s" % (k, ", ".join(lst)))
    text = "\n".join(lines) + "\n"
    open(os.path.join(a.out, "report.txt"), "w", encoding="utf-8").write(text)
    print(text)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--levels", default="all")
    ap.add_argument("--scenes", default="scene_map_world_1,scene_map_world_2,scene_map_world_3,scene_map_world_4,scene_map_world_DLC,scene_shop")
    ap.add_argument("--fight", type=int, default=6000)
    ap.add_argument("--visit", type=int, default=2400)
    ap.add_argument("--save", default=os.path.join(ROOT, "work", "console", "pcroot", "save_pc"))
    ap.add_argument("--data", default=os.path.join(ROOT, "work", "t34root", "GameData"))
    ap.add_argument("--out", default=os.path.join(ROOT, "work", "costs"))
    ap.add_argument("--console", default="hw30")
    ap.add_argument("--console-scripts", default="hw28", help="a console run with its scripts timed (profile.txt), for the scripts' ratio")
    ap.add_argument("--calib-skip", default="scene_level_platforming_1_2F",
                    help="scenes left out of the ratios (changed since that console run: Treetop Trouble's physics)")
    ap.add_argument("--report", action="store_true", help="only the report, from the logs already in --out")
    a = ap.parse_args()
    levels = LEVELS if a.levels == "all" else [] if a.levels == "none" else a.levels.split(",")
    scenes = [s.split(":")[0] for s in SCENES] if a.scenes == "all" else [] if a.scenes == "none" else a.scenes.split(",")
    os.makedirs(a.out, exist_ok=True)
    names = list(levels) + list(scenes)
    if not a.report:
        for i, lv in enumerate(levels):
            lo = LOADOUTS[i % len(LOADOUTS)]
            level, setup = SETUPS.get(lv, (lv, ""))
            t0 = time.time()
            run(a, lv, LEVEL_SCRIPT.format(name=lv, map="scene_map_world_1", level=level, setup=setup, fight=a.fight, primary=lo[0], secondary=lo[1],
                                           super=lo[2], charm=lo[3], mugman="on" if lo[4] == "mugman" else "off"), 1800 + 900 + a.fight + 300)
            print("%-28s %.0f s" % (lv, time.time() - t0), flush=True)
        for sc in scenes:
            t0 = time.time()
            run(a, sc, SCENE_SCRIPT.format(name=sc, map="scene_map_world_1", scene=sc, frames=a.visit, quiet=""), 1800 + 900 + a.visit)
            print("%-28s %.0f s" % (sc, time.time() - t0), flush=True)
    report(a, names)
    return 0


if __name__ == "__main__":
    sys.exit(main())
