#!/usr/bin/env python3
"""What a PC run of the console code still reads late: textures drawn without enough read-ahead, objects read
from the converted files in play, clips read from the drive in the frame.

    readahead_report.py <host_native log> [lead frames, default 10]

The log comes from host_native with the read-ahead traces on, a bot driving it:

    CH360_TRACE_TEX=work/GameData/ctex CH360_TRACE_PREFETCH=1 CH360_TRACE_CLIPS=1
    CH360_GUEST_ENV="CH360_TREE_BUDGET_MB=4 DOTNET_GCgen0size=100000 DOTNET_GCLOHCompact=1 CH360_WHOLE_FILE_KB=0
                     CH360_BLOCK_BUDGET_MB=4 CH360_HOLD_SCRIPTED=1 CH360_LOAD_SLICE_MS=20 CH360_TRACE_READS=1
                     CH360_TRACE_PREFETCH=1"
    work/console/native/host_native.exe --root <root> --frames N -- run --console 1 --data /GameData
        --save /<save copy> --bot /<script>

(the guest settings are the console's: its parsed-tree and block budgets, its collector). Each part, per scene
visit:
- textures: first drawn with no read-ahead, or one asked for fewer than <lead> frames before (on the console a
  page takes 50-80 ms to read behind others: a short lead is a load in the frame there);
- object reads in play: READ lines from a level's or map's hold to the next load ("again": a tree parsed before,
  dropped under the tree budget and read back: something built at load and let go, or read for a fact later);
- clips: CLIP lines, a short clip's first play read from the drive in the frame.
The PC has the console's code and budgets but not its memory: a texture let go for room on the console (its
on-demand and read-again counts, the SCENE GPU lines of its log) doesn't show here.
"""
import collections
import re
import sys


def textures(lines, lead):
    scene, scene_at, last = '?', 0, 0
    asked = {}
    misses = collections.defaultdict(list)
    for line in lines:
        if line.startswith('SCENE loaded '):
            scene, scene_at = line.split()[2], last
        m = re.match(r'PREFETCH frame (\d+)( wanted)?: (.*)', line)
        if m:
            last = int(m.group(1))
            asked.setdefault(m.group(3).strip(), last)
            continue
        m = re.match(r'TEX first drawn frame (\d+): (.*?), (\d+) KB(.*)', line)
        if m:
            f, key, kb = int(m.group(1)), m.group(2), int(m.group(3))
            last = f
            ahead = f - asked.get(key, f)
            if key != '(runtime)' and (key not in asked or ahead < lead):
                misses[scene].append((f, f - scene_at, kb, key, 'asked %d frames before' % ahead if key in asked else 'never asked'))
            continue
        m = re.search(r'frame (\d+)', line)
        if m and line.startswith(('HITCH', 'FRAMES', 'TEXSET')):
            last = int(m.group(1))
    print('# textures drawn with under %d frames of read-ahead' % lead)
    for sc, items in misses.items():
        print('== %s: %d textures, %d KB' % (sc, len(items), sum(i[2] for i in items)))
        for f, rel, kb, key, note in items:
            print('   frame %6d (+%5d)  %5d KB  %s  (%s)' % (f, rel, kb, key, note))


def reads(lines):
    visits, cur, scene = [], None, '?'
    for ln in lines:
        if ln.startswith('SCENE loaded '):
            cur, scene = None, ln.split()[2]
        elif ln.startswith('LEVEL hold '):
            cur = (scene, [])
            visits.append(cur)
        elif ln.startswith('READ ') and cur is not None:
            f = int(re.search(r'frame (\d+)', ln).group(1))
            b = int(re.search(r'blocks (\d+)', ln).group(1))
            parts = ln.split()
            cur[1].append((f, parts[5], ' again ' in ln, b, parts[1]))
    print('# objects read in play (from a hold to the next load: a burst of 20+ reads in a frame)')
    for scene, rs in visits:
        per = collections.Counter(r[0] for r in rs)
        burst = min((f for f, c in per.items() if c >= 20), default=None)
        play = [r for r in rs if burst is None or r[0] < burst]
        drive = [r for r in play if r[3] >= 1]
        print('== %s: %d object reads in play, %d from the drive' % (scene, len(play), len(drive)))
        by = collections.defaultdict(list)
        for f, t, again, _, file in drive:
            by[f].append('%s%s %s' % (t, ' again' if again else '', file))
        for f in sorted(by):
            print('   frame %6d: %s' % (f, ', '.join(by[f])[:200]))


def clips(lines):
    scene = '?'
    print('# clips read from the drive in the frame')
    for ln in lines:
        if ln.startswith('SCENE loaded '):
            scene = ln.split()[2]
        elif ln.startswith('CLIP read in the frame: '):
            print('   %-32s %s' % (scene, ln[len('CLIP read in the frame: '):].strip()))


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    lines = open(sys.argv[1], encoding='utf-8', errors='replace').read().splitlines()
    textures(lines, int(sys.argv[2]) if len(sys.argv) > 2 else 10)
    reads(lines)
    clips(lines)
    return 0


if __name__ == '__main__':
    sys.exit(main())
