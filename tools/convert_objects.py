#!/usr/bin/env python3
"""Convert Cuphead's Unity serialized files into CHOB object files.

    convert_objects.py --game <GOG Cuphead folder> --out <GameData dir> [--files a,b,...] [--bundles]

Each serialized file (globalgamemanagers, levelN, *.assets, and the files
inside the StreamingAssets asset bundles with --bundles) becomes
<out>/objects/<name>.chob holding every object as a tagged value tree:

  built-in classes   the fields UnityPy reads with its Unity 2017.4 class
                     database (the GOG build has no type trees)
  MonoBehaviour      m_GameObject, m_Enabled, m_Script, m_Name, plus "_raw":
                     the script's serialized field bytes, which the engine
                     decodes against the real managed type (Unity's rules)
  bulk data          texture pixels and audio sample data are left out
                     ("_omitted"); the media converter handles them
  Shader             name only ({"m_Name"}); the port draws with its own
                     shaders, but materials and Shader.Find need the object
  TextAsset          m_Script as exact bytes (tag 7)
  byte lists         integer lists of 4096+ values 0-255 (fonts' m_FontData,
                     index buffers) as bytes (tag 7)

CHOB v1 (little-endian):
  'CHOB' u32 version
  u32 n, strings (u32 len, utf-8)       keys, type names, external paths
  u32 n, externals (u32 string)         m_FileID 1..n
  u32 n, objects: i64 pathID, i32 classID, u32 type-name string, value
  value: u8 tag
    0 null | 1 false | 2 true | 3 int (zigzag LEB128) | 4 f32 | 5 f64
    6 string (u32 len, utf-8) | 7 bytes (u32 len) | 8 list (u32 n, values)
    9 map (u32 n, (u32 key string, value)...) | 10 pptr (i32 fileID, i64 pathID)

Reads the game folder only; never writes into it.
"""
import argparse
import os
import struct
import sys
import time

import UnityPy

OMIT_KEYS = {"image data", "m_StreamData", "m_AudioData", "m_Resource", "m_PlatformBlob"}
OMIT_CLASSES = {"CGProgram"}             # replaced by the port's own shaders
NAME_ONLY_CLASSES = {"Shader"}           # kept as {"m_Name"} so references resolve


class Writer:
    def __init__(self):
        self.strings = {}
        self.slist = []
        self.buf = bytearray()

    def sid(self, s):
        i = self.strings.get(s)
        if i is None:
            i = self.strings[s] = len(self.slist)
            self.slist.append(s)
        return i

    def u8(self, v): self.buf.append(v)
    def u32(self, v): self.buf += struct.pack("<I", v)
    def i32(self, v): self.buf += struct.pack("<i", v)
    def i64(self, v): self.buf += struct.pack("<q", v)

    def varint(self, v):
        z = (v << 1) ^ (v >> 63)
        z &= (1 << 64) - 1
        while True:
            b = z & 0x7F
            z >>= 7
            if z:
                self.buf.append(b | 0x80)
            else:
                self.buf.append(b)
                return

    def value(self, v):
        if v is None:
            self.u8(0)
        elif v is True:
            self.u8(2)
        elif v is False:
            self.u8(1)
        elif isinstance(v, int):
            if -(1 << 63) <= v < (1 << 63):
                self.u8(3); self.varint(v)
            else:   # u64 above i64 range: keep the bit pattern
                self.u8(3); self.varint(v - (1 << 64))
        elif isinstance(v, float):
            f = struct.unpack("<f", struct.pack("<f", v))[0] if abs(v) < 3.4e38 else None
            if f is not None and (f == v or v != v):
                self.u8(4); self.buf += struct.pack("<f", v)
            else:
                self.u8(5); self.buf += struct.pack("<d", v)
        elif isinstance(v, str):
            b = v.encode("utf-8", "surrogateescape")
            self.u8(6); self.u32(len(b)); self.buf += b
        elif isinstance(v, (bytes, bytearray, memoryview)):
            self.u8(7); self.u32(len(v)); self.buf += bytes(v)
        elif isinstance(v, dict):
            if len(v) == 2 and "m_FileID" in v and "m_PathID" in v:
                self.u8(10); self.i32(v["m_FileID"]); self.i64(v["m_PathID"])
                return
            self.u8(9); self.u32(len(v))
            for k, x in v.items():
                self.u32(self.sid(k))
                self.value("_omitted" if k in OMIT_KEYS and x not in (None, "", b"") else x)
        elif isinstance(v, (list, tuple)) and len(v) >= 4096 and all(type(x) is int and 0 <= x <= 255 for x in v):
            # Byte blobs UnityPy reads as integer lists (a font's m_FontData,
            # mesh index buffers): raw bytes, which the engine skips in one
            # step and copies whole. It reads such lists as bytes either way.
            self.u8(7); self.u32(len(v)); self.buf += bytes(v)
        elif isinstance(v, (list, tuple)):
            self.u8(8); self.u32(len(v))
            for x in v:
                self.value(x)
        else:
            # Unknown UnityPy object: its dict view.
            self.value(getattr(v, "__dict__", str(v)))


