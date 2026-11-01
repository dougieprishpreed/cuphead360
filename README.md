# Cuphead360

A native Xbox 360 port of **Cuphead** and **The Delicious Last Course**, built
from the user's own GOG installation with the Microsoft Xbox 360 XDK and
Visual Studio 2010.

This repository contains original engine/runtime source, reverse-engineering
tooling, and asset-conversion tooling. It does **not** contain the game's
original executables, assemblies, asset bundles, audio, textures, or any
proprietary SDK material. Those stay in the local `game/` directory and in a
locally generated `GameData/` directory.

## Status

**2026-11-01:** the port now runs the game's own C# on .NET with a
reimplemented Unity engine (`managed/`), compiled for the console through
NativeAOT-LLVM -> WebAssembly -> w2c2 -> C. It boots the original start scene
on Windows; it does not render or play yet. See
[docs/MANAGED_PLAYER.md](docs/MANAGED_PLAYER.md) and
[docs/HANDOFF.md](docs/HANDOFF.md). The notes below describe the earlier
interpreter foundation, which is retired.

Development foundation with experimental vertical slices. Earlier foundation
checkpoints demonstrated the following; their Xenia results do not validate
the latest binary or the playable opening flow:
- loads the original `Assembly-CSharp.dll` metadata and IL through a custom
  big-endian-safe runtime;
- executes original `EaseUtils`, `PlayerData/PlayerInventory`, and
  `LevelPlayerMotor/VelocityManager` logic on Windows **and** on the Xbox 360
  XEX under Xenia;
- extracts real scenes, sprites, and sprite animation tracks from the user's
  game data and renders them with the same shared software renderer on
  Windows and Xbox 360;
- binds scene GameObjects/Transforms/components to managed objects, recovers
  serialized component fields, and executes original `TransformExtensions`,
  `AnimationHelper.Update`, and `AbstractMonoBehaviour.Awake` logic against
  them;
- includes a playable tutorial movement slice (run, jump, dash, ground
  collision, camera follow, real Cuphead sprites) driven partly by original
  `VelocityManager` IL, verified on Windows and in Xenia.

It is **not** yet a playable Cuphead port. See
[docs/STATUS.md](docs/STATUS.md) for the current summary and
[docs/VERIFICATION.md](docs/VERIFICATION.md) for checkpoint evidence. Recovery
state and next actions live in [docs/HANDOFF.md](docs/HANDOFF.md); use
[docs/NEXT_SESSION_PROMPT.md](docs/NEXT_SESSION_PROMPT.md) to start a succeeding
agent session. Update both recovery files at the end of each round of changes.

Current reference build:

- Cuphead GOG, `bundleVersion` **1.3.2** (build id `55543280706943035`)
- Unity **2017.4.9f1**, Mono scripting backend
- Base game + The Delicious Last Course scenes and assets present

## Layout

```
docs/           architecture, reverse-engineering notes, progress, handoff
src/core/       portable foundations: types, endian, files, logging, memory
src/runtime/    metadata loader, IL execution runtime, CLR object model
src/engine/     Unity-compatible scene/component/sprite/animation runtime
src/platform/   win32 and xbox360 backends
tools/          analysis, metadata generation, asset preparation
projects/       Visual Studio 2010 / Xbox 360 build files
tests/          automated unit, integration, and gameplay tests
```

## Build

See [docs/BUILDING.md](docs/BUILDING.md). The short version:

```
scripts\build_win32.bat
scripts\build_xbox360.bat
```

The Xbox 360 build needs the XDK and Visual Studio 2010. The Windows build
uses Visual Studio 2010's compiler so that the same C++ dialect is exercised
on both targets. The Windows executable is the primary automated test target.

## Legal

Cuphead is © Studio MDHR. This project is a personal interoperability and
preservation effort. No game code or assets are distributed here. Use only
with a legally obtained copy of the game.
