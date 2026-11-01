#!/usr/bin/env python3
"""Export the game's Texture2D data as the GPU uses it (no recompression).

    convert_textures.py --game <GOG Cuphead folder> --out <GameData dir> [--files a,b] [--bundles] [--only-missing]

Writes <out>/textures/<key>.tex for every Texture2D, where <key> is the
converted object file's path below objects/ (slashes as "_", without
".chob") plus "_" and the path id, the same identity the engine and the
render captures use. File format (little-endian):

  'CHTX' u32 version(1) u32 unityTextureFormat u32 width u32 height
  u32 mipCount u32 byteCount  bytes (mip 0 .. n as Unity stores them)

Block-compressed formats (DXT1/DXT5/BC7) stay compressed; crunched DXT
is unpacked to plain DXT blocks. Reads the game
folder only.
"""
import argparse
import os
import struct
import sys
import time

import UnityPy
import texture2ddecoder


def key_for(objects_rel, path_id):
    rel = objects_rel.replace("\\", "/")
    if rel.endswith(".chob"):
        rel = rel[:-5]
    return rel.replace("/", "_") + "_%d" % path_id


def export(sf, rel, outdir, stats, only_missing):
    for path_id, obj in sf.objects.items():
        if obj.type is None or obj.type.name != "Texture2D":
            continue
        fn = os.path.join(outdir, key_for(rel, path_id) + ".tex")
        if only_missing and os.path.exists(fn):
            continue
        try:
            tex = obj.read()
            sd = getattr(tex, "m_StreamData", None)
            if sd is not None and not getattr(sd, "path", "") and not tex.image_data:
                data = b""   # a dynamic font's texture: Unity fills it at run time
            else:
                data = tex.get_image_data() or b""   # reads streamed (.resS) data too
            fmt = int(getattr(tex.m_TextureFormat, "value", tex.m_TextureFormat))
            if fmt in (28, 29) and data:
                # Crunched DXT1/DXT5: unpack to plain blocks the GPU samples directly.
                data = texture2ddecoder.unpack_unity_crunch(data)
                fmt = 10 if fmt == 28 else 12
            mips = int(getattr(tex, "m_MipCount", 1) or 1)
            head = b"CHTX" + struct.pack("<6I", 1, fmt, tex.m_Width, tex.m_Height, mips, len(data))
            tmp = fn + ".tmp"
            with open(tmp, "wb") as f:
                f.write(head)
                f.write(data)
            os.replace(tmp, fn)
            stats["textures"] += 1
            stats["bytes"] += len(data)
        except Exception as e:  # noqa: BLE001 - recorded, never silent
            stats["errors"].append("%s %d: %s" % (rel, path_id, e))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--files", help="comma-separated serialized files (default: all; 'none' for bundles only)")
    ap.add_argument("--bundles", action="store_true")
    ap.add_argument("--only-missing", action="store_true")
    a = ap.parse_args()
    data = os.path.join(a.game, "Cuphead_Data")
    if os.path.abspath(a.out).startswith(os.path.abspath(a.game)):
        sys.exit("--out must be outside the game folder")
    outdir = os.path.join(a.out, "textures")
    os.makedirs(outdir, exist_ok=True)
    stats = {"textures": 0, "bytes": 0, "errors": []}
    t0 = time.time()
    names = [] if a.files == "none" else a.files.split(",") if a.files else sorted(
        f for f in os.listdir(data) if f == "globalgamemanagers" or f.startswith("level") or f.endswith(".assets")) + [
        "Resources/unity default resources", "Resources/unity_builtin_extra"]
    for name in names:
        env = UnityPy.load(os.path.join(data, name))
        for fname, sf in list(env.files.items()):
            if hasattr(sf, "objects"):
                export(sf, os.path.basename(name) + ".chob", outdir, stats, a.only_missing)
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
                        export(sf, "bundles/%s/%s.chob" % (bname, os.path.basename(fname)), outdir, stats, a.only_missing)
                    elif hasattr(sf, "files"):
                        walk(sf.files)
            walk(env.files)
    print("convert_textures: %d textures, %.1f MB, %d errors in %.1f s" % (stats["textures"], stats["bytes"] / 1048576, len(stats["errors"]), time.time() - t0))
    for e in stats["errors"][:30]:
        print("  error:", e)
    return 1 if stats["errors"] else 0


if __name__ == "__main__":
    sys.exit(main())
