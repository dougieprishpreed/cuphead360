"""The folder an asset bundle's converted files go in (<GameData>/objects/bundles/<folder>).

The console's internal drive (FATX) holds names of at most 42 characters; eight of the game's asset bundles have
longer names (TMP fonts and music, up to 53). Those are cut to 33 characters, then "_" and the FNV-1a 32 hash of the
whole name in 8 hex digits: 42 characters, the same on every PC. Shorter names stay as they are. The texture and clip
keys come from the folder (convert_textures.py, convert_audio.py: "bundles_<folder>_CAB-..."), so they follow it.
managed/Cuphead360.Engine/Impl/Bundles.cs (Folder) does the same for the bundle names the game loads.

migrate() renames data converted before this rule (stage_console.py runs it): the long folders, the files keyed by
them, and the keys in read-ahead lists.
"""
import os
import re
import shutil

MAX_NAME = 42   # FATX's longest file or folder name
KEY = re.compile(r"bundles_(\w+?)_CAB-")


def fnv1a32(text):
    h = 0x811C9DC5
    for b in text.encode("utf-8"):
        h = ((h ^ b) * 0x01000193) & 0xFFFFFFFF
    return h


def folder(name):
    if len(name) <= MAX_NAME:
        return name
    return "%s_%08x" % (name[:MAX_NAME - 9], fnv1a32(name))


def short_keys(text):
    """Text with every key of a long-named bundle ("bundles_<name>_CAB-") in its short form."""
    return KEY.sub(lambda m: "bundles_%s_CAB-" % folder(m.group(1)), text)


def migrate(data, list_dirs=(), dry=False):
    """Data converted with the long folder names: rename them and what is keyed by them. Returns the count."""
    n = 0
    bdir = os.path.join(data, "objects", "bundles")
    if os.path.isdir(bdir):
        for name in os.listdir(bdir):
            if len(name) > MAX_NAME and os.path.isdir(os.path.join(bdir, name)):
                n += 1
                if not dry:
                    dst = os.path.join(bdir, folder(name))
                    if os.path.exists(dst):   # converted again since: the short one is current
                        shutil.rmtree(os.path.join(bdir, name))
                    else:
                        os.rename(os.path.join(bdir, name), dst)
    for sub in ("audio", "ctex", "ctex4k", "ctexc", "ctexs", "textures"):
        d = os.path.join(data, sub)
        if not os.path.isdir(d):
            continue
        for f in os.listdir(d):
            if not f.startswith("bundles_"):
                continue
            g = short_keys(f)
            if g != f:
                n += 1
                if not dry:
                    if os.path.exists(os.path.join(d, g)):
                        os.remove(os.path.join(d, f))
                    else:
                        os.rename(os.path.join(d, f), os.path.join(d, g))
    for d in list_dirs:
        if not os.path.isdir(d):
            continue
        for f in os.listdir(d):
            if not f.endswith(".txt"):
                continue
            p = os.path.join(d, f)
            with open(p, encoding="utf-8", errors="replace") as fh:
                s = fh.read()
            t = short_keys(s)
            if t != s:
                n += 1
                if not dry:
                    with open(p, "w", encoding="utf-8", newline="\n") as fh:
                        fh.write(t)
    return n
