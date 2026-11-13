#!/usr/bin/env python3
"""The console's texture memory replayed from a PC run, to compare ways of keeping and reading pages.

    texture_cache_sim.py <drawn trace> [--scene-from F] [--scene-to F] [--decoded MB ...] [--stored MB ...]
                         [--fill] [--usb MBps] [--decode MBps] [--hz 60|30]

The trace comes from host_native with CH360_TRACE_DRAWN=<file> (a bot driving it, the console's guest settings): each
drawn frame's textures, each read-ahead ask, each texture's key. Sizes come from the texture packs
(work/console/packs/CTEX*.PAK: each page's stored LZ4 bytes and its decoded size).

The model follows console/xsdk/main.cpp's Renderer: decoded pages up to a room (--decoded), made least recently used
first; a frame waits for the pages it draws (the loader reads those first); asks (RenderList's read-ahead) queue
behind them and are read while room can be made from pages not drawn for AHEAD_AGE frames, 8 MB in flight at most;
one loader thread reads a page's stored bytes from the drive (--usb MB/s plus a fixed cost a read) and decodes it
(--decode MB/s). --stored MB: a second tier of pages' stored bytes kept in memory (least recently used), each read
from the drive kept there; a page found there decodes without the drive. --fill: a scene's read-ahead (the
hourglass) also reads into that tier every page the scene's asks name later (an upper bound for filling it at load).

Frames marked as the loading screen wait without a hitch (the console draws its kept frames meanwhile). Prints,
per setting: frames that waited, the wait in all, hitches (a frame over --hitch ms late), pages read from the drive
and from memory.
"""
import argparse, collections, glob, os, struct, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def fnv64(key):
    h = 0xcbf29ce484222325
    for c in key.encode("utf-8"):
        h = ((h ^ c) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h


def pack_sizes():
    rows = {}
    for p in sorted(glob.glob(os.path.join(ROOT, "work", "console", "packs", "CTEX*.PAK"))):
        with open(p, "rb") as f:
            head = f.read(16)
            n = struct.unpack("<I", head[8:12])[0]
            data = f.read(24 * n)
        for i in range(n):
            hi, lo, off, st, sz, _ = struct.unpack_from("<6I", data, 24 * i)
            rows[(hi << 32) | lo] = (st, sz)
    return rows


def read_trace(path, lo, hi):
    """events: ('K', id, key) / ('P', id) / ('A', frame, id, wanted) / ('S', frame, begin) / ('F', frame, loading, ids)"""
    ev = []
    with open(path, encoding="utf-8", errors="replace") as f:
        for ln in f:
            p = ln.split()
            if not p:
                continue
            k = p[0]
            if k == "K" and len(p) >= 3:
                ev.append(("K", int(p[1]), p[2]))
            elif k == "P":
                ev.append(("P", int(p[1])))
            elif k == "A":
                fr = int(p[1])
                if lo <= fr <= hi:
                    ev.append(("A", fr, int(p[2]), int(p[3])))
            elif k == "S":
                fr = int(p[1])
                if lo <= fr <= hi:
                    ev.append(("S", fr, int(p[2])))
            elif k == "G":
                fr = int(p[1])
                if lo <= fr <= hi:
                    ev.append(("G", fr, int(p[2])))
            elif k == "F":
                fr = int(p[1])
                if lo <= fr <= hi:
                    ev.append(("F", fr, int(p[2]), [int(x) for x in p[3:]]))
    return ev


class Sim:
    AHEAD_AGE = 180
    PRE_CAP = 8 << 20
    PAGE_MAX = 4 << 20

    def __init__(self, a, sizes, decoded_mb, stored_mb):
        self.a = a
        self.AHEAD_AGE = a.ahead_age
        self.sizes = sizes
        self.room = decoded_mb << 20
        self.store_room = stored_mb << 20
        self.key = {}
        self.res = collections.OrderedDict()   # id -> decoded bytes, least recently used first
        self.last = {}                          # id -> frame last drawn or asked
        self.store = collections.OrderedDict()  # key -> stored bytes kept in memory, least recently used first
        self.store_bytes = 0
        self.bytes = 0
        self.q_drawn = collections.deque()      # the loader's drawn ring
        self.q_pre = collections.deque()        # its prefetch ring
        self.queued = {}                        # id -> 'drawn' / 'pre' (in a ring, not started)
        self.cur = None                         # (id, done ms, pre)
        self.t = 0.0                            # the loader's clock
        self.backlog = collections.deque()      # asks waiting for room (pre_backlog_)
        self.backset = set()
        self.loaded_once = set()
        self.st = collections.Counter()
        self.waits = []
        self.frame = 0
        self.hold = False
        self.all_keys = {}
        self.scene_store = set()
        self.fill_seen = set()
        self.prefill = []
        self.prefill_set = set()

    def size_of(self, i):
        k = self.key.get(i)
        if k is None:
            return None
        return self.sizes.get(k)

    def job_ms(self, i):
        k = self.key[i]
        stored, n = self.size_of(i)
        dec = n / (self.a.decode * 1048.576)
        if k in self.store:
            self.store.move_to_end(k)
            self.st["from memory"] += 1
            return dec + 1.0
        self.st["from the drive"] += 1
        self.keep_stored(k, stored)
        return self.a.seek + stored / (self.a.usb * 1048.576) + dec

    def keep_stored(self, k, stored):
        if self.store_room <= 0:
            return
        if k in self.store:
            self.store.move_to_end(k)
            return
        # the scene's own (filled at its load) are never let go for another's
        for old in [x for x in self.store if x not in self.scene_store]:
            if self.store_bytes + stored <= self.store_room:
                break
            self.store_bytes -= self.store.pop(old)
        if self.store_bytes + stored <= self.store_room:
            self.store[k] = stored
            self.store_bytes += stored

    def busy(self, i):
        return i in self.queued or (self.cur is not None and self.cur[0] == i)

    def loading_bytes(self, pre_only=False):
        b = 0
        for i, kind in self.queued.items():
            if not pre_only or kind == "pre":
                b += self.size_of(i)[1]
        if self.cur is not None and (not pre_only or self.cur[2]):
            b += self.size_of(self.cur[0])[1]
        return b

    def evict(self, need, aged_only):
        """Room for need bytes more: least recently used first, never what the last 3 frames drew (aged_only:
        only what nothing drew or asked for within AHEAD_AGE frames)."""
        for i in list(self.res.keys()):
            if self.bytes + need <= self.room:
                break
            l = self.last.get(i, -1)
            if l >= self.frame - 3 or (aged_only and l > self.frame - self.AHEAD_AGE):
                continue
            self.bytes -= self.res.pop(i)
            self.st["let go"] += 1
        return self.bytes + need <= self.room

    def complete(self, i, pre):
        n = self.size_of(i)[1]
        if i in self.res:
            return
        if self.bytes + n > self.room:
            self.evict(n, False)
        self.res[i] = n
        self.bytes += n
        self.st["loaded"] += 1
        if i in self.loaded_once:
            self.st["read again"] += 1
        self.loaded_once.add(i)
        if not pre:
            self.st["on demand"] += 1

    def advance(self, until):
        """The loader works until the clock reaches until (or its rings are empty)."""
        while True:
            if self.cur is not None:
                i, done, pre = self.cur
                if done > until:
                    return
                self.t = done
                self.cur = None
                self.complete(i, pre)
            q = self.q_drawn if self.q_drawn else self.q_pre
            if not q:
                self.t = max(self.t, until)
                return
            i = q.popleft()
            kind = self.queued.pop(i, None)
            if kind is None or i in self.res:
                continue
            start = max(self.t, until if self.t < until and False else self.t)
            self.cur = (i, start + self.job_ms(i), kind == "pre")

    def feed(self):
        while self.backlog:
            i = self.backlog[0]
            if i in self.res or self.busy(i) or self.size_of(i) is None:
                self.backlog.popleft()
                self.backset.discard(i)
                continue
            if self.loading_bytes(True) > self.PRE_CAP:
                return
            want = self.loading_bytes() + self.PAGE_MAX
            if self.bytes + want > self.room and not self.evict(want, True):
                self.st["held back"] += 1
                return
            self.backlog.popleft()
            self.backset.discard(i)
            self.queued[i] = "pre"
            self.q_pre.append(i)

    def draw(self, ids, now, loading):
        need = [i for i in ids if self.size_of(i) is not None]
        for i in need:
            self.last[i] = self.frame
            if i in self.res:
                self.res.move_to_end(i)
        missing = [i for i in need if i not in self.res]
        if not missing:
            return now
        for i in missing:
            if self.cur is not None and self.cur[0] == i:
                continue
            if self.queued.get(i) == "pre":
                self.q_pre.remove(i)
            if self.queued.get(i) != "drawn":
                self.queued[i] = "drawn"
                self.q_drawn.append(i)
        t = now
        while any(i not in self.res for i in missing):
            nxt = self.cur[1] if self.cur is not None else t
            self.advance(max(nxt, t))
            t = max(t, self.t)
            if self.cur is None and not self.q_drawn and not self.q_pre:
                break
        return t

    def run(self, ev):
        now = 0.0
        frame_ms = 1000.0 / self.a.hz
        for idx, e in enumerate(ev):
            k = e[0]
            if k == "K":
                self.key[e[1]] = e[2]
                self.res.pop(e[1], None)
            elif k == "P":
                self.key.pop(e[1], None)
            elif k == "S":
                if e[2] == 1 and self.a.fill:
                    self.fill_scene(e[1], ev, idx)
                if e[2] == 1 and self.a.guesses:
                    self.scene_store = set()
                    self.fill_seen = set()
                if e[2] == 0:
                    self.hold = True   # the load's last stage and the level hold: what was asked is read under the hourglass
                if e[2] == 2:
                    # the load's last stage over (the console's drop_load_asks): what memory held back isn't read in play
                    for i in self.backlog:
                        self.backset.discard(i)
                    self.backlog.clear()
            elif k == "G" and self.a.prefill:
                k2 = self.key.get(e[2])
                if k2 and self.sizes.get(k2) and e[2] not in self.prefill_set:
                    self.prefill_set.add(e[2])
                    self.prefill.append(e[2])
            elif k == "G":
                # the console's fill as the build asked for it (RenderList.FillAhead), in its order, kept under the hourglass
                if self.a.guesses:
                    k2 = self.key.get(e[2])
                    if k2 and self.sizes.get(k2) and k2 not in self.fill_seen:
                        self.fill_seen.add(k2)
                        self.keep_stored(k2, self.sizes.get(k2)[0])
                        if k2 in self.store:
                            if self.a.protect == "all":
                                self.scene_store.add(k2)
                            elif self.a.protect == "lru":
                                # the level's later asks let go first: older in the LRU than those asked before
                                self.store.move_to_end(k2, last=False)
            elif k == "A":
                _, fr, i, wanted = e
                if not wanted or self.size_of(i) is None:
                    continue
                self.last[i] = fr
                if i in self.res:
                    self.res.move_to_end(i)
                elif not self.busy(i) and i not in self.backset:
                    self.backlog.append(i)
                    self.backset.add(i)
                self.feed()
            elif k == "F":
                _, fr, loading, ids = e
                self.frame = fr
                self.advance(now)
                self.feed()
                if self.hold and not loading:
                    # the hold: the first frame shows once nothing it draws or asked for is still coming
                    while self.cur is not None or self.q_drawn or self.q_pre or self.backlog:
                        before = (self.cur, len(self.q_pre), len(self.backlog))
                        self.advance(self.cur[1] if self.cur is not None else now)
                        now = max(now, self.t)
                        self.feed()
                        if (self.cur, len(self.q_pre), len(self.backlog)) == before:
                            break
                    now = max(now, self.draw(ids, now, True))
                    if self.a.prefill:
                        for i in self.prefill:
                            if i in self.res or self.size_of(i) is None:
                                continue
                            n = self.size_of(i)[1]
                            if self.bytes + n > self.room:
                                continue
                            self.job_ms(i)
                            self.res[i] = n
                            self.res.move_to_end(i, last=False)   # not drawn yet: the first to go
                            self.bytes += n
                            self.loaded_once.add(i)
                            self.st["prefilled"] += 1
                        self.prefill = []
                        self.prefill_set = set()
                    self.hold = False
                    loading = 1
                t = self.draw(ids, now, loading)
                if t > now and not loading:
                    self.waits.append((fr, t - now))
                now = max(now, t) + frame_ms
        return self

    def fill_scene(self, frame, ev, idx):
        """Every page the scene draws or asks for afterwards, its stored bytes kept (as the load could read them all:
        an upper bound, keys taken from the whole trace)."""
        seen = set()
        self.scene_store = set()
        for e in ev[idx:]:
            if e[0] == "S" and e[2] == 1 and e[1] > frame:
                break
            ids = e[3] if e[0] == "F" else [e[2]] if e[0] == "A" and e[3] else []
            for i in ids:
                k = self.all_keys.get(i)
                if k and k not in seen and self.sizes.get(k):
                    seen.add(k)
                    self.keep_stored(k, self.sizes.get(k)[0])
        self.scene_store = set(seen)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("trace")
    ap.add_argument("--scene-from", type=int, default=0)
    ap.add_argument("--scene-to", type=int, default=1 << 30)
    ap.add_argument("--decoded", type=int, nargs="+", default=[140])
    ap.add_argument("--stored", type=int, nargs="+", default=[0])
    ap.add_argument("--fill", action="store_true")
    ap.add_argument("--guesses", action="store_true", help="fill from the trace's G lines (the console's own fill)")
    ap.add_argument("--protect", default="all", choices=["all", "none", "lru"],
                    help="the fill's pages: never let go in the scene (all), plain LRU in their order (none), or LRU with the later asked going first (lru)")
    ap.add_argument("--usb", type=float, default=10.0, help="MB/s from the drive")
    ap.add_argument("--seek", type=float, default=15.0, help="ms a read costs beyond its bytes")
    ap.add_argument("--decode", type=float, default=170.0, help="MB/s decoded (LZ4) and copied")
    ap.add_argument("--hz", type=float, default=60.0)
    ap.add_argument("--hitch", type=float, default=17.0)
    ap.add_argument("--ahead-age", type=int, default=180)
    ap.add_argument("--prefill", action="store_true", help="at a level's hold, decode its fill's pages (G lines, in order) into free room")
    ap.add_argument("--every", type=int, default=1, help="draw every nth traced frame (2: a 60 Hz trace played as the locked 30)")
    a = ap.parse_args()
    rows = pack_sizes()

    class Sizes(dict):
        def get(self, k, d=None):
            if k in self:
                return self[k]
            r = rows.get(fnv64(k))
            v = (r[0], max(r[1] - 28, 1)) if r else None
            self[k] = v
            return v
    sizes = Sizes()
    ev = read_trace(a.trace, a.scene_from, a.scene_to)
    # keys named before the window
    keys = [e for e in read_trace(a.trace, -1, -1) if e[0] in "KP"]
    ev = keys + [e for e in ev if e[0] not in "KP"] if a.scene_from > 0 else ev
    if a.every > 1:
        ev = [e for e in ev if e[0] != "F" or e[1] % a.every == 0]
    frames = sum(1 for e in ev if e[0] == "F" and not e[2])
    print("%d play frames" % frames)
    for d in a.decoded:
        for s in a.stored:
            sim = Sim(a, sizes, d - s, s)
            sim.all_keys = {e[1]: e[2] for e in keys if e[0] == "K"}
            sim.run(ev)
            w = sim.waits
            total = sum(x for _, x in w)
            hitches = sum(1 for _, x in w if x > a.hitch)
            print("decoded %3d MB + stored %2d MB%s: %4d frames waited, %6.0f ms in all, %4d over %.0f ms (worst %4.0f); "
                  "loaded %d (%d on demand, %d read again, %d prefilled), from the drive %d, from memory %d, held back %d" % (
                      d - s, s, " (filled)" if a.fill else " (its fill)" if a.guesses else "", len(w), total, hitches, a.hitch,
                      max((x for _, x in w), default=0), sim.st["loaded"], sim.st["on demand"], sim.st["read again"], sim.st["prefilled"],
                      sim.st["from the drive"], sim.st["from memory"], sim.st["held back"]))


if __name__ == "__main__":
    main()
