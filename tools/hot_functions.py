#!/usr/bin/env python3
"""hot_functions.py: which translated functions run often, for the console's -O2 / -Os split.

    hot_functions.py STACKS... [--out work/managed/hot_functions.txt] [--min-share 0.0002] [--linkmap MAP]

STACKS are tools/sample_native.py --raw --save-stacks files (one sample a line, leaf first, functions named fN as
the C names them), sampled on host_native over every level (a bot playing each in turn) and the menus. A
function is hot when it was the leaf of any sample (its own code ran) or was on the stack in at least
--min-share of the samples (a caller whose loop runs its callees). The list names every method of the
sampled build (the wasm's link map, as the C stage reads it), H hot or C cold: a name stays with its method
from build to build where its number moves, so the list holds across engine changes. tools/build_managed.py's
C stage puts the C ones in each player file's <file>_cold.c (-Os) and keeps the rest -O2, any method new since
the sampling among them. Sample again after large changes. Prints how many are hot and what they cover.
"""
import argparse
import collections
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from build_managed import LINKMAP, function_names  # noqa: E402


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("stacks", nargs="+")
    ap.add_argument("--linkmap", default=LINKMAP, help="the sampled build's (wasm-ld's map)")
    ap.add_argument("--out", default=os.path.join(ROOT, "work", "managed", "hot_functions.txt"))
    ap.add_argument("--min-share", type=float, default=0.0002)
    a = ap.parse_args()
    fn = re.compile(r"^f\d+$")
    leaf, onstack = collections.Counter(), collections.Counter()
    n = 0
    for path in a.stacks:
        for line in open(path, encoding="utf-8", errors="replace"):
            st = [x for x in line.rstrip("\n").split(";") if x]
            if not st:
                continue
            n += 1
            ff = [x for x in st if fn.match(x)]
            if ff and fn.match(st[0]):
                leaf[st[0]] += 1
            for x in set(ff):
                onstack[x] += 1
    if n == 0:
        sys.exit("no samples")
    hot = set(leaf) | {f for f, c in onstack.items() if c >= a.min_share * n}
    names = function_names(a.linkmap, os.path.join(ROOT, "work", "managed", "c", "player.h"))
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("# %d samples; H: run often (-O2), C: rarely (-Os)\n" % n)
        for x in sorted(names, key=lambda s: int(s[1:])):
            f.write(("H " if x in hot else "C ") + names[x] + "\n")
    leaves = sum(leaf.values())
    print("%d samples (%d with translated code at the leaf); %d of %d functions hot (%d as leaves) -> %s"
          % (n, leaves, len(hot), len(names), len(leaf), a.out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
