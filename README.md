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

Foundation plus first executable vertical slices. The current build:
- loads the original `Assembly-CSharp.dll` metadata and IL through a custom
  big-endian-safe runtime;
- executes original `EaseUtils`, `PlayerData/PlayerInventory`, and
  `LevelPlayerMotor/VelocityManager` logic on Windows **and** on the Xbox 360
  XEX under Xenia;
- extracts real scenes, sprites, and sprite animation tracks from the user's
  game data and renders them with the same shared software renderer on
  Windows and Xbox 360;
- binds scene GameObjects/Transforms/components to managed objects and
  executes original `TransformExtensions`, `AnimationHelper.Update`, and
  `AbstractMonoBehaviour.Awake` logic against them.

It is **not** yet a playable Cuphead port. See
[docs/PROGRESS.md](docs/PROGRESS.md) for exactly what compiles, executes, and
has been validated, and [docs/HANDOFF.md](docs/HANDOFF.md) for the current
build mode and next steps.

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
