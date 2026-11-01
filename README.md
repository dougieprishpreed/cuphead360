# Cuphead360

An Xbox 360 port of **Cuphead** and **The Delicious Last Course**, built from
the player's own GOG installation.

The game's own C# assemblies run unchanged on .NET; Unity's native engine is
reimplemented in C# (`managed/`); the console build compiles the whole thing
ahead of time through NativeAOT-LLVM -> WebAssembly -> w2c2 -> C, the
pipeline the Spire360 port already ships on Xbox 360 hardware.

This repository holds only the patcher, engine, host, converter and docs. It
does **not** contain the game's executables, assemblies, asset bundles, audio,
textures, SDK material, or anything generated from them; those stay in the
local `game/`, `managed/lib/` and `work/` folders and are built on the
player's PC.

## Status

Early. On Windows, the original code boots from the start scene through
Cuphead's own initialisation and plays the title sequence (studio logo,
dancing title, "Press Any Button") with no exceptions; a preview tool draws
what the engine would render to PNG. There's no input or audio yet, and the
console build hasn't been compiled or run. Details: [docs/STATUS.md](docs/STATUS.md); recovery state and
next steps: [docs/HANDOFF.md](docs/HANDOFF.md).

Reference build: Cuphead GOG `bundleVersion` 1.3.2 (build id
`55543280706943035`), Unity 2017.4.9f1, base game and DLC.

## Layout

```
managed/Cuphead360.Patcher   Cecil tool: publicize, gen, redirect
managed/Cuphead360.Engine    the Unity engine in C# (Impl/) + generated stubs
managed/Cuphead360.Player    host: census, selftest, run, probes
tools/build_managed.py       build pipeline (patch, engine, JIT host, wasm, C)
tools/convert_objects.py     Unity serialized files -> CHOB object trees
tools/save_guard.py          checks the installed saves are untouched
docs/                        architecture, plan, status, handoff, evidence
```

## Build

See [docs/BUILDING.md](docs/BUILDING.md). Short version (Windows, .NET 10 SDK,
Python 3 with UnityPy):

```
python tools/build_managed.py
python -I tools/convert_objects.py --game game --out work/GameData --bundles
dotnet work/managed/jit/Cuphead360.Player.dll run --frames 300
```

## Legal

Cuphead is © Studio MDHR. This is a personal interoperability and
preservation project. No game code or assets are distributed here. Use only
with a legally obtained copy of the game.
