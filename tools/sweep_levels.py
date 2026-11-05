#!/usr/bin/env python3
"""sweep_levels.py: every level and scene played by the bot on the JIT player, for missing behaviour.

    python tools/sweep_levels.py --save work/console/pcroot/save_pc [--passes Normal,Hard,Easy]
        [--levels all|Veggies,Slime,...] [--scenes all|none|scene_map_world_DLC,...] [--fight 18000]
        [--out work/sweep]

Each level, in each pass (a difficulty), is played from a copy of the save (a slot on the world map):
title -> continue -> map -> a loadout from the rotation (weapons, super, charm; Cuphead, Mugman or
Ms. Chalice) -> `level NAME` (the game's own SceneLoader.LoadLevel) -> invincible (the game's
debug switch, so every phase is reached) -> `fight N all`: the bot's full policy (EX shots and
supers, parries, weapon switches, dashes, aiming; plane and run-and-gun policies) at the game's
own damage. The bot logs the boss's phases (the level's LevelProperties states), the death card's
timeline (damage and named milestones), the parts fought, the weapons and supers seen, and 40 s
stretches without damage (a stuck phase). Scenes (maps, shops, cutscenes, hubs) are visited:
`scene NAME`, then wandering with A presses (cutscenes play without input).

Writes <out>/<name>.log; prints a row per run (scene reached, how it ended, phases, milestones,
damage, stalls, exceptions, data problems, missing engine methods), then the exceptions and
missing methods over all runs, and which weapons, supers and charms were played. One run at a time.
"""
import argparse, collections, os, re, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLAYER = os.path.join(ROOT, "work", "managed", "jit", "Cuphead360.Player.dll")

# Levels enum names (Assembly-CSharp Levels) worth playing: bosses, run-and-gun, King Dice's
# rooms, the shmups, the Mausoleum and tutorials, and the DLC (King's Leap included).
LEVELS = """Tutorial ShmupTutorial ChaliceTutorial
Veggies Slime Frogs Flower FlyingBlimp Platforming_Level_1_1 Platforming_Level_1_2 Mausoleum
Baroness Clown Dragon FlyingBird FlyingGenie Platforming_Level_2_1 Platforming_Level_2_2
Bee Pirate Mouse Robot SallyStagePlay Train FlyingMermaid Platforming_Level_3_1 Platforming_Level_3_2
DicePalaceMain DicePalaceBooze DicePalaceChips DicePalaceCigar DicePalaceDomino DicePalaceRabbit
DicePalaceFlyingHorse DicePalaceRoulette DicePalaceEightBall DicePalaceFlyingMemory Devil
Airplane OldMan RumRunners SnowCult FlyingCowboy Saltbaker Graveyard
ChessPawn ChessKnight ChessBishop ChessRook ChessQueen""".split()

# Scenes that aren't fights: maps (the DLC island too), shops, hubs, cutscenes (played without input).
SCENES = """scene_map_world_1 scene_map_world_2 scene_map_world_3 scene_map_world_4 scene_map_world_DLC
scene_shop scene_shop_DLC scene_level_house_elder_kettle scene_level_dice_gate scene_level_kitchen
scene_level_chess_castle scene_level_tower_of_power
scene_cutscene_intro:quiet scene_cutscene_world2:quiet scene_cutscene_world3:quiet scene_cutscene_kingdice:quiet
scene_cutscene_devil:quiet scene_cutscene_outro:quiet scene_cutscene_credits:quiet scene_cutscene_dlc_intro:quiet
scene_cutscene_dlc_saltbaker_prebattle:quiet scene_cutscene_dlc_ending:quiet scene_cutscene_dlc_credits_comic:quiet""".split()

