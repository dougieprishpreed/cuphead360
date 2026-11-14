#!/usr/bin/env python3
"""learned_lists.py: each level's and scene's textures in first-draw order, for the console's learned read-ahead.

    python tools/learned_lists.py [--levels all|Frogs,FlyingBlimp,...] [--scenes all|none|scene_map_world_1,...]
        [--save work/console/pcroot/save_pc] [--root work/t34root] [--phase 1200] [--fight 18000]

The console's glue (console/ch360_imports.c, learn_*) keeps, per scene, the textures its plays drew in the order
they first drew them, and while a scene plays asks for the few after the furthest one drawn: a boss's next form, a
spawn's first frames, the next stretch of a run-and-gun level are read before they're drawn. It learns on the console
(<USB>/learned/<scene>.txt); this script gives it a start for every scene the user hasn't played yet. Each level is
played by the bot on host_native (the console's code; hidden, muted) with sweep_levels.py's forced script (every
boss state, its normal play then a push into the next; the win; the screens after), from a root whose GameData holds
the texture packs (sprite pages need the packs' table); the glue writes <root>/learned/<scene>.txt as each scene
ends, merging with what is there (a texture's earliest frame kept). The lists are copied to work/console/learned,
which tools/stage_console.py stages into GameData/learned. --jobs N plays N at once, each from a root of its own (a
junction to the root's GameData, its own learned/ and save), and merges their lists (a texture's earliest frame;
the maps, menus and the win screen are seen by every job). A level takes 2-6 minutes.
"""
import argparse, concurrent.futures, os, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import sweep_levels as sw  # noqa: E402  (its levels, loadouts, setups and scripts)

HOST = os.path.join(ROOT, "work", "console", "native", "host_native.exe")
GUEST_ENV = ("CH360_TREE_BUDGET_MB=4 DOTNET_GCgen0size=100000 DOTNET_GCLOHCompact=1 CH360_WHOLE_FILE_KB=0 "
             "CH360_BLOCK_BUDGET_MB=4 CH360_HOLD_SCRIPTED=1 CH360_LOAD_SLICE_MS=20")

LEVEL_SCRIPT = sw.PROLOGUE + """loadout {primary} {secondary} {super} {charm}
mugman {mugman}
difficulty {mode}
level {level} 3600
wait 200
{setup}invincible
fight {fight} all force phase={phase}
visit 1500
"""

SCENE_SCRIPT = sw.PROLOGUE + """scene {scene} 3600
visit {frames}{quiet}
"""


def run(a, name, text, frames, root=None):
    root = os.path.abspath(root or a.root)
    save = os.path.join(root, "save_learn")
    if os.path.exists(save):
        shutil.rmtree(save)
    shutil.copytree(a.save, save)
    script = os.path.join(root, "bot_learn.txt")
    with open(script, "w", encoding="utf-8") as f:
        f.write(text)
    os.makedirs(a.out, exist_ok=True)
    log = os.path.join(a.out, "learn_%s.log" % name)
    env = dict(os.environ, CH360_GUEST_ENV=GUEST_ENV)
    t0 = time.time()
    with open(log, "w", encoding="utf-8") as f:
        subprocess.run([HOST, "--root", root, "--frames", str(frames), "--", "run", "--console", "1", "--data", "/GameData",
                        "--save", "/save_learn", "--bot", "/bot_learn.txt"], stdout=f, stderr=subprocess.STDOUT, cwd=ROOT,
                       env=env, creationflags=0x08000000 if os.name == "nt" else 0)
    text = open(log, encoding="utf-8", errors="replace").read()
    won = "BOT won at" in text
    print("%-28s %4.0f s%s" % (name, time.time() - t0, ", won" if won else ""), flush=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--levels", default="all")
    ap.add_argument("--scenes", default="all")
    ap.add_argument("--save", default=os.path.join(ROOT, "work", "console", "pcroot", "save_pc"))
    ap.add_argument("--root", default=os.path.join(ROOT, "work", "t34root"))
    ap.add_argument("--map", default="scene_map_world_1")
    ap.add_argument("--mode", default="Normal")
    ap.add_argument("--fight", type=int, default=18000)
    ap.add_argument("--phase", type=int, default=1200)
    ap.add_argument("--visit", type=int, default=2400)
    ap.add_argument("--out", default=os.path.join(ROOT, "work", "learn"))
    ap.add_argument("--jobs", type=int, default=1, help="levels played at once, each from a root of its own")
    a = ap.parse_args()
    levels = sw.LEVELS if a.levels == "all" else [x for x in a.levels.split(",") if x]
    scenes = [] if a.scenes == "none" else sw.SCENES if a.scenes == "all" else [x for x in a.scenes.split(",") if x]
    items = []
    for n, level in enumerate(levels):
        lo = sw.LOADOUTS[n % len(sw.LOADOUTS)]
        load, setup = sw.SETUPS.get(level, (level, ""))
        text = LEVEL_SCRIPT.format(name=level, map=a.map, primary=lo[0], secondary=lo[1], super=lo[2], charm=lo[3],
                                   mugman="on" if lo[4] == "mugman" else "off", mode=a.mode, level=load, setup=setup,
                                   fight=a.fight, phase=a.phase)
        items.append((level, text, 1800 + 900 + a.fight + 1500))
    for item in scenes:
        scene, _, quiet = item.partition(":")
        items.append((scene, SCENE_SCRIPT.format(name=scene, map=a.map, scene=scene, frames=a.visit, quiet=" quiet" if quiet else ""),
                      1800 + 900 + a.visit))
    base = os.path.abspath(a.root)
    roots = [base] if a.jobs <= 1 else [job_root(base, k) for k in range(a.jobs)]

    def worker(k):
        for i in range(k, len(items), len(roots)):
            run(a, items[i][0], items[i][1], items[i][2], roots[k])

    with concurrent.futures.ThreadPoolExecutor(len(roots)) as ex:
        list(ex.map(worker, range(len(roots))))
    dst = os.path.join(ROOT, "work", "console", "learned")
    os.makedirs(dst, exist_ok=True)
    merged = {}
    for r in roots + ([base] if base not in roots else []):   # and what the base root learned before
        src = os.path.join(r, "learned")
        if not os.path.isdir(src):
            continue
        for f in os.listdir(src):
            if not f.endswith(".txt"):
                continue
            m = merged.setdefault(f, {})
            for ln in open(os.path.join(src, f), encoding="utf-8", errors="replace"):
                frame, _, key = ln.strip().partition(" ")
                if key and frame.isdigit():
                    m[key] = min(m.get(key, 1 << 30), int(frame))
    for f, m in merged.items():
        with open(os.path.join(dst, f), "w", encoding="utf-8", newline="\n") as out:
            for key, frame in sorted(m.items(), key=lambda kv: (kv[1], kv[0])):
                out.write("%d %s\n" % (frame, key))
    print("%d lists in %s" % (len(merged), dst))


def job_root(base, k):
    """A job's own root: its GameData a junction to the base root's (the packs), its learned/ and save its own."""
    r = base + "_job%d" % k
    os.makedirs(r, exist_ok=True)
    gd = os.path.join(r, "GameData")
    if not os.path.exists(gd):
        subprocess.run(["cmd", "/c", "mklink", "/J", gd, os.path.join(base, "GameData")], check=True, stdout=subprocess.DEVNULL)
    learned = os.path.join(r, "learned")
    if os.path.isdir(learned):
        shutil.rmtree(learned)
    return r


if __name__ == "__main__":
    main()
