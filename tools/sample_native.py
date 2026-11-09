"""sample_native.py: a sampling profiler for host_native (the translated C on PC), hidden and headless.

    sample_native.py --wasm-map managed/Cuphead360.Player/obj/Release/net10.0/wasi-wasm/native/Cuphead360.Player.linkmap
               --exe-map host_native.map --log out.log --after REGEX --delay S --seconds S
               [--hz N] [--focus NAME]... [--out prof.txt] -- host_native.exe args...

Starts the program with no window, waits for REGEX in its output, then --delay seconds, then samples the
busiest thread's stack (dbghelp StackWalk64) for --seconds. Names: the exe's linker map (/MAP) gives the C
function at each address; fN is wasm function N, named from wasm-ld's map (defined functions in index order
after the imports). Prints self and inclusive shares, and for each --focus the callees beneath it and the
inclusive shares within it. --save-stacks keeps the named stacks; --load-stacks F reports on them again
(other --focus/--callers) without running anything.

The exe's map: relink work/console/native/obj/*.o with -Wl,/MAP:host_native_map.map (seconds; the same
code). From Git Bash set MSYS_NO_PATHCONV=1 so the player's /paths reach it unchanged. Example (the map):
    MSYS_NO_PATHCONV=1 python tools/sample_native.py --wasm-map <linkmap> --exe-map work/console/native/host_native_map.map
        --log work/managed/prof.log --after "SCENE scene_map_world_1: built" --delay 4 --seconds 15
        -- <full path of host_native_map.exe> --root . --frames 6000 -- run --console 1 --data /work/GameData
        --save /work/managed/<save copy> --bot /work/managed/bot_map_idle.txt"""
import argparse, bisect, ctypes, ctypes.wintypes as wt, os, re, subprocess, sys, time
from collections import Counter, defaultdict

ap = argparse.ArgumentParser()
ap.add_argument('--wasm-map')
ap.add_argument('--exe-map')
ap.add_argument('--log')
ap.add_argument('--after')
ap.add_argument('--save-stacks', help='write the named stacks (leaf first, one sample a line)')
ap.add_argument('--load-stacks', help='report on stacks written by --save-stacks instead of running')
ap.add_argument('--delay', type=float, default=5)
ap.add_argument('--after-count', type=int, default=1, help='wait for this many matches of --after')
ap.add_argument('--seconds', type=float, default=20)
ap.add_argument('--hz', type=float, default=400)
ap.add_argument('--focus', action='append', default=[])
ap.add_argument('--callers', action='append', default=[], help='functions to list the callers of (the frame above each)')
ap.add_argument('--out')
ap.add_argument('--top', type=int, default=50)
ap.add_argument('--raw', action='store_true', help="name the translated functions fN, as the C has them (build_managed.py's hot list)")
ap.add_argument('--imports', type=int, help="the wasm module's imported functions (default: counted in work/managed/c/player.h)")
ap.add_argument('cmd', nargs=argparse.REMAINDER)
a = ap.parse_args()
cmd = a.cmd[1:] if a.cmd and a.cmd[0] == '--' else a.cmd


def report(stacks, tid):
    N = max(1, len(stacks))
    selfc = Counter(st[0] for st in stacks if st)
    incl = Counter()
    for st in stacks:
        for n in set(st): incl[n] += 1
    out = []
    out.append('%d samples of thread %d' % (N, tid))
    out.append('\nSELF')
    for n, c in selfc.most_common(a.top): out.append('%6.2f%%  %s' % (100.0 * c / N, n))
    out.append('\nINCLUSIVE')
    for n, c in incl.most_common(a.top * 2): out.append('%6.2f%%  %s' % (100.0 * c / N, n))
    for fname in a.focus:
        below = Counter(); within = Counter(); selfin = 0; tot = 0
        for st in stacks:
            idx = [i for i, n in enumerate(st) if fname in n]
            if not idx: continue
            tot += 1
            i = idx[-1]   # the outermost match
            if i == 0: selfin += 1
            else: below[st[i - 1]] += 1
            for n in set(st[:i]): within[n] += 1
        out.append('\nFOCUS %s: %.2f%% of samples (self %.2f%%)' % (fname, 100.0 * tot / N, 100.0 * selfin / N))
        for n, c in below.most_common(40): out.append('%6.2f%%  %s' % (100.0 * c / N, n))
        out.append('  within it, inclusive:')
        for n, c in within.most_common(a.top): out.append('%6.2f%%  %s' % (100.0 * c / N, n))
    for fname in a.callers:
        above = Counter(); tot = 0
        for st in stacks:
            idx = [i for i, n in enumerate(st) if fname in n]
            if not idx: continue
            tot += 1
            i = idx[-1]   # the outermost match
            # the first caller that isn't the function itself or a compiler-generated thunk of it
            j = i + 1
            while j < len(st) and fname in st[j]: j += 1
            above[st[j] if j < len(st) else '(top)'] += 1
        out.append('\nCALLERS %s: %.2f%% of samples' % (fname, 100.0 * tot / N))
        for n, c in above.most_common(25): out.append('%6.2f%%  %s' % (100.0 * c / N, n))
    text = '\n'.join(out)
    print(text)
    if a.out:
        open(a.out, 'w', encoding='utf-8').write(text + '\n')


