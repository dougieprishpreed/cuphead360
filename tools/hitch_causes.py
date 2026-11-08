#!/usr/bin/env python3
"""Every gameplay hitch in a console log, by cause.

    hitch_causes.py work/console/<run>/cuphead360.txt [scene-prefix]

Per play scene (maps and levels; menus and loads left out), each HITCH line (a frame over 50 ms) is put to
one cause: a GC pause, the host's texture loads, uploads or vblank wait (the HOST SLOW line beside it), the
host with no slow part said, or the game phase that took longest (a big allocation beside it: a spawn or a
load). Prints the counts and the ms over budget per cause, then a few examples of each. Reads only the log.
"""
import re, sys, collections

path = sys.argv[1]
only = sys.argv[2] if len(sys.argv) > 2 else None
lines = open(path, encoding='utf-8', errors='replace').read().splitlines()

said = collections.Counter(); unsaid = collections.Counter()
for ln in lines:
    m = re.match(r'FRAMES \d+-\d+: .*hitches (\d+)(?: \((\d+) not said[^)]*\))?; (\S+)', ln)
    if m:
        sc = m.group(3).split(',')[-1]
        said[sc] += int(m.group(1)) - int(m.group(2) or 0); unsaid[sc] += int(m.group(2) or 0)

by = collections.defaultdict(collections.Counter)      # scene -> cause -> count
ms = collections.defaultdict(collections.Counter)      # scene -> cause -> total ms over the frame budget
dur = collections.defaultdict(list)
examples = collections.defaultdict(list)
for i, ln in enumerate(lines):
    m = re.match(r'HITCH frame (\d+): (\d+) ms to the next frame; game ([\d.]+) \(([^)]*)\) list ([\d.]+) submit ([\d.]+), host and vblank ([\d.]+) ms;(.*); (\S+)$', ln)
    if not m: continue
    sc = m.group(9)
    if 'load_helper' in sc or sc in ('scene_start', 'scene_title', 'scene_slot_select'): continue
    if only and not sc.startswith(only): continue
    total = int(m.group(2))
    parts = {k: float(v) for k, v in re.findall(r'(\w+)=([\d.]+)', m.group(4))}
    parts['list'] = float(m.group(5)); parts['submit'] = float(m.group(6)); parts['host+vblank'] = float(m.group(7))
    rest = m.group(8)
    gc = re.search(r'GC (\d+)/(\d+)/(\d+) paused (\d+) ms', rest)
    alloc = re.search(r'allocated (\d+) KB', rest)
    dec = re.search(r'audio decode ([\d.]+) ms', rest)
    # The host's slow part next to it (logged just before or after the hitch line).
    host = None
    for j in range(max(0, i - 4), min(len(lines), i + 3)):
        h = re.match(r'HOST SLOW frame \d+: ([\d.]+) ms: boxes\+loads ([\d.]+) \((\d+) textures\), fence ([\d.]+), uploads ([\d.]+), acquire ([\d.]+)', lines[j])
        if h: host = tuple(float(x) for x in h.groups())
    over = total - 16.7
    if gc and int(gc.group(4)) >= 0.4 * total:
        cause = 'GC gen%s' % ('2' if gc.group(3) != '0' else '1' if gc.group(2) != '0' else '0')
    elif parts['submit'] >= 0.4 * total:
        if host and host[1] >= 0.4 * host[0]: cause = 'texture loads (host)'
        elif host and host[4] >= 0.4 * host[0]: cause = 'texture uploads (host)'
        elif host and host[5] >= 0.4 * host[0]: cause = 'vblank wait (host)'
        else: cause = 'host, no slow part said'
    else:
        k = max((p for p in parts if p not in ('submit',)), key=lambda p: parts[p])
        big = alloc and int(alloc.group(1)) > 400
        cause = 'game %s%s' % (k, ' (big allocation: spawn/load)' if big and k in ('update', 'animators', 'fixed', 'late') else '')
        if k == 'audio' and dec and float(dec.group(1)) > 0.3 * parts['audio']: cause = 'game audio (Vorbis decode)'
    by[sc][cause] += 1; ms[sc][cause] += over; dur[sc].append(total)
    if len(examples[cause]) < 3: examples[cause].append(ln[:230])

for sc in by:
    d = sorted(dur[sc])
    print('== %s: %d hitches said (%d more not said); median %d ms, 90%% %d ms, worst %d ms' % (
        sc, len(d), unsaid[sc], d[len(d) // 2], d[int(len(d) * 0.9)], d[-1]))
    for cause, n in by[sc].most_common():
        print('   %4d  %6.0f ms over  %s' % (n, ms[sc][cause], cause))
print()
tot = collections.Counter(); totms = collections.Counter()
for sc in by:
    for c, n in by[sc].items(): tot[c] += n; totms[c] += ms[sc][c]
print('== all gameplay scenes')
for c, n in tot.most_common(): print('   %4d  %6.0f ms over  %s' % (n, totms[c], c))
print()
for c, ex in examples.items():
    print('-- ' + c)
    for e in ex: print('   ' + e)