# The shop's nine weapons, three supers (Ms. Chalice gets her own versions) and nine charms
# (Astral Cookie plays Ms. Chalice), each in several loadouts.
LOADOUTS = [
    ("level_weapon_peashot", "level_weapon_spreadshot", "level_super_beam", "charm_health_up_1", "cuphead"),
    ("level_weapon_homing", "level_weapon_bouncer", "level_super_ghost", "charm_smoke_dash", "mugman"),
    ("level_weapon_charge", "level_weapon_boomerang", "level_super_invincible", "charm_parry_plus", "cuphead"),
    ("level_weapon_crackshot", "level_weapon_wide_shot", "level_super_beam", "charm_chalice", "cuphead"),
    ("level_weapon_upshot", "level_weapon_peashot", "level_super_ghost", "charm_parry_attack", "mugman"),
    ("level_weapon_spreadshot", "level_weapon_charge", "level_super_invincible", "charm_super_builder", "cuphead"),
    ("level_weapon_boomerang", "level_weapon_crackshot", "level_super_beam", "charm_healer", "mugman"),
    ("level_weapon_wide_shot", "level_weapon_homing", "level_super_ghost", "charm_health_up_2", "cuphead"),
    ("level_weapon_bouncer", "level_weapon_upshot", "level_super_invincible", "charm_curse", "mugman"),
    ("level_weapon_homing", "level_weapon_charge", "level_super_invincible", "charm_chalice", "mugman"),
    ("level_weapon_upshot", "level_weapon_spreadshot", "level_super_ghost", "charm_chalice", "cuphead"),
]

PROLOGUE = """# sweep_levels.py: {name}
wait 594
pressuntil scene_slot_select A 108 3000
wait 120
pressuntil {map} A 60 3000
wait 120
"""

LEVEL_SCRIPT = PROLOGUE + """loadout {primary} {secondary} {super} {charm}
mugman {mugman}
difficulty {mode}
level {level} 3600
wait 200
invincible
fight {fight} all
"""

SCENE_SCRIPT = PROLOGUE + """scene {scene} 3600
visit {frames}{quiet}
"""


def summarize(text):
    s = {}
    m = re.search(r"SYNTHETIC: SceneLoader.Load(?:Level|Scene)\(\w+\) -> (\S+)", text)
    s["scene"] = m.group(1) if m else "(not reached)"
    ends = re.findall(r"^BOT (?:fight|visit) [^\n]*?: (scene changed to \S+|TIMEOUT|done in \S+)", text, re.M)
    s["end"] = ends[-1] if ends else ""
    s["won"] = bool(re.search(r"^BOT won at", text, re.M))
    m = re.search(r"^BOT summary: phases (\d+)/(\d+) \[([^\]]*)\](?: missing \[([^\]]*)\])?", text, re.M)
    s["phases"] = "%s/%s" % (m.group(1), m.group(2)) if m else ""
    s["missing_phases"] = m.group(4) if m and m.group(4) else ""
    m = re.search(r"damage [\d.]+/\d+ \((\d+)%\), events (\d+)/(\d+)", text)
    s["damage"] = "%s%% ev %s/%s" % (m.group(1), m.group(2), m.group(3)) if m else ""
    s["stalls"] = len(re.findall(r"^BOT no damage for 40 s", text, re.M))
    s["stall_lines"] = re.findall(r"^BOT no damage for 40 s[^\n]*", text, re.M)[:3]
    s["weapons"] = set(re.findall(r"^BOT weapon (\S+)", text, re.M))
    s["supers"] = set(re.findall(r"^BOT super (?!used )(\w+) at", text, re.M))
    s["supers_used"] = len(re.findall(r"^BOT super used", text, re.M))
    m = re.search(r"parry tries (\d+)", text)
    s["parries"] = int(m.group(1)) if m else 0
    s["knockouts"] = len(re.findall(r"^BOT knocked out", text, re.M))
    s["visited"] = re.findall(r"^BOT visit: scene (\S+)", text, re.M)
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
    return s


