#!/usr/bin/env python3
"""Stage the console build: the XEX and the converted data in one folder.

    stage_console.py --to <folder> [--data work/GameData] [--xex work/console/xsdk/default.xex] [--dry-run]

Writes <folder>/default.xex, <folder>/GameData/{objects,ctex,audio,fonts}
and <folder>/licenses/, the layout console/xsdk/main.cpp reads from game:\\.
Copy <folder> to the console (USB drive or FTP) and start default.xex.
Files already there with the same size and time are skipped, so staging
again after a rebuild copies what changed. The PC's own Arial (Windows'
font folder) goes into GameData/fonts: the game falls back to it for
characters its fonts lack, as Unity does on Windows.

Everything staged comes from the player's own game files: keep it to the
player's own console.
"""
import argparse
import os
import shutil
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def copy_tree(src, dst, exts, dry, stats):
    for dirpath, _, files in os.walk(src):
        rel = os.path.relpath(dirpath, src)
        for f in files:
            if exts and not f.endswith(exts):
                continue
            s = os.path.join(dirpath, f)
            d = os.path.join(dst, rel, f)
            st = os.stat(s)
            if st.st_size >= 4 << 30:
                sys.exit("%s is 4 GB or more: FAT32 can't hold it" % s)
            stats["files"] += 1
            stats["bytes"] += st.st_size
            if os.path.exists(d):
                dt = os.stat(d)
                if dt.st_size == st.st_size and int(dt.st_mtime) >= int(st.st_mtime):
                    continue
            stats["copied"] += 1
            if not dry:
                os.makedirs(os.path.dirname(d), exist_ok=True)
                shutil.copy2(s, d)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--to", required=True)
    ap.add_argument("--data", default=os.path.join(ROOT, "work", "GameData"))
    ap.add_argument("--xex", default=os.path.join(ROOT, "work", "console", "xsdk", "default.xex"))
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()
    for need in ("objects", "ctex", "audio"):
        if not os.path.isdir(os.path.join(a.data, need)):
            sys.exit("no %s in %s (convert_objects.py, convert_console.py, convert_audio.py)" % (need, a.data))
    if not os.path.exists(a.xex):
        sys.exit("no XEX at %s (tools/build_console.py --stage xsdk)" % a.xex)
    stats = {"files": 0, "bytes": 0, "copied": 0}
    total = sum(os.path.getsize(os.path.join(dp, f)) for sub in ("objects", "ctex", "audio")
                for dp, _, fs in os.walk(os.path.join(a.data, sub)) for f in fs)
    if not a.dry_run:
        os.makedirs(a.to, exist_ok=True)
        free = shutil.disk_usage(a.to).free
        if free < total * 1.02:
            sys.exit("%s has %.1f GB free; the data needs %.1f GB" % (a.to, free / 2**30, total / 2**30))
    gd = os.path.join(a.to, "GameData")
    copy_tree(os.path.join(a.data, "objects"), os.path.join(gd, "objects"), (".chob",), a.dry_run, stats)
    copy_tree(os.path.join(a.data, "ctex"), os.path.join(gd, "ctex"), (".ctx",), a.dry_run, stats)
    copy_tree(os.path.join(a.data, "audio"), os.path.join(gd, "audio"), (".ogg",), a.dry_run, stats)
    arial = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", "arial.ttf")
    if os.path.exists(arial) and not a.dry_run:
        os.makedirs(os.path.join(gd, "fonts"), exist_ok=True)
        shutil.copy2(arial, os.path.join(gd, "fonts", "arial.ttf"))
    if not a.dry_run:
        shutil.copy2(a.xex, os.path.join(a.to, "default.xex"))
        lic = os.path.join(os.path.dirname(a.xex), "licenses")
        if os.path.isdir(lic):
            shutil.copytree(lic, os.path.join(a.to, "licenses"), dirs_exist_ok=True)
        os.makedirs(os.path.join(a.to, "save"), exist_ok=True)
    print("stage_console: %d files, %.2f GB; %d copied%s -> %s" % (
        stats["files"], stats["bytes"] / 2**30, stats["copied"], " (dry run)" if a.dry_run else "", a.to))
    return 0


if __name__ == "__main__":
    sys.exit(main())