def mono_behaviour(obj):
    """Header fields plus the raw script field bytes."""
    raw = obj.get_raw_data()
    p = 0
    go_file, go_path = struct.unpack_from("<iq", raw, p); p += 12
    enabled = raw[p] != 0; p += 4
    sc_file, sc_path = struct.unpack_from("<iq", raw, p); p += 12
    n = struct.unpack_from("<I", raw, p)[0]; p += 4
    name = raw[p:p + n].decode("utf-8", "surrogateescape"); p += n
    p = (p + 3) & ~3
    return {
        "m_GameObject": {"m_FileID": go_file, "m_PathID": go_path},
        "m_Enabled": enabled,
        "m_Script": {"m_FileID": sc_file, "m_PathID": sc_path},
        "m_Name": name,
        "_raw": raw[p:],
    }


def shader_name(obj):
    """A Shader's name is m_ParsedForm.m_Name; read only that much."""
    try:
        t = obj.read_typetree()
        pf = t.get("m_ParsedForm")
        return (pf or {}).get("m_Name") or t.get("m_Name") or ""
    except Exception:  # noqa: BLE001 - a stub without a name still resolves
        return ""


def convert_file(sf, out_path, stats):
    w = Writer()
    objects = []
    for path_id, obj in sf.objects.items():
        tname = obj.type.name if obj.type else "Class%d" % obj.class_id
        if tname in OMIT_CLASSES:
            continue
        try:
            if tname == "MonoBehaviour":
                tree = mono_behaviour(obj)
            elif tname in NAME_ONLY_CLASSES:
                tree = {"m_Name": shader_name(obj)}
            else:
                tree = obj.read_typetree()
                if tname == "TextAsset" and isinstance(tree.get("m_Script"), str):
                    tree["m_Script"] = tree["m_Script"].encode("utf-8", "surrogateescape")
        except Exception as e:  # noqa: BLE001 - recorded, never silent
            tree = {"_error": "%s: %s" % (type(e).__name__, e)}
            stats["errors"].append("%s %d %s: %s" % (out_path, path_id, tname, e))
        objects.append((path_id, obj.class_id, tname, tree))
        stats["objects"] += 1
    body = Writer()
    body.strings, body.slist = w.strings, w.slist
    body.u32(len(sf.externals))
    for ext in sf.externals:
        body.u32(body.sid(ext.path))
    body.u32(len(objects))
    for path_id, class_id, tname, tree in objects:
        body.i64(path_id); body.i32(class_id); body.u32(body.sid(tname))
        body.value(tree)
    head = bytearray(b"CHOB") + struct.pack("<I", 1) + struct.pack("<I", len(body.slist))
    for s in body.slist:
        b = s.encode("utf-8", "surrogateescape")
        head += struct.pack("<I", len(b)) + b
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    tmp = out_path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(head)
        f.write(body.buf)
    os.replace(tmp, out_path)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--files", help="comma-separated serialized file names (default: all in Cuphead_Data; 'none' for bundles only)")
    ap.add_argument("--bundles", action="store_true", help="also convert the files inside StreamingAssets asset bundles")
    a = ap.parse_args()
    data = os.path.join(a.game, "Cuphead_Data")
    if not os.path.exists(os.path.join(data, "globalgamemanagers")):
        sys.exit("not a Cuphead folder (no Cuphead_Data/globalgamemanagers): " + a.game)
    if os.path.abspath(a.out).startswith(os.path.abspath(a.game)):
        sys.exit("--out must be outside the game folder")
    names = [] if a.files == "none" else a.files.split(",") if a.files else sorted(
        f for f in os.listdir(data)
        if f == "globalgamemanagers" or f.startswith("level") or f.endswith(".assets")) + [
        # Unity's built-in resources ("library/unity default resources",
        # "resources/unity_builtin_extra" externals): default font, sprite
        # material, UI sprites.
        "Resources/unity default resources", "Resources/unity_builtin_extra"]
    stats = {"objects": 0, "errors": [], "files": 0}
    t0 = time.time()
    for name in names:
        env = UnityPy.load(os.path.join(data, name))
        for fname, sf in env.files.items():
            if not hasattr(sf, "objects"):
                continue
            convert_file(sf, os.path.join(a.out, "objects", os.path.basename(name) + ".chob"), stats)
            stats["files"] += 1
    if a.bundles:
        bdir = os.path.join(data, "StreamingAssets", "AssetBundles")
        for bname in sorted(os.listdir(bdir)):
            path = os.path.join(bdir, bname)
            if not os.path.isfile(path) or bname.endswith(".manifest"):
                continue
            env = UnityPy.load(path)

            def walk(files):
                for fname, sf in files.items():
                    if hasattr(sf, "objects"):
                        convert_file(sf, os.path.join(a.out, "objects", "bundles", bname, os.path.basename(fname) + ".chob"), stats)
                        stats["files"] += 1
                    elif hasattr(sf, "files"):   # a bundle: its serialized files are inside
                        walk(sf.files)
            walk(env.files)
    print("convert_objects: %d files, %d objects, %d errors in %.1f s" % (stats["files"], stats["objects"], len(stats["errors"]), time.time() - t0))
    for e in stats["errors"][:30]:
        print("  error:", e)
    return 1 if stats["errors"] else 0


if __name__ == "__main__":
    sys.exit(main())
