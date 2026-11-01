#!/usr/bin/env python3
"""Read CHOB object files (format: tools/convert_objects.py).

    chob.py types  <file.chob>                    class counts
    chob.py dump   <file.chob> [--type T] [--path ID] [--depth N]
    chob.py scene  <file.chob>                    GameObject hierarchy with components

Also importable: load(path) -> Chob with .externals and .objects
{pathID: (classID, typeName, tree)}; PPtrs are PPtr(file, path) tuples.
"""
import argparse
import collections
import struct
import sys

PPtr = collections.namedtuple("PPtr", "file path")


class Chob:
    def __init__(self, data):
        if data[:4] != b"CHOB":
            raise ValueError("not a CHOB file")
        self.d = data
        self.p = 8
        n = self.u32()
        self.strings = []
        for _ in range(n):
            ln = self.u32()
            self.strings.append(self.d[self.p:self.p + ln].decode("utf-8", "surrogateescape"))
            self.p += ln
        self.externals = [self.strings[self.u32()] for _ in range(self.u32())]
        self.objects = {}
        for _ in range(self.u32()):
            path_id, class_id = struct.unpack_from("<qi", self.d, self.p)
            self.p += 12
            tname = self.strings[self.u32()]
            self.objects[path_id] = (class_id, tname, self.value())
        del self.d

    def u32(self):
        v = struct.unpack_from("<I", self.d, self.p)[0]
        self.p += 4
        return v

    def varint(self):
        z = shift = 0
        while True:
            b = self.d[self.p]
            self.p += 1
            z |= (b & 0x7F) << shift
            shift += 7
            if not b & 0x80:
                break
        return (z >> 1) ^ -(z & 1)

    def value(self):
        tag = self.d[self.p]
        self.p += 1
        if tag == 0: return None
        if tag == 1: return False
        if tag == 2: return True
        if tag == 3: return self.varint()
        if tag == 4:
            v = struct.unpack_from("<f", self.d, self.p)[0]; self.p += 4; return v
        if tag == 5:
            v = struct.unpack_from("<d", self.d, self.p)[0]; self.p += 8; return v
        if tag in (6, 7):
            ln = self.u32()
            b = self.d[self.p:self.p + ln]; self.p += ln
            return b.decode("utf-8", "surrogateescape") if tag == 6 else bytes(b)
        if tag == 8:
            return [self.value() for _ in range(self.u32())]
        if tag == 9:
            out = {}
            for _ in range(self.u32()):
                k = self.strings[self.u32()]
                out[k] = self.value()
            return out
        if tag == 10:
            f, pid = struct.unpack_from("<iq", self.d, self.p); self.p += 12
            return PPtr(f, pid)
        raise ValueError("bad tag %d at %d" % (tag, self.p - 1))


def load(path):
    with open(path, "rb") as f:
        return Chob(f.read())


def show(v, depth, indent=""):
    if isinstance(v, dict):
        if depth <= 0:
            return "{...%d}" % len(v)
        return "{" + ", ".join("%s: %s" % (k, show(x, depth - 1, indent)) for k, x in v.items()) + "}"
    if isinstance(v, list):
        if depth <= 0 or len(v) > 16:
            return "[%d items%s]" % (len(v), (": " + show(v[0], depth - 1)) if v and depth > 0 else "")
        return "[" + ", ".join(show(x, depth - 1) for x in v) + "]"
    if isinstance(v, bytes):
        return "<%d bytes>" % len(v)
    if isinstance(v, PPtr):
        return "->%d:%d" % (v.file, v.path)
    if isinstance(v, float):
        return "%g" % v
    return repr(v)


def scene(c):
    objs = c.objects
    comps = {}
    for pid, (_, t, tree) in objs.items():
        if t == "GameObject":
            comps[pid] = [x["component"].path for x in tree.get("m_Component", [])]

    def tr_of(go):
        for cp in comps.get(go, []):
            o = objs.get(cp)
            if o and o[1] in ("Transform", "RectTransform"):
                return cp
        return None

    def label(cp):
        o = objs.get(cp)
        if not o:
            return "?%d" % cp
        if o[1] == "MonoBehaviour":
            s = objs.get(o[2]["m_Script"].path) if o[2]["m_Script"].file == 0 else None
            return "MB:%s" % (s[2].get("m_ClassName") if s else "ext%d:%d" % tuple(o[2]["m_Script"]))
        return o[1]

    def walk(go, ind):
        _, _, g = objs[go]
        tr = tr_of(go)
        pos = objs[tr][2].get("m_LocalPosition") if tr else None
        p = " (%g,%g,%g)" % (pos["x"], pos["y"], pos["z"]) if pos else ""
        act = "" if g.get("m_IsActive", True) else " [inactive]"
        print("%s%s%s%s  %s" % (ind, g.get("m_Name"), act, p, " ".join(label(x) for x in comps[go] if x != tr)))
        if tr:
            for ch in objs[tr][2].get("m_Children", []):
                cg = objs.get(ch.path)
                if cg:
                    walk(cg[2]["m_GameObject"].path, ind + "  ")

    for pid in comps:
        tr = tr_of(pid)
        if tr is None or objs[tr][2].get("m_Father", PPtr(0, 0)).path == 0:
            walk(pid, "")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cmd", choices=["types", "dump", "scene"])
    ap.add_argument("file")
    ap.add_argument("--type")
    ap.add_argument("--path", type=int)
    ap.add_argument("--depth", type=int, default=4)
    a = ap.parse_args()
    c = load(a.file)
    if a.cmd == "types":
        n = collections.Counter(t for _, t, _ in c.objects.values())
        for t, k in n.most_common():
            print("%6d %s" % (k, t))
        print("externals:", c.externals)
    elif a.cmd == "dump":
        for pid, (cid, t, tree) in c.objects.items():
            if (a.type and t != a.type) or (a.path is not None and pid != a.path):
                continue
            print("%d %s(%d) %s" % (pid, t, cid, show(tree, a.depth)))
    else:
        scene(c)


if __name__ == "__main__":
    sys.exit(main())
