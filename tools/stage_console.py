#!/usr/bin/env python3
"""Stage the console build: the XEX and the converted data in one folder.

    stage_console.py --to <folder> [--data work/GameData] [--xex work/console/xsdk/default.xex] [--dry-run]

Writes <folder>/default.xex, <folder>/GameData/{objects,ctex,audio,fonts}
and <folder>/licenses/, the layout console/xsdk/main.cpp reads from game:\\.
Textures and clips are staged under short names, <key>.ctx as
ctex/XX/YYYYYYYY.CTX and <key>.ogg as audio/XX/YYYYYYYY.OGG (XX the top
byte, YYYYYYYY the low word of FNV-1a 64 of the key; console/ch360_imports.c
works out the same): FAT32 has no folder index, so every open scanned a
folder of thousands of long names (~0.1 s a texture, ~0.3 s a clip on the
console). Loose files from the old layout are removed.
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


def staged_name(key):
    """XX/YYYYYYYY for a converted file's key (console/ch360_imports.c, ch360_staged_name)."""
    h = 0xcbf29ce484222325
    for c in key.encode("utf-8"):
        h = ((h ^ c) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return "%02X/%08X" % (h >> 56, h & 0xFFFFFFFF)


def plan(src, exts, short):
    """(source path, path under the staged folder) for each file; short: staged names."""
    out, seen = [], {}
    for dirpath, _, files in os.walk(src):
        rel = os.path.relpath(dirpath, src)
        for f in sorted(files):
            if exts and not f.endswith(exts):
                continue
            if short:
                stem, ext = os.path.splitext(f)
                d = staged_name(stem) + ext.upper()
                if d in seen:
                    sys.exit("staged name %s for both %s and %s" % (d, seen[d], f))
                seen[d] = f
            else:
                d = os.path.normpath(os.path.join(rel, f))
            out.append((os.path.join(dirpath, f), d))
    return out


def remove_loose(dst, exts, dry):
    """The old layout's loose <key>.ctx / <key>.ogg directly in dst."""
    n = 0
    if os.path.isdir(dst):
        for f in os.listdir(dst):
            p = os.path.join(dst, f)
            if os.path.isfile(p) and f.endswith(exts):
                n += 1
                if not dry:
                    os.remove(p)
    return n


def copy_tree(files, dst, dry, stats):
    for s, rel in files:
        d = os.path.join(dst, rel)
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
    gd = os.path.join(a.to, "GameData")
    parts = [("objects", (".chob",), False), ("ctex", (".ctx",), True), ("audio", (".ogg",), True)]
    plans = {sub: plan(os.path.join(a.data, sub), exts, short) for sub, exts, short in parts}
    removed = 0
    for sub, exts, short in parts:
        if short:
            removed += remove_loose(os.path.join(gd, sub), exts, a.dry_run)
    # Room for what isn't there yet (a file replaced frees its old copy's space).
    need = os.path.getsize(a.xex) - (os.path.getsize(os.path.join(a.to, "default.xex"))
                                     if os.path.exists(os.path.join(a.to, "default.xex")) else 0)
    for sub, files in plans.items():
        for s, rel in files:
            d = os.path.join(gd, sub, rel)
            need += os.path.getsize(s) - (os.path.getsize(d) if os.path.exists(d) else 0)
    if not a.dry_run:
        os.makedirs(a.to, exist_ok=True)
        free = shutil.disk_usage(a.to).free
        if free < max(need, 0) + (64 << 20):
            sys.exit("%s has %.1f GB free; staging needs %.1f GB more" % (a.to, free / 2**30, need / 2**30))
    for sub, files in plans.items():
        copy_tree(files, os.path.join(gd, sub), a.dry_run, stats)
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
    print("stage_console: %d files, %.2f GB; %d copied, %d old loose files removed%s -> %s" % (
        stats["files"], stats["bytes"] / 2**30, stats["copied"], removed, " (dry run)" if a.dry_run else "", a.to))
    return 0


if __name__ == "__main__":
    sys.exit(main())
