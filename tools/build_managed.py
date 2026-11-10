#!/usr/bin/env python3
"""Build the Cuphead360 managed player from the player's own GOG Cuphead files.

The game's C# (Assembly-CSharp and the Unity, Rewired and Galaxy assemblies it
uses) runs as its original IL on .NET. Unity's engine boundary - every method
whose body lives in the native Unity player or a native library - is
redirected into Cuphead360.Engine (managed/Cuphead360.Engine/Impl).

Stages (each can be run alone with --stage a,b,...):
  patcher   build the Cecil patcher (managed/Cuphead360.Patcher)
  publicize copy the game's managed DLLs (minus Unity's Mono class libraries)
            to managed/lib/public with engine-side internals made public
  gen       managed/Cuphead360.Engine/Generated/Icalls.g.cs: a default stub
            for every engine method Impl/ does not implement yet
  engine    build Cuphead360.Engine against lib/public
  redirect  managed/lib/patched: engine methods call the engine
  jit       build the player for .NET on this PC (the quick oracle)
  census    every type loads and every method compiles on this runtime
  selftest  Unity lifecycle semantics through the patched assemblies
  wasm      NativeAOT-LLVM -> work/managed/wasm/Cuphead360.Player.wasm
  c         w2c2 -> work/managed/c/*.c (+ import audit)

Usage:
  build_managed.py [--game "<GOG Cuphead folder>"] [--stage all|...] [--jobs N]

Requirements: .NET 10 SDK; for wasm the wasi-sdk 29 (WASI_SDK_PATH, or
C:\\wasi-sdk-29.0-x86_64-windows) and the dotnet-experimental NuGet feed; for c
a built w2c2 (--w2c2, or Spire360's build next to this repository).
Nothing under managed/lib, work/ or build/ may be committed: it is derived
from the player's game.
"""
import argparse
import os
import re
import shutil
import struct
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANAGED = os.path.join(ROOT, "managed")
LIB_PUBLIC = os.path.join(MANAGED, "lib", "public")
LIB_PATCHED = os.path.join(MANAGED, "lib", "patched")
OUT = os.path.join(ROOT, "work", "managed")
STAGES = ["patcher", "publicize", "gen", "engine", "redirect", "jit", "census", "selftest", "wasm", "c"]
DEFAULT_STAGES = ["patcher", "publicize", "gen", "engine", "redirect", "jit", "census", "selftest"]
JOBS = 2


def run(cmd, **kw):
    if cmd[:2] in (["dotnet", "build"], ["dotnet", "publish"]):
        cmd = cmd + ["-m:%d" % JOBS, "-nologo"]
    print("+", " ".join(str(c) for c in cmd), flush=True)
    kw.setdefault("check", True)
    return subprocess.run(cmd, **kw)


def patcher(*args):
    return run(["dotnet", os.path.join(OUT, "patcher", "Cuphead360.Patcher.dll")] + list(args))


def managed_dir(game):
    d = os.path.join(game, "Cuphead_Data", "Managed")
    if not os.path.exists(os.path.join(d, "Assembly-CSharp.dll")):
        sys.exit(f"Assembly-CSharp.dll not found under {d}: pass --game <GOG Cuphead folder>")
    return d


def stage_patcher(a):
    run(["dotnet", "build", os.path.join(MANAGED, "Cuphead360.Patcher"), "-c", "Release", "-o", os.path.join(OUT, "patcher"), "-v", "q"])


DUMPED = os.path.join(OUT, "dumped")


