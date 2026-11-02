#!/usr/bin/env python3
"""Export the game's AudioClips as Ogg Vorbis files the engine decodes.

    convert_audio.py --game <GOG Cuphead folder> --out <GameData dir> [--files a,b] [--bundles] [--only-missing] [--quality Q]

Writes <out>/audio/<key>.ogg for every AudioClip, where <key> is the
converted object file's path below objects/ (slashes as "_", without
".chob") plus "_" and the path id: the same identity the textures use.
UnityPy decodes each clip (FSB5 Vorbis/PCM, through its bundled FMOD) to
WAV, and ffmpeg (on PATH) re-encodes it to Ogg Vorbis at the clip's own
sample rate and channel count. Reads the game folder only.
"""
import argparse
import os
import subprocess
import sys
import time

import UnityPy


def key_for(objects_rel, path_id):
    rel = objects_rel.replace("\\", "/")
    if rel.endswith(".chob"):
        rel = rel[:-5]
    return rel.replace("/", "_") + "_%d" % path_id


def export(sf, rel, outdir, stats, only_missing, quality):
    for path_id, obj in sf.objects.items():
        if obj.type is None or obj.type.name != "AudioClip":
            continue
        fn = os.path.join(outdir, key_for(rel, path_id) + ".ogg")
        if only_missing and os.path.exists(fn):
            continue
        try:
            clip = obj.read()
            samples = clip.samples
            if not samples:
                stats["empty"] += 1
                continue
            wav = next(iter(samples.values()))   # one subsound per clip in this game
            tmp = fn + ".tmp"
            p = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "wav", "-i", "pipe:0",
                                "-c:a", "libvorbis", "-q:a", str(quality), "-f", "ogg", tmp],
                               input=wav, capture_output=True)
            if p.returncode != 0:
                raise RuntimeError(p.stderr.decode(errors="replace").strip()[:200])
            os.replace(tmp, fn)
            stats["clips"] += 1
            stats["bytes"] += os.path.getsize(fn)
        except Exception as e:  # noqa: BLE001 - recorded, never silent
            stats["errors"].append("%s %d: %s" % (rel, path_id, e))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--files", help="comma-separated serialized files (default: all; 'none' for bundles only)")
    ap.add_argument("--bundles", action="store_true")
    ap.add_argument("--only-missing", action="store_true")
    ap.add_argument("--quality", type=float, default=5)
    a = ap.parse_args()
    data = os.path.join(a.game, "Cuphead_Data")
    if os.path.abspath(a.out).startswith(os.path.abspath(a.game)):
        sys.exit("--out must be outside the game folder")
    outdir = os.path.join(a.out, "audio")
    os.makedirs(outdir, exist_ok=True)
    stats = {"clips": 0, "bytes": 0, "empty": 0, "errors": []}
    t0 = time.time()
    names = [] if a.files == "none" else a.files.split(",") if a.files else sorted(
        f for f in os.listdir(data) if f == "globalgamemanagers" or f.startswith("level") or f.endswith(".assets"))
    for name in names:
        env = UnityPy.load(os.path.join(data, name))
        for fname, sf in list(env.files.items()):
            if hasattr(sf, "objects"):
                export(sf, os.path.basename(name) + ".chob", outdir, stats, a.only_missing, a.quality)
    if a.bundles:
        bdir = os.path.join(data, "StreamingAssets", "AssetBundles")
        for bname in sorted(os.listdir(bdir)):
            path = os.path.join(bdir, bname)
            if not os.path.isfile(path) or bname.endswith(".manifest"):
                continue
            env = UnityPy.load(path)

            def walk(files):
                for fname, sf in list(files.items()):
                    if hasattr(sf, "objects"):
                        export(sf, "bundles/%s/%s.chob" % (bname, os.path.basename(fname)), outdir, stats, a.only_missing, a.quality)
                    elif hasattr(sf, "files"):
                        walk(sf.files)
            walk(env.files)
    print("convert_audio: %d clips, %.1f MB, %d empty, %d errors in %.1f s" % (
        stats["clips"], stats["bytes"] / 1048576, stats["empty"], len(stats["errors"]), time.time() - t0))
    for e in stats["errors"][:30]:
        print("  error:", e)
    return 1 if stats["errors"] else 0


if __name__ == "__main__":
    sys.exit(main())
