#!/usr/bin/env python3
"""The game's large sprite meshes, by atlas: those the render list draws in strips.

    large_meshes.py --data work/GameData [--min-corners 3000] [--part 1200]

A sprite whose mesh has --min-corners triangle corners or more (Sprites.StripCorners) is split once into
strips of --part corners (Sprites.StripPart) across its longer side, and the strips off the camera's view
are left out before the console's glue reads them (RenderList.Strips). This lists every such sprite in
the game's atlases with its corners as drawn (tools/simplify_meshes.py's meshes.bin where it simplified
one) and its strips, per atlas (each level's atlases are named after it), so the split is known to cover
every level, not only the one it was found in (Hilda Berg's night sky).

Reads <data>/objects/bundles/atlas_* and <data>/objects/meshes.bin. Writes nothing.
"""
import argparse
import collections
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import chob  # noqa: E402
from simplify_meshes import render_key  # noqa: E402


def simplified(path):
    """meshes.bin: render key -> index count."""
    out = {}
    if not os.path.exists(path):
        return out
    b = open(path, 'rb').read()
    if b[:4] != b'CHMS' or struct.unpack_from('<I', b, 4)[0] != 2:
        return out
    n, at = struct.unpack_from('<I', b, 8)[0], 12
    for _ in range(n):
        kl = struct.unpack_from('<H', b, at)[0]
        at += 2
        key = b[at:at + kl].decode('ascii')
        at += kl
        nv = struct.unpack_from('<I', b, at)[0]
        at += 4 + nv * 8
        ni = struct.unpack_from('<I', b, at)[0]
        at += 4 + ni * 2
        out[key] = ni
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--data', default='work/GameData')
    ap.add_argument('--min-corners', type=int, default=3000)
    ap.add_argument('--part', type=int, default=1200)
    a = ap.parse_args()
    objects = os.path.join(a.data, 'objects')
    bundles = os.path.join(objects, 'bundles')
    simple = simplified(os.path.join(objects, 'meshes.bin'))
    by_atlas = collections.defaultdict(list)
    for atlas_dir in sorted(os.listdir(bundles)):
        if not atlas_dir.startswith('atlas_'):
            continue
        for f in sorted(os.listdir(os.path.join(bundles, atlas_dir))):
            if not f.endswith('.chob'):
                continue
            c = chob.load(os.path.join(bundles, atlas_dir, f))
            for _, tn, tree in c.objects.values():
                if tn != 'Sprite':
                    continue
                corners = len(tree['m_RD']['m_IndexBuffer']) // 2
                key = render_key(tree['m_RenderDataKey'])
                drawn = simple.get(key, corners)
                if drawn >= a.min_corners:
                    by_atlas[atlas_dir].append((drawn, corners, tree['m_Name']))
    total = 0
    for atlas, lst in sorted(by_atlas.items(), key=lambda kv: -sum(x[0] for x in kv[1])):
        lst.sort(reverse=True)
        total += len(lst)
        print('%s: %d sprites, %d corners' % (atlas, len(lst), sum(x[0] for x in lst)))
        for drawn, corners, name in lst[:6]:
            print('    %-48s %6d corners%s, %d strips' % (name, drawn, '' if drawn == corners else ' (%d before simplifying)' % corners,
                                                    (drawn + a.part - 1) // a.part))
        if len(lst) > 6:
            print('    ... %d more' % (len(lst) - 6))
    print('%d sprites in %d atlases are drawn in strips' % (total, len(by_atlas)))


if __name__ == '__main__':
    sys.exit(main())
