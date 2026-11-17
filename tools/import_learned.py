#!/usr/bin/env python3
"""import_learned.py: the console's learned lists (what each scene drew, in the order its plays first drew it), kept
in console/learned to ship with the build.

    python tools/import_learned.py --from E:\\Apps\\cuphead360\\learned [--to console/learned] [--dry-run]

The console writes <its folder>/learned/<scene>.txt as each scene ends ("<frame> <page key>" lines: the frame of the
scene's play in which a page was first drawn, merged over its plays, the earliest kept; console/ch360_imports.c, the
learned read-ahead). It reads that list first, else GameData/learned's. tools/stage_console.py stages console/learned's
lists into GameData/learned (over the PC's bot plays' from tools/learned_lists.py), so a new console starts from the
player's plays: the scene's next pages asked for ahead of its play, its first two seconds' pages read under its hold.

Each list is merged with the one already in --to the same way (a page's earliest frame), so importing after every
console run only adds. Lists name sprite pages (tools/cluster_console.py's keys): after the pages are cut again,
re-learn them (stale names are skipped by the console, and by --check here).
"""
import argparse, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def read(path):
    out = {}
    if os.path.exists(path):
        for ln in open(path, encoding="utf-8", errors="replace"):
            p = ln.split()
            if len(p) == 2 and p[0].isdigit():
                f = int(p[0])
                if p[1] not in out or f < out[p[1]]:
                    out[p[1]] = f
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--from", dest="src", required=True)
    ap.add_argument("--to", default=os.path.join(ROOT, "console", "learned"))
    ap.add_argument("--data", default=os.path.join(ROOT, "work", "GameData"), help="for --check: the converted data")
    ap.add_argument("--check", action="store_true", help="count the pages the converted data no longer has")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()
    if not os.path.isdir(a.src):
        sys.exit("no folder %s" % a.src)
    known = None
    if a.check:
        known = set(f[:-4] for f in os.listdir(os.path.join(a.data, "ctexc")) if f.endswith(".ctx"))
        known |= set(f[:-4] for f in os.listdir(os.path.join(a.data, "ctex")) if f.endswith(".ctx"))
    os.makedirs(a.to, exist_ok=True)
    scenes = added = 0
    for f in sorted(os.listdir(a.src)):
        if not f.endswith(".txt") or not f.startswith("scene_"):
            continue
        new = read(os.path.join(a.src, f))
        dst = os.path.join(a.to, f)
        old = read(dst)
        merged = dict(old)
        for k, fr in new.items():
            if k not in merged or fr < merged[k]:
                merged[k] = fr
        more = len(merged) - len(old)
        changed = merged != old
        stale = sum(1 for k in merged if known is not None and k not in known)
        print("%-44s %5d pages (%+d)%s%s" % (f[:-4], len(merged), more, " changed" if changed and more == 0 else "",
                                             ", %d not in the data" % stale if stale else ""))
        scenes += 1
        added += max(more, 0)
        if changed and not a.dry_run:
            rows = sorted(merged.items(), key=lambda kv: (kv[1], kv[0]))
            with open(dst + ".tmp", "w", encoding="utf-8", newline="\n") as o:
                for k, fr in rows:
                    o.write("%d %s\n" % (fr, k))
            os.replace(dst + ".tmp", dst)
    print("import_learned: %d scenes from %s into %s, %d pages added%s" % (scenes, a.src, a.to, added, " (dry run)" if a.dry_run else ""))


if __name__ == "__main__":
    main()