if a.load_stacks:
    with open(a.load_stacks, encoding='utf-8') as f:
        report([ln.rstrip('\n').split(';') for ln in f if ln.strip()], 0)
    sys.exit(0)
if not ((a.wasm_map or a.raw) and a.exe_map and a.log and a.after):
    ap.error('--wasm-map (or --raw), --exe-map, --log and --after are needed to run')

# wasm-ld map (the ILC build's .linkmap): CODE section input functions, in index order after the imports.
wnames = []
_inc = False
_sec = re.compile(r'^\s+-\s+[0-9a-f]+\s+[0-9a-f]+ ([A-Z_]+)$')
_fn = re.compile(r'^\s+-\s+[0-9a-f]+\s+[0-9a-f]+\s+\S.*?:\((.*)\)$')
with open(a.wasm_map or os.devnull, encoding='utf-8', errors='replace') as f:
    for line in f:
        line = line.rstrip()
        m = _sec.match(line)
        if m:
            if _inc: break
            _inc = m.group(1) == 'CODE'; continue
        if _inc:
            m = _fn.match(line)
            if m: wnames.append(m.group(1))
imports = a.imports
if imports is None:
    # w2c2 declares each imported function as i<len>_<module><name>(...) in the player's header.
    _h = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'work', 'managed', 'c', 'player.h')
    imports = len(set(re.findall(r'\b(i\d+_\w+)\(', open(_h, encoding='utf-8', errors='replace').read()))) if os.path.exists(_h) else 91


def wname(idx):
    k = idx - imports
    return wnames[k] if 0 <= k < len(wnames) else 'f%d' % idx


# exe map: code symbols (section 0001) with their RVA (preferred base 0x140000000).
addrs, syms = [], []
for line in open(a.exe_map, encoding='utf-8', errors='replace'):
    p = line.split()
    if len(p) >= 3 and p[0].startswith('0001:'):
        try:
            rva = int(p[2], 16) - 0x140000000
        except ValueError:
            continue
        addrs.append(rva); syms.append(p[1])
order = sorted(range(len(addrs)), key=lambda i: addrs[i])
addrs = [addrs[i] for i in order]; syms = [syms[i] for i in order]

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
dbg = ctypes.WinDLL('dbghelp', use_last_error=True)
psapi = ctypes.WinDLL('psapi', use_last_error=True)
k32.OpenThread.restype = wt.HANDLE
k32.OpenProcess.restype = wt.HANDLE
dbg.StackWalk64.argtypes = [wt.DWORD, wt.HANDLE, wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                            ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
dbg.SymInitialize.argtypes = [wt.HANDLE, ctypes.c_char_p, wt.BOOL]
k32.GetThreadContext.argtypes = [wt.HANDLE, ctypes.c_void_p]
k32.SuspendThread.argtypes = [wt.HANDLE]
k32.ResumeThread.argtypes = [wt.HANDLE]


class THREADENTRY32(ctypes.Structure):
    _fields_ = [('dwSize', wt.DWORD), ('cntUsage', wt.DWORD), ('th32ThreadID', wt.DWORD), ('th32OwnerProcessID', wt.DWORD),
                ('tpBasePri', wt.LONG), ('tpDeltaPri', wt.LONG), ('dwFlags', wt.DWORD)]


def threads(pid):
    snap = k32.CreateToolhelp32Snapshot(4, 0)
    te = THREADENTRY32(); te.dwSize = ctypes.sizeof(te)
    out = []
    ok = k32.Thread32First(snap, ctypes.byref(te))
    while ok:
        if te.th32OwnerProcessID == pid: out.append(te.th32ThreadID)
        ok = k32.Thread32Next(snap, ctypes.byref(te))
    k32.CloseHandle(snap)
    return out


def cpu_time(h):
    c, e, k, u = (wt.FILETIME() for _ in range(4))
    k32.GetThreadTimes(h, ctypes.byref(c), ctypes.byref(e), ctypes.byref(k), ctypes.byref(u))
    return (k.dwHighDateTime << 32 | k.dwLowDateTime) + (u.dwHighDateTime << 32 | u.dwLowDateTime)


log = open(a.log, 'wb')
proc = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, creationflags=0x08000000)
pat = re.compile(a.after)
t0 = time.time()
while True:
    time.sleep(0.05)
    if proc.poll() is not None:
        sys.exit('program ended before the trigger')
    with open(a.log, 'rb') as f:
        if len(pat.findall(f.read().decode('utf-8', 'replace'))) >= a.after_count: break
