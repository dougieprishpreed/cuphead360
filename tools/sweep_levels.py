#!/usr/bin/env python3
"""sweep_levels.py: every level played by the bot on the JIT player, for missing behaviour.

    python tools/sweep_levels.py --save work/console/pcroot/save_pc [--levels all|Veggies,Slime,...]
        [--fight 6000] [--out work/sweep]

For each level: a copy of the save (a slot on the world map), the bot script title -> continue ->
map -> `level NAME` (the game's own SceneLoader.LoadLevel) -> invincible -> megadamage (the
game's debug switches) -> fight with EX shots, run with the JIT player (no window, no sound).
Writes <out>/<level>.log and prints, per level, the scene reached, how the fight ended, the
exceptions (first line and where), the missing engine methods the run met and the data problems,
then the exceptions and missing methods over all levels. Runs one level at a time.
"""
import argparse, collections, os, re, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLAYER = os.path.join(ROOT, "work", "managed", "jit", "Cuphead360.Player.dll")

# Levels enum names (Assembly-CSharp Levels) worth playing: bosses, run-and-gun, King Dice's
# rooms, the shmups and the DLC; not the test scenes.
LEVELS = """Tutorial ShmupTutorial
Veggies Slime Frogs Flower FlyingBlimp Platforming_Level_1_1 Platforming_Level_1_2 Mausoleum
Baroness Clown Dragon FlyingBird FlyingGenie Platforming_Level_2_1 Platforming_Level_2_2
Bee Pirate Mouse Robot SallyStagePlay Train FlyingMermaid Platforming_Level_3_1 Platforming_Level_3_2
DicePalaceMain DicePalaceBooze DicePalaceChips DicePalaceCigar DicePalaceDomino DicePalaceRabbit
DicePalaceFlyingHorse DicePalaceRoulette DicePalaceEightBall DicePalaceFlyingMemory Devil
Airplane OldMan RumRunners SnowCult FlyingCowboy Saltbaker Graveyard
ChessPawn ChessKnight ChessBishop ChessRook ChessQueen""".split()

SCRIPT = """# sweep_levels.py: {level}
wait 594
pressuntil scene_slot_select A 108 3000
wait 120
pressuntil {map} A 60 3000
wait 120
level {level} 3600
wait 200
invincible
megadamage
fight {fight} ex
"""


def summarize(text):
    s = {}
    m = re.search(r"SYNTHETIC: SceneLoader.LoadLevel\(\w+\) -> (\S+)", text)
    s["scene"] = m.group(1) if m else "(not reached)"
    ends = re.findall(r"^BOT fight [^\n]*?: (scene changed to \S+|TIMEOUT)", text, re.M)
    s["end"] = ends[-1] if ends else ""
    s["knockouts"] = len(re.findall(r"^BOT knocked out", text, re.M))
    exc = collections.Counter()
    lines = text.split("\n")
    for i, ln in enumerate(lines):
        if ln.startswith("EXCEPTION"):
            where = next((l.strip() for l in lines[i + 1:i + 4] if l.strip().startswith("at ")), "")
            msg = re.sub(r"\('.*?'\)", "", ln)
            exc[(msg[:200], where[:160])] += 1
    s["exceptions"] = exc
    # the ledger's lines; Rewired's Windows input probes (user32, raw input) are expected
    s["missing"] = sorted(set(m.group(1) for m in re.finditer(r"^\s+missing (?:icall|pinvoke) (\S+)(.*)$", text, re.M)
                              if not re.search(r"user32\.dll|RawInput", m.group(2))))
    m = re.search(r"data problems (\d+)", text)
    s["data"] = int(m.group(1)) if m else -1
    s["problems"] = sorted(set(re.findall(r"^DATA [^\n]{0,200}", text, re.M)))[:10]
    m = re.search(r"RUN done: (\d+) frames in ([\d.]+) s", text)
    s["run"] = "%s frames, %s s" % (m.group(1), m.group(2)) if m else "(no RUN done)"
    return s


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--save", required=True, help="a save folder whose first slot is on a world map")
    ap.add_argument("--map", default="scene_map_world_1", help="the map scene that slot continues to")
    ap.add_argument("--levels", default="all")
    ap.add_argument("--fight", type=int, default=6000, help="frames of fighting per level")
    ap.add_argument("--out", default=os.path.join(ROOT, "work", "sweep"))
    a = ap.parse_args()
    levels = LEVELS if a.levels == "all" else a.levels.split(",")
    os.makedirs(a.out, exist_ok=True)
    allexc, allmissing, rows = collections.Counter(), collections.Counter(), []
    for lv in levels:
        script = os.path.join(a.out, "bot_%s.txt" % lv)
        open(script, "w", encoding="utf-8").write(SCRIPT.format(level=lv, map=a.map, fight=a.fight))
        save = os.path.join(a.out, "save_" + lv)
        shutil.rmtree(save, ignore_errors=True)
        shutil.copytree(a.save, save)
        log = os.path.join(a.out, lv + ".log")
        t0 = time.time()
        with open(log, "wb") as f:
            subprocess.run(["dotnet", PLAYER, "run", "--frames", str(1800 + 900 + a.fight), "--save", save, "--bot", script],
                           stdout=f, stderr=subprocess.STDOUT, cwd=ROOT, creationflags=0x08000000 if os.name == "nt" else 0)
        shutil.rmtree(save, ignore_errors=True)
        s = summarize(open(log, encoding="utf-8", errors="replace").read())
        rows.append((lv, s))
        for k, n in s["exceptions"].items(): allexc[(k, lv)] += n
        for m in s["missing"]:
            allmissing[m] += 1
        print("%-24s %-34s %-36s ko %d, exceptions %d, data %d, missing %d (%.0f s)" % (
            lv, s["scene"], s["end"], s["knockouts"], sum(s["exceptions"].values()), s["data"], len(s["missing"]), time.time() - t0), flush=True)
    print("\nEXCEPTIONS (level: count, message, first frame)")
    for ((msg, where), lv), n in sorted(allexc.items(), key=lambda kv: (-kv[1], kv[0][1])):
        print("  %s x%d: %s | %s" % (lv, n, msg, where))
    print("\nMISSING engine methods met (levels)")
    for m, n in allmissing.most_common():
        print("  %3d  %s" % (n, m))
    return 0


if __name__ == "__main__":
    sys.exit(main())