def stage_publicize(a):
    # The game's Managed folder plus the assemblies the game decrypts and
    # loads from bytes at run time (Rewired's platform layer), which the
    # player dumps to work/managed/dumped the first time it meets them.
    staging = os.path.join(OUT, "input")
    shutil.rmtree(staging, ignore_errors=True)
    shutil.copytree(managed_dir(a.game), staging)
    if os.path.isdir(DUMPED):
        for f in sorted(os.listdir(DUMPED)):
            if f.endswith(".dll"):
                shutil.copy2(os.path.join(DUMPED, f), os.path.join(staging, f))
                print("publicize: + run-time assembly", f)
    shutil.rmtree(LIB_PUBLIC, ignore_errors=True)
    patcher("publicize", "--in", staging, "--out", LIB_PUBLIC)
    # Rewired's decryption helpers keep their visibility: Rewired finds their
    # members by reflection with non-public binding flags.
    if os.path.isdir(DUMPED):
        for f in sorted(os.listdir(DUMPED)):
            if f.startswith("Rewired_Config_Manager") and f.endswith(".dll"):
                shutil.copy2(os.path.join(DUMPED, f), os.path.join(LIB_PUBLIC, f))


def stage_gen(a):
    patcher("gen", "--in", LIB_PUBLIC, "--impl", os.path.join(MANAGED, "Cuphead360.Engine", "Impl"),
            "--out", os.path.join(MANAGED, "Cuphead360.Engine", "Generated", "Icalls.g.cs"),
            "--list", os.path.join(OUT, "engine_methods.json"))


def stage_engine(a):
    run(["dotnet", "build", os.path.join(MANAGED, "Cuphead360.Engine"), "-c", "Release", "-o", os.path.join(OUT, "engine"), "-v", "q"])


def stage_redirect(a):
    shutil.rmtree(LIB_PATCHED, ignore_errors=True)
    patcher("redirect", "--in", LIB_PUBLIC, "--engine", os.path.join(OUT, "engine", "Cuphead360.Engine.dll"),
            "--out", LIB_PATCHED, "--report", os.path.join(OUT, "redirect_report.json"))


def stage_jit(a):
    run(["dotnet", "build", os.path.join(MANAGED, "Cuphead360.Player"), "-c", "Release", "-o", os.path.join(OUT, "jit"), "-v", "q"])


def player(*args):
    return run(["dotnet", os.path.join(OUT, "jit", "Cuphead360.Player.dll")] + list(args))


def stage_census(a):
    player("census")


def stage_selftest(a):
    player("selftest")


def find_wasi_sdk():
    sdk = os.environ.get("WASI_SDK_PATH")
    if sdk and os.path.isdir(sdk):
        return sdk
    for cand in (r"C:\wasi-sdk-29.0-x86_64-windows", os.path.join(os.path.dirname(ROOT), "wasi-sdk-29.0-x86_64-windows"),
                 "/opt/wasi-sdk"):
        if os.path.isdir(cand):
            return cand
    sys.exit("wasi-sdk 29 not found: set WASI_SDK_PATH")


