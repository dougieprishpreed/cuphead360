#!/usr/bin/env python3
"""Export the game's AudioClips as Ogg Vorbis files the engine decodes.

    convert_audio.py --game <GOG Cuphead folder> --out <GameData dir> [--files a,b] [--bundles] [--only-missing] [--quality Q]

Writes <out>/audio/<key>.ogg for every AudioClip, where <key> is the
converted object file's path below objects/ (slashes as "_", without
".chob") plus "_" and the path id: the same identity the textures use.
UnityPy decodes each clip (FSB5 Vorbis/PCM, through its bundled FMOD) to
WAV, and libvorbis (through the soundfile package's libsndfile) encodes it
to Ogg Vorbis at the clip's own sample rate and channel count: --quality 5,
the same as ffmpeg's -q:a 5 that made the files before (sample counts the
same, sizes within 1%). Reads the game folder only.
"""
import argparse
import io
import os
import sys
import time

import soundfile
import UnityPy

# libsndfile 1.2's Vorbis encoder brings the process down when a long clip (22 s, 989,362 frames) is written in one
# call; written in blocks it encodes the whole clip.
BLOCK = 8192


def encode(wav, path, quality):
    """WAV bytes to an Ogg Vorbis file; quality as ffmpeg's -q:a (0-10): libsndfile's Vorbis quality is 1 - level."""
    data, rate = soundfile.read(io.BytesIO(wav), dtype="float32", always_2d=True)
    level = min(1.0, max(0.0, 1.0 - quality / 10.0))
    with soundfile.SoundFile(path, "w", rate, data.shape[1], format="OGG", subtype="VORBIS", compression_level=level) as f:
        for i in range(0, len(data), BLOCK):
            f.write(data[i:i + BLOCK])


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
            encode(wav, tmp, quality)
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
