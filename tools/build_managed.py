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
import shutil
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


def stage_publicize(a):
    shutil.rmtree(LIB_PUBLIC, ignore_errors=True)
    patcher("publicize", "--in", managed_dir(a.game), "--out", LIB_PUBLIC)


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
    run([find_w2c2(a), "-f", "2000", os.path.join(OUT, "wasm", "Cuphead360.Player.wasm"), os.path.join(cdir, "player.c")])
    spire_tools = os.path.join(os.path.dirname(ROOT), "spire360", "tools")
    if os.path.isdir(spire_tools):
        run([sys.executable, os.path.join(spire_tools, "w2c2_fix_tables.py"), os.path.join(cdir, "player.c")])
        run([sys.executable, os.path.join(spire_tools, "w2c2_imports.py"), os.path.join(cdir, "player.h")], check=False)
    print(len([f for f in os.listdir(cdir) if f.endswith(".c")]), "C files in", cdir)


def main():
    global JOBS
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=os.path.join(ROOT, "game"))
    ap.add_argument("--stage", default=",".join(DEFAULT_STAGES))
    ap.add_argument("--jobs", type=int, default=JOBS)
    ap.add_argument("--w2c2")
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