def stage_wasm(a):
    sdk = find_wasi_sdk()
    env = dict(os.environ, MSBuildEnableWorkloadResolver="false", WASI_SDK_PATH=sdk)
    out = os.path.join(OUT, "wasm")
    run(["dotnet", "publish", os.path.join(MANAGED, "Cuphead360.Player"), "-c", "Release", "-r", "wasi-wasm",
         "-p:CupheadWasm=true", "-p:IlcLlvmTarget=wasm32-unknown-wasip1", "-p:LinkerFlavor=lld", "-p:DebugType=none",
         "-p:CupheadIlcParallelism=%d" % JOBS, "-o", out,
         "-p:RestoreConfigFile=" + os.path.join(MANAGED, "wasm-nuget.config")], env=env)
    wasm = os.path.join(out, "Cuphead360.Player.wasm")
    with open(wasm, "rb") as f:
        head = f.read(8)
    print("player.wasm:", os.path.getsize(wasm) // 1024, "KB,", "core module" if head[4:8] == b"\x01\x00\x00\x00" else "NOT a core module")


def find_w2c2(a):
    if a.w2c2:
        return a.w2c2
    cand = os.path.join(os.path.dirname(ROOT), "spire360", "out", "logic", "w2c2-build", "Release", "w2c2.exe")
    if os.path.exists(cand):
        return cand
    found = shutil.which("w2c2")
    if found:
        return found
    sys.exit("w2c2 not found: pass --w2c2")


def stage_c(a):
    cdir = os.path.join(OUT, "c")
    shutil.rmtree(cdir, ignore_errors=True)
    os.makedirs(cdir)
    # w2c2 names the module after the file it reads: translate "player.wasm"
    # from the output folder so the C symbols are m6_player...
    shutil.copyfile(os.path.join(OUT, "wasm", "Cuphead360.Player.wasm"), os.path.join(cdir, "player.wasm"))
    run([find_w2c2(a), "-f", "2000", "player.wasm", "player.c"], cwd=cdir)
    os.remove(os.path.join(cdir, "player.wasm"))
    spire_tools = os.path.join(os.path.dirname(ROOT), "spire360", "tools")
    if os.path.isdir(spire_tools):
        run([sys.executable, os.path.join(spire_tools, "w2c2_fix_tables.py"), os.path.join(cdir, "player.c")])
        # Trapping stubs for the imports neither srt nor the console host
        # implements (wasi:http and stream calls the game never makes); the
        # console host (console/ch360_imports.c) defines the "ch360" ones.
        stubs = os.path.join(cdir, "player_stubs.c")
        run([sys.executable, os.path.join(spire_tools, "w2c2_imports.py"), os.path.join(cdir, "player.h"), "--stubs", stubs], check=False)
        if os.path.exists(stubs):
            with open(stubs, "rb") as f:
                text = f.read().decode("utf-8")
            text = re.sub(r"(?ms)^\w[^\n]*\bi5_ch360\w*\([^)]*\) \{.*?^\}\r?\n", "", text)
            with open(stubs, "wb") as f:
                f.write(text.encode("utf-8"))
    if a.segments_in_c:
        compress_data_segments_in_c(os.path.join(cdir, "player.c"))
    else:
        compress_data_segments(os.path.join(cdir, "player.c"))
    if a.no_hoist:
        print("the memory base is not hoisted (--no-hoist)")
    else:
        hoist_memory_base(cdir)
    if a.cold_os:
        split_cold(cdir, LINKMAP, HOT_LIST)
    else:
        print("the -Os split is off (--cold-os): every function -O2")
    print(len([f for f in os.listdir(cdir) if f.endswith(".c")]), "C files in", cdir)


# The translated functions' run-time weight (tools/hot_functions.py: sampled on host_native over every level), by
# method name, which stays with a method across builds where its number (fN) does not.
HOT_LIST = os.path.join(OUT, "hot_functions.txt")
LINKMAP = os.path.join(ROOT, "managed", "Cuphead360.Player", "obj", "Release", "net10.0", "wasi-wasm", "native",
                       "Cuphead360.Player.linkmap")


def function_names(linkmap, header):
    """fN -> the method's name (wasm-ld's map: its CODE section's functions in index order, after the module's
    imports, which the C names fN too). A name met again gets #2, #3... (3 of 159,000)."""
    sec = re.compile(r"^\s+-\s+[0-9a-f]+\s+[0-9a-f]+ ([A-Z_]+)$")
    fn = re.compile(r"^\s+-\s+[0-9a-f]+\s+[0-9a-f]+\s+\S.*?:\((.*)\)$")
    names, inside = [], False
    with open(linkmap, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip()
            m = sec.match(line)
            if m:
                if inside:
                    break
                inside = m.group(1) == "CODE"
                continue
            if inside:
                m = fn.match(line)
                if m:
                    names.append(m.group(1))
    with open(header, encoding="utf-8", errors="replace") as f:
        imports = len(set(re.findall(r"\b(i\d+_\w+)\(", f.read())))
    seen, out = {}, {}
    for k, n in enumerate(names):
        seen[n] = seen.get(n, 0) + 1
        out["f%d" % (imports + k)] = n if seen[n] == 1 else "%s#%d" % (n, seen[n])
    return out


def split_cold(cdir, linkmap, hot_list):
    """Each player file's functions that ran rarely when sampled go to a twin, <file>_cold.c, which the console
    compiles for size (-Os, console/xsdk/CMakeLists.txt: 12-16% smaller than -O2, and slower). The list names
    each method of the sampled build hot or cold; a method it doesn't name (new since) stays -O2, as does
    everything without a list. Only with --cold-os: the first console build with it (hw31) stopped at
    start-up with an exception in the 64-bit number code, which the -O2 build runs right."""
    if not os.path.exists(hot_list) or not os.path.exists(linkmap):
        print("no hot list (%s): every function -O2" % hot_list)
        return
    hot, cold = set(), set()
    with open(hot_list, encoding="utf-8") as f:
        for line in f:
            if line.startswith("H "):
                hot.add(line[2:].rstrip("\n"))
            elif line.startswith("C "):
                cold.add(line[2:].rstrip("\n"))
    names = function_names(linkmap, os.path.join(cdir, "player.h"))
    sig = re.compile(r"^\w+ (f\d+)\(wasmModuleInstance\* instance")
    n_hot = n_cold = n_new = 0
    for name in sorted(os.listdir(cdir)):
        if not re.match(r"m6_player\.s\d+\.c$", name):
            continue
        path = os.path.join(cdir, name)
        with open(path, "rb") as f:
            text = f.read().decode("utf-8")
        eol = "\r\n" if "\r\n" in text else "\n"
        head, keep, rare = [], [], []
        cur, depth, fname = None, 0, None
        for ln in text.split(eol):
            if cur is None:
                m = sig.match(ln)
                if not m:
                    (keep if keep or rare else head).append(ln)
                    continue
                cur, fname, depth = [], m.group(1), 0
            cur.append(ln)
            depth += ln.count("{") - ln.count("}")
            if depth == 0:
                method = names.get(fname)
                if method in cold:
                    rare.extend(cur)
                    n_cold += 1
                else:
                    keep.extend(cur)
                    n_hot += 1
                    if method not in hot:
                        n_new += 1
                cur = None
        if cur is not None:
            sys.exit("%s: a function without its end" % name)
        with open(path, "wb") as f:
            f.write(eol.join(head + keep).encode("utf-8"))
        with open(path[:-2] + "_cold.c", "wb") as f:
            f.write(eol.join(head + rare).encode("utf-8"))
    print("hot list: %d functions -O2 (%d not in the list), %d to the -Os files" % (n_hot, n_new, n_cold))


ACCESS = re.compile(r"\b((?:i32|i64|f32|f64)_(?:load|store)(?:8|16|32)?(?:_[su])?)\(i->m0,")
INSTANCE = re.compile(r"(m6_playerInstance\* i = \(m6_playerInstance\*\)instance;(\r?\n))")


def hoist_memory_base(cdir):
    """Each function that reads or writes the linear memory loads its base once (mb) and goes through the
    accessors' base-pointer twins (console/w2c2/w2c2_base.h): through i->m0 every access loaded the memory
    struct and its data pointer again, since a store might have changed them (2.3 million instructions,
    8.9 MB of the console's code). The host never moves the block (W2C2_HOST_MEMORY), so the base holds for
    the memory's life."""
    funcs = accesses = 0
    for name in sorted(os.listdir(cdir)):
        if not name.endswith(".c"):
            continue
        path = os.path.join(cdir, name)
        with open(path, "rb") as f:
            text = f.read().decode("utf-8")
        parts = INSTANCE.split(text)   # [before, line, eol, body, line, eol, body, ...]
        if len(parts) == 1:
            continue
        out = [parts[0]]
        for k in range(1, len(parts), 3):
            line, eol, body = parts[k], parts[k + 1], parts[k + 2]
            # the body up to the next function's instance line: this function's
            body, n = ACCESS.subn(r"\1_b(mb,", body)
            out.append(line + ("U8* const mb = i->m0->data;" + eol if n else "") + body)
            if n:
                funcs += 1
                accesses += n
        with open(path, "wb") as f:
            f.write("".join(out).encode("utf-8"))
    print("memory base hoisted: %d functions, %d accesses" % (funcs, accesses))


def compress_data_segments_in_c(path):
    """--segments-in-c (the thirtieth run's way, for a test): the module's data segments (20 MB) as LZ4 blocks in the C,
    decoded into the linear memory at start
    (ch360_lz4_decode, console/ch360_pack.c): the XEX image is resident on the console, and its copy
    of the segments was only read once, at start (10 MB of memory back)."""
    import lz4.block
    with open(path, "rb") as f:
        text = f.read().decode("utf-8")
    sizes = {}
    for m in re.finditer(r"LOAD_DATA\(\(\*i->m0\), (\d+)U, (m6_playerData\d+), (\d+)\);", text):
        sizes[m.group(2)] = (int(m.group(1)), int(m.group(3)))
    out, at, before, after = [], 0, 0, 0
    for name, (offset, size) in sizes.items():
        if size < 65536:
            continue
        head = "const U8 %s[]={" % name
        start = text.index(head, at)
        end = text.index("};", start)
        data = bytes(int(v, 0) for v in text[start + len(head):end].split(",") if v.strip())   # w2c2 writes 7 or 0x45
        if len(data) != size:
            sys.exit("data segment %s: %d bytes, LOAD_DATA says %d" % (name, len(data), size))
        packed = lz4.block.compress(data, mode="high_compression", compression=12, store_size=False)
        lines = [",".join(str(b) for b in packed[k:k + 32]) for k in range(0, len(packed), 32)]
        out.append(text[at:start])
        out.append(head + "\n" + ",\n".join(lines) + "\n")
        at = end
        sizes[name] = (offset, size, len(packed))
        before += size
        after += len(packed)
    out.append(text[at:])
    text = "".join(out)
    for name, v in sizes.items():
        if len(v) != 3:
            continue
        offset, size, packed = v
        call = "LOAD_DATA((*i->m0), %dU, %s, %d);" % (offset, name, size)
        text = text.replace(call, "wasmMemoryCheckRange(i->m0, %dU, %d);\nif (ch360_lz4_decode(%s, %d, i->m0->data + %dU, %d) != %d) abort();"
                            % (offset, size, name, packed, offset, size, size))
    decl = "int ch360_lz4_decode(const unsigned char* src, unsigned srclen, unsigned char* dst, unsigned dstlen);\n"
    text = text.replace("static void m6_playerInitDataSegments(", decl + "static void m6_playerInitDataSegments(", 1)
    with open(path, "wb") as f:
        f.write(text.encode("utf-8"))
    print("data segments: %d KB as %d KB of LZ4" % (before // 1024, after // 1024))


def compress_data_segments(path):
    """The module's data segments (20 MB) as LZ4 blocks in PLAYER.DAT beside the C (staged to GameData), read
    and decoded into the linear memory at start (ch360_player_data, console/ch360_pack.c). The XEX image is
    resident on the console: the segments were raw there (20 MB), then LZ4 arrays (10 MB), each read only once.
    The file:  'CHPD' u32 version (1) u32 count u32 0; count entries u32 offset stored size fnv (FNV-1a 32 of
    the stored bytes, which the call in the C checks); the bytes."""
    import lz4.block
    with open(path, "rb") as f:
        text = f.read().decode("utf-8")
    sizes = {}
    for m in re.finditer(r"LOAD_DATA\(\(\*i->m0\), (\d+)U, (m6_playerData\d+), (\d+)\);", text):
        sizes[m.group(2)] = (int(m.group(1)), int(m.group(3)))
    out, at, before, blobs = [], 0, 0, []
    calls = {}
    for name, (offset, size) in sizes.items():
        if size < 65536:
            continue
        head = "const U8 %s[]={" % name
        start = text.index(head, at)
        end = text.index("};", start)
        data = bytes(int(v, 0) for v in text[start + len(head):end].split(",") if v.strip())   # w2c2 writes 7 or 0x45
        if len(data) != size:
            sys.exit("data segment %s: %d bytes, LOAD_DATA says %d" % (name, len(data), size))
        packed = lz4.block.compress(data, mode="high_compression", compression=12, store_size=False)
        fnv = 0x811c9dc5
        for b in packed:
            fnv = ((fnv ^ b) * 0x01000193) & 0xffffffff
        out.append(text[at:start])
        out.append("/* %s: in PLAYER.DAT */\n" % name)
        at = end + 2
        calls[name] = (len(blobs), offset, size, len(packed), fnv)
        blobs.append((packed, size, fnv))
        before += size
    out.append(text[at:])
    text = "".join(out)
    for name, (index, offset, size, stored, fnv) in calls.items():
        call = "LOAD_DATA((*i->m0), %dU, %s, %d);" % (offset, name, size)
        if text.count(call) != 1:
            sys.exit("data segment %s: its LOAD_DATA call not found once" % name)
        text = text.replace(call, "wasmMemoryCheckRange(i->m0, %dU, %d);\nif (ch360_player_data(%d, %d, 0x%08xu, i->m0->data + %dU, %d) != 0) abort();"
                            % (offset, size, index, stored, fnv, offset, size))
    decl = "int ch360_player_data(unsigned index, unsigned stored, unsigned fnv, unsigned char* dst, unsigned size);\n"
    text = text.replace("static void m6_playerInitDataSegments(", decl + "static void m6_playerInitDataSegments(", 1)
    with open(path, "wb") as f:
        f.write(text.encode("utf-8"))
    table, pos = b"", 16 + 16 * len(blobs)
    for packed, size, fnv in blobs:
        table += struct.pack("<IIII", pos, len(packed), size, fnv)
        pos += len(packed)
    with open(os.path.join(os.path.dirname(path), "PLAYER.DAT"), "wb") as f:
        f.write(struct.pack("<4sIII", b"CHPD", 1, len(blobs), 0) + table + b"".join(p for p, _, _ in blobs))
    print("data segments: %d KB as %d KB of LZ4 in PLAYER.DAT" % (before // 1024, (pos - 16 - 16 * len(blobs)) // 1024))


def main():
    global JOBS
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=os.path.join(ROOT, "game"))
    ap.add_argument("--stage", default=",".join(DEFAULT_STAGES))
    ap.add_argument("--jobs", type=int, default=JOBS)
    ap.add_argument("--w2c2")
    ap.add_argument("--no-hoist", action="store_true",
                    help="C stage: every access through i->m0 as w2c2 writes it (a console test of the hoist)")
    ap.add_argument("--segments-in-c", action="store_true",
                    help="C stage: the data segments as LZ4 arrays in the C, no PLAYER.DAT (a console test)")
    ap.add_argument("--cold-os", action="store_true",
                    help="C stage: put the functions hot_functions.txt marks rarely run in <file>_cold.c, which the "
                         "console compiles -Os (10.5 MB less code; stopped the console at start-up in hw31)")
    a = ap.parse_args()
    JOBS = a.jobs
    stages = STAGES if a.stage == "all" else a.stage.split(",")
    for s in stages:
        if s not in STAGES:
            sys.exit(f"unknown stage {s}; stages: {', '.join(STAGES)}")
    os.makedirs(OUT, exist_ok=True)
    for s in stages:
        print(f"== {s}", flush=True)
        try:
            globals()["stage_" + s](a)
        except subprocess.CalledProcessError as e:
            print(f"BUILD_MANAGED FAILED in stage {s} (exit {e.returncode})")
            return 1
    print("BUILD_MANAGED OK:", ",".join(stages))
    return 0


if __name__ == "__main__":
    sys.exit(main())