def run(a, name, script_text, frames):
    script = os.path.join(a.out, "bot_%s.txt" % name)
    open(script, "w", encoding="utf-8").write(script_text)
    save = os.path.join(a.out, "save_" + name)
    shutil.rmtree(save, ignore_errors=True)
    shutil.copytree(a.save, save)
    log = os.path.join(a.out, name + ".log")
    with open(log, "wb") as f:
        subprocess.run(["dotnet", PLAYER, "run", "--frames", str(frames), "--save", save, "--bot", script],
                       stdout=f, stderr=subprocess.STDOUT, cwd=ROOT, creationflags=0x08000000 if os.name == "nt" else 0)
    shutil.rmtree(save, ignore_errors=True)
    return summarize(open(log, encoding="utf-8", errors="replace").read())


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--save", required=True, help="a save folder whose first slot is on a world map")
    ap.add_argument("--map", default="scene_map_world_1", help="the map scene that slot continues to")
    ap.add_argument("--levels", default="all")
    ap.add_argument("--scenes", default="all")
    ap.add_argument("--passes", default="Normal", help="difficulties, one pass each (Easy, Normal, Hard)")
    ap.add_argument("--fight", type=int, default=18000, help="frames of fighting per level at most")
    ap.add_argument("--visit", type=int, default=2400, help="frames per visited scene")
    ap.add_argument("--out", default=os.path.join(ROOT, "work", "sweep"))
    a = ap.parse_args()
    levels = LEVELS if a.levels == "all" else [] if a.levels == "none" else a.levels.split(",")
    scenes = SCENES if a.scenes == "all" else [] if a.scenes == "none" else a.scenes.split(",")
    os.makedirs(a.out, exist_ok=True)
    allexc, allmissing = collections.Counter(), collections.Counter()
    weapons, supers, charms, stalled = collections.Counter(), collections.Counter(), collections.Counter(), []
    for pi, mode in enumerate(a.passes.split(",")):
        for li, lv in enumerate(levels):
            lo = LOADOUTS[(li + 3 * pi) % len(LOADOUTS)]
            name = "%s_%s" % (lv, mode)
            t0 = time.time()
            s = run(a, name, LEVEL_SCRIPT.format(name=name, map=a.map, level=lv, mode=mode, fight=a.fight, primary=lo[0], secondary=lo[1],
                                                 super=lo[2], charm=lo[3], mugman="on" if lo[4] == "mugman" else "off"), 1800 + 900 + a.fight + 600)
            for k, n in s["exceptions"].items(): allexc[(k, name)] += n
            for m in s["missing"]: allmissing[m] += 1
            for w in s["weapons"]: weapons[w] += 1
            for x in s["supers"]: supers[x] += 1
            charms[lo[3]] += 1
            if s["stall_lines"] or s["missing_phases"]: stalled.append((name, s["missing_phases"], s["stall_lines"]))
            print("%-30s %-34s %-32s %-4s ph %-5s %-14s st %d pa %3d ko %d exc %d data %d miss %d  %s/%s %s (%.0f s)" % (
                name, s["scene"], s["end"], "WON" if s["won"] else "", s["phases"], s["damage"], s["stalls"], s["parries"], s["knockouts"],
                sum(s["exceptions"].values()), s["data"], len(s["missing"]), lo[3], lo[4], "%d %s" % (s["supers_used"], ",".join(sorted(s["supers"]))), time.time() - t0), flush=True)
    for sc in scenes:
        scene, _, quiet = sc.partition(":")
        t0 = time.time()
        s = run(a, scene, SCENE_SCRIPT.format(name=scene, map=a.map, scene=scene, frames=a.visit, quiet=" quiet" if quiet else ""), 1800 + 900 + a.visit + 600)
        for k, n in s["exceptions"].items(): allexc[(k, scene)] += n
        for m in s["missing"]: allmissing[m] += 1
        print("%-30s %-34s %-32s exc %d data %d miss %d  then %s (%.0f s)" % (
            scene, s["scene"], s["end"], sum(s["exceptions"].values()), s["data"], len(s["missing"]), ",".join(s["visited"][:4]), time.time() - t0), flush=True)
    print("\nEXCEPTIONS (run: count, message, first frame)")
    for ((msg, where), lv), n in sorted(allexc.items(), key=lambda kv: (-kv[1], kv[0][1])):
        print("  %s x%d: %s | %s" % (lv, n, msg, where))
    print("\nMISSING engine methods met (runs)")
    for m, n in allmissing.most_common():
        print("  %3d  %s" % (n, m))
    print("\nPHASES not reached or 40 s without damage")
    for name, missing, lines in stalled:
        print("  %s: missing [%s]" % (name, missing))
        for ln in lines: print("      " + ln)
    print("\nPLAYED weapons %s\n       supers %s\n       charms %s" % (dict(weapons), dict(supers), dict(charms)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
