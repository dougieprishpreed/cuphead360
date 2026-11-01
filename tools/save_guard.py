"""Fingerprint the installed Cuphead saves and check they are untouched.

    python tools/save_guard.py check   [--ref work/managed/appdata_cuphead_before.txt]
    python tools/save_guard.py snapshot [--ref ...]

Reads only: name, size, mtime and SHA-256 of every file in %APPDATA%\\Cuphead.
The reference file uses the format path|size|mtime_ticks|SHA256 (one per line).
"""
import argparse
import hashlib
import os
import sys

ROOT = os.path.join(os.environ.get("APPDATA", ""), "Cuphead")
EPOCH_TICKS = 621355968000000000  # .NET ticks at 1970-01-01


def fingerprint():
    rows = []
    if not os.path.isdir(ROOT):
        return rows
    for name in sorted(os.listdir(ROOT)):
        p = os.path.join(ROOT, name)
        if not os.path.isfile(p):
            continue
        st = os.stat(p)
        h = hashlib.sha256(open(p, "rb").read()).hexdigest().upper()
        ticks = EPOCH_TICKS + st.st_mtime_ns // 100
        rows.append(f"{p}|{st.st_size}|{ticks}|{h}")
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["check", "snapshot"])
    ap.add_argument("--ref", default=os.path.join("work", "managed", "appdata_cuphead_before.txt"))
    a = ap.parse_args()
    now = fingerprint()
    if a.mode == "snapshot":
        with open(a.ref, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(now) + "\n")
        print(f"save_guard: {len(now)} files recorded in {a.ref}")
        return 0
    ref = [l.strip().lstrip("﻿") for l in open(a.ref, encoding="utf-8-sig") if l.strip()]
    # Compare path, size and hash; mtime is reported but a hash match settles it.
    key = lambda r: (r.split("|")[0].lower(), r.split("|")[1], r.split("|")[3])
    if sorted(map(key, ref)) == sorted(map(key, now)):
        print(f"save_guard: OK, {len(now)} files unchanged (SHA-256)")
        return 0
    print("save_guard: CHANGED")
    for r in sorted(set(ref) ^ set(now)):
        print("  " + r)
    return 1


if __name__ == "__main__":
    sys.exit(main())
