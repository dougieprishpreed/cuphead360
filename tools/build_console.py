#!/usr/bin/env python3
"""Build the console side of Cuphead360 from the translated player.

    build_console.py [--stage native,xsdk] [--opt 1] [--jobs 2] [--distro CTR360-XSDK]

Stages:
  native  work/managed/c (tools/build_managed.py --stage wasm,c) + the wasm
          runtime + console/host_native.c -> work/console/native/host_native.exe,
          the console build's code as a headless PC program (clang or gcc).
  xsdk    console/xsdk/build.sh in a WSL distro (--distro, default
          CTR360-XSDK): the shaders, the XEX ->
          /opt/cuphead360-xsdk/output/default.xex, copied to
          work/console/xsdk/default.xex.

The runtime (srt) and w2c2's headers come from Spire360 next to this
repository. Everything built here holds the player's own game code: keep
it local.
"""
import argparse
import concurrent.futures
import os
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SPIRE = os.path.join(os.path.dirname(ROOT), "spire360")
CDIR = os.path.join(ROOT, "work", "managed", "c")
OUT = os.path.join(ROOT, "work", "console")


def run(cmd, **kw):
    print("+", " ".join(str(c) for c in cmd), flush=True)
    kw.setdefault("check", True)
    return subprocess.run(cmd, **kw)


def stage_native(a):
    if not os.path.exists(os.path.join(CDIR, "player.h")):
        sys.exit("no translated player: run tools/build_managed.py --stage wasm,c")
    build = os.path.join(OUT, "native")
    objdir = os.path.join(build, "obj")
    os.makedirs(objdir, exist_ok=True)
    rt = os.path.join(SPIRE, "runtime")
    plat = "srt_plat_win32.c" if os.name == "nt" else "srt_plat_posix.c"
    sources = sorted(os.path.join(CDIR, f) for f in os.listdir(CDIR) if f.endswith(".c"))
    sources += [os.path.join(rt, "src", f) for f in ("srt_core.c", "srt_wasi.c", "srt_wasi_p2.c", plat)]
    sources += [os.path.join(ROOT, "console", f) for f in ("ch360_imports.c", "ch360_ogg.c", "ch360_pack.c", "host_native.c")]
    incs = [os.path.join(rt, "include"), os.path.join(rt, "src"), os.path.join(SPIRE, "third_party", "w2c2", "w2c2"),
            CDIR, os.path.join(ROOT, "console")]
    cc = a.cc or next((c for c in ("clang", "gcc", "cc") if shutil.which(c)), None)
    if not cc:
        sys.exit("no C compiler (clang or gcc)")
    exe = os.path.join(build, "host_native" + (".exe" if os.name == "nt" else ""))

    # The console's own headers (ch360_host.h's records) are part of every source's input: an object
    # older than one of them is rebuilt too (a stale host_native.o read the draw list with an old stride).
    headers = max((os.path.getmtime(os.path.join(ROOT, "console", h)) for h in os.listdir(os.path.join(ROOT, "console")) if h.endswith(".h")), default=0)

    def compile_one(src):
        obj = os.path.join(objdir, os.path.splitext(os.path.basename(src))[0] + ".o")
        if os.path.exists(obj) and os.path.getmtime(obj) > max(os.path.getmtime(src), headers):
            return obj
        cmd = [cc, "-c", "-O" + a.opt, "-w", "-DW2C2_HOST_MEMORY", "-D_CRT_SECURE_NO_WARNINGS", "-fno-strict-aliasing"] + \
              ["-I" + i for i in incs] + ["-o", obj, src]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode != 0:
            raise RuntimeError("%s:\n%s%s" % (os.path.basename(src), r.stdout, r.stderr))
        return obj

    print("compiling %d files with %s -O%s, %d jobs" % (len(sources), cc, a.opt, a.jobs), flush=True)
    t0 = time.time()
    objs = []
    with concurrent.futures.ThreadPoolExecutor(a.jobs) as pool:
        for n, obj in enumerate(pool.map(compile_one, sources), 1):
            objs.append(obj)
            if n % 5 == 0 or n == len(sources):
                print("  %d/%d (%.0f s)" % (n, len(sources), time.time() - t0), flush=True)
    link = [cc, "-o", exe] + objs
    if os.name == "nt":
        link += ["-Wl,/STACK:16777216"]
    else:
        link += ["-lm", "-lpthread"]
    run(link)
    print("%s (%d KB), %.0f s" % (exe, os.path.getsize(exe) // 1024, time.time() - t0))


def stage_xsdk(a):
    script = os.path.join(ROOT, "console", "xsdk", "build.sh")
    wsl_root = "/mnt/" + ROOT[0].lower() + ROOT[2:].replace("\\", "/")
    # --jobs reaches build.sh's JOBS (compilers at once) through WSLENV
    env = dict(os.environ, MSYS_NO_PATHCONV="1", JOBS=str(a.jobs))
    env["WSLENV"] = ":".join(v for v in (os.environ.get("WSLENV", ""), "JOBS/u") if v)
    run(["wsl.exe", "-d", a.distro, "--", "bash", wsl_root + "/console/xsdk/build.sh"], env=env)
    dst = os.path.join(OUT, "xsdk")
    os.makedirs(dst, exist_ok=True)
    print("XEX: /opt/cuphead360-xsdk/output/default.xex (copied to %s by build.sh)" % dst)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--stage", default="native")
    ap.add_argument("--opt", default="1")
    ap.add_argument("--jobs", type=int, default=2)
    ap.add_argument("--cc")
    ap.add_argument("--distro", default="CTR360-XSDK", help="the WSL distro holding the xSDK toolchain (xsdk stage)")
    a = ap.parse_args()
    for s in a.stage.split(","):
        {"native": stage_native, "xsdk": stage_xsdk}[s](a)
    print("BUILD_CONSOLE OK:", a.stage)


if __name__ == "__main__":
    sys.exit(main())
