#!/usr/bin/env python3
"""unlock_save.py: a save slot with every level open, for testing levels one after another.

    python tools/unlock_save.py --from <slot .sav> --to <slot .sav> [--keep Devil,Saltbaker] [--force]

Copies a save (its loadout, weapons and charms, its options) and marks every level completed on Normal with an A, so
the world maps' gates (the bridges, the boats, the casino and the DLC's doors check completed levels, their difficulty
and grade: PlayerData's level checks) let the player reach every level. The levels in --keep stay as they were (by
default the Devil and Chef Saltbaker: the last fights, which play their endings when won, not marked won). Every level
can be replayed from the map; nothing else (coins, supers, the curse charm) changes.

The game's save is plain JSON (PlayerData). The target slot is refused if it holds progress (a level played) unless
--force; its old file is kept beside it as <name>.bak.
"""
import argparse, json, os, shutil, sys

LEVELS = {
    "Tutorial": 0, "Pirate": 2, "Bat": 3, "Train": 5, "Veggies": 6, "Frogs": 7, "AirshipJelly": 8,
    "Bee": 1429976377, "Mouse": 1430652919, "Dragon": 1432722919, "Flower": 1450266910, "Slime": 1450863107,
    "Baroness": 1451300935, "AirshipStork": 1459338579, "AirshipCrab": 1459489001, "FlyingBird": 1428495827,
    "FlyingMermaid": 1446558823, "FlyingBlimp": 1449745424, "Robot": 1452935394, "Clown": 1456125457,
    "SallyStagePlay": 1456740288, "DicePalaceMain": 1465296077, "FlyingGenie": 1460200177, "Devil": 1466688317,
    "Mausoleum": 1481199742, "House": 1484633053, "DiceGate": 1495090481, "ShmupTutorial": 1504847973,
    "Airplane": 1511943573, "RumRunners": 1518081307, "OldMan": 1523429320, "SnowCult": 1527591209,
    "FlyingCowboy": 1530096313, "Kitchen": 1566994171, "Saltbaker": 1573044456, "ChaliceTutorial": 1580294079,
    "Graveyard": 1616405510, "ChessCastle": 1624358789,
}
NORMAL, GRADE_A = 1, 10   # Level.Mode.Normal, LevelScoringData.Grade.A


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--from", dest="src", required=True)
    ap.add_argument("--to", dest="dst", required=True)
    ap.add_argument("--keep", default="Devil,Saltbaker", help="levels left as they were (names from the game's Levels)")
    ap.add_argument("--force", action="store_true", help="replace a slot that holds progress")
    a = ap.parse_args()
    raw = open(a.src, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    data = json.loads(raw.decode("utf-8-sig"))
    keep = set()
    for name in filter(None, a.keep.split(",")):
        if name not in LEVELS:
            sys.exit("unknown level %s (known: %s)" % (name, ", ".join(sorted(LEVELS))))
        keep.add(LEVELS[name])
    if os.path.exists(a.dst) and not a.force:
        old = json.loads(open(a.dst, "rb").read().decode("utf-8-sig"))
        if any(o.get("played") for o in old["levelDataManager"]["levelObjects"]):
            sys.exit("%s holds progress (a level played): --force to replace it" % a.dst)
    opened = 0
    for o in data["levelDataManager"]["levelObjects"]:
        if o["levelID"] in keep:
            continue
        if not o["completed"] or o["difficultyBeaten"] < NORMAL:
            opened += 1
        o["completed"] = True
        o["played"] = True
        o["difficultyBeaten"] = max(o["difficultyBeaten"], NORMAL)
        o["grade"] = max(o["grade"], GRADE_A)
    out = json.dumps(data, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    if bom:
        out = b"\xef\xbb\xbf" + out
    if os.path.exists(a.dst):
        shutil.copy2(a.dst, a.dst + ".bak")
    with open(a.dst + ".tmp", "wb") as f:
        f.write(out)
    os.replace(a.dst + ".tmp", a.dst)
    print("unlock_save: %s -> %s: %d of %d levels marked won (kept as they were: %s)" % (
        a.src, a.dst, opened, len(data["levelDataManager"]["levelObjects"]), a.keep or "none"))


if __name__ == "__main__":
    main()