print('trigger after %.0f s' % (time.time() - t0), flush=True)
time.sleep(a.delay)

hp = k32.OpenProcess(0x1F0FFF, False, proc.pid)
dbg.SymSetOptions(0x2 | 0x10)   # undecorated, deferred loads
dbg.SymInitialize(hp, None, True)
mods = (wt.HMODULE * 1)(); need = wt.DWORD()
psapi.EnumProcessModules(hp, mods, ctypes.sizeof(mods), ctypes.byref(need))
base = mods[0] or 0
hts = [(tid, k32.OpenThread(0x1FFFFF, False, tid)) for tid in threads(proc.pid)]
c0 = {tid: cpu_time(h) for tid, h in hts}
time.sleep(1.0)
tid, ht = max(hts, key=lambda th: cpu_time(th[1]) - c0[th[0]])
print('sampling thread %d, exe base %#x' % (tid, base), flush=True)

ctx_buf = ctypes.create_string_buffer(1232 + 16)
ctx_addr = (ctypes.addressof(ctx_buf) + 15) & ~15
frame = ctypes.create_string_buffer(512)
fta = ctypes.cast(dbg.SymFunctionTableAccess64, ctypes.c_void_p).value
gmb = ctypes.cast(dbg.SymGetModuleBase64, ctypes.c_void_p).value
u64 = ctypes.c_uint64


def name_at(pc):
    rva = pc - base
    i = bisect.bisect_right(addrs, rva) - 1
    if i < 0 or rva < 0 or rva > addrs[-1] + 0x100000:
        return '?%x' % pc
    s = syms[i]
    m = re.match(r'^f(\d+)$', s)
    return (s if a.raw else wname(int(m.group(1)))) if m else s


samples = []
period = 1.0 / a.hz
end = time.time() + a.seconds
while time.time() < end and proc.poll() is None:
    if k32.SuspendThread(ht) == 0xFFFFFFFF: break
    try:
        ctypes.memset(ctx_addr, 0, 1232)
        ctypes.c_uint32.from_address(ctx_addr + 0x30).value = 0x10000B
        if not k32.GetThreadContext(ht, ctx_addr): continue
        rip = u64.from_address(ctx_addr + 0xF8).value
        rsp = u64.from_address(ctx_addr + 0x98).value
        rbp = u64.from_address(ctx_addr + 0xA0).value
        ctypes.memset(frame, 0, 512)
        for off, val in ((0, rip), (32, rbp), (48, rsp)):
            u64.from_buffer(frame, off).value = val
            ctypes.c_uint32.from_buffer(frame, off + 12).value = 3
        pcs = []
        while len(pcs) < 200 and dbg.StackWalk64(0x8664, hp, ht, ctypes.addressof(frame), ctx_addr, None, fta, gmb, None):
            pc = u64.from_buffer(frame, 0).value
            if pc == 0: break
            pcs.append(pc)
    finally:
        k32.ResumeThread(ht)
    samples.append(pcs)
    time.sleep(period)
proc.kill()
print('%d samples' % len(samples), flush=True)

cache = {}
stacks = []
for pcs in samples:
    st = []
    for pc in pcs:
        n = cache.get(pc)
        if n is None: n = cache[pc] = name_at(pc)
        st.append(n)
    stacks.append(st)
if a.save_stacks:
    with open(a.save_stacks, 'w', encoding='utf-8') as f:
        for st in stacks: f.write(';'.join(st) + '\n')
report(stacks, tid)
