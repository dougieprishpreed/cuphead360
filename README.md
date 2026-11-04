# Cuphead360

An Xbox 360 port of **Cuphead** and **The Delicious Last Course**, built on
your own PC from your own copy of the game.

It runs the game's original code. Unity's engine is rewritten in C#, and the
whole game is compiled ahead of time into native code for the Xbox 360. The
game's textures, sounds and scenes are converted for the console from your
installed copy.

## Status

**Early alpha, playable in parts on real hardware.**

Working on the console:

- The title screen, menus, save slots and the world map.
- Boss fights (tested: The Root Pack and Ruse of an Ooze, start to finish),
  close to full speed.
- Music and sound, saves, and the controller.
- Every sprite drawn at the game's full resolution.

Still in progress:

- Hitches in fights, and pauses when a level first loads.
- Run-and-gun levels: the first one ran very slowly in the last test (a fix
  is in the current build, not yet tested on the console).
- Loading screens that freeze instead of animating (also in the current
  build, untested).
- About 20 special shader effects that still draw as plain sprites.
- Most levels and the DLC haven't been tried on the console yet.

Tested with the GOG version of Cuphead 1.3.2, with The Delicious Last Course.

## What you need

- An Xbox 360 that runs homebrew (such as with ABadAvatar).
- Your own copy of Cuphead (GOG version).
- A USB drive formatted FAT32, with about 5 GB free.
- A Windows PC to build it on (see below).

There's no ready-made download. The console build contains the game's own
code compiled for the Xbox 360, so you have to make it yourself from your
own copy. A longer-term goal is a build with no game code in it, which could
then be shared.

## Building and installing

You need some development tools: the .NET 10 SDK, Python 3, the WASI SDK, and
a WSL (Linux) setup with the xSDK toolchain and vulkan4xenon. The steps are
in [docs/BUILDING.md](docs/BUILDING.md) (the game data and the managed build)
and [console/README.md](console/README.md) (the console build and putting it
on a USB drive). In outline:

1. Convert your game's files (objects, textures, sounds) with the tools in
   `tools/`.
2. Build the player and the console program.
3. Stage it to your USB drive with `tools/stage_console.py`.
4. On the console, start `default.xex` from the folder it made.

Converting the textures takes one to two hours on a typical PC the first time.

## Controls

The game's own controls on a standard Xbox 360 controller. Hold **Back +
Start** to quit to the dashboard.

## Reporting problems

The game writes a log, `cuphead360.txt`, next to `default.xex` on the USB
drive. Please include it, and say what you were doing, when you report a
problem.

## Acknowledgements

- Studio MDHR, for Cuphead.
- The authors of xSDK and vulkan4xenon, which make modern homebrew on the
  Xbox 360 possible.
- The .NET team, for NativeAOT and its LLVM and WebAssembly work.
- The authors of w2c2, stb_vorbis, LZ4, UnityPy and texture2ddecoder.

## Keep your build to yourself

Everything this project makes comes from your own copy of the game: keep it
on your own PC and console, and don't share it. This repository holds only
the tools, the engine and the documentation, with none of the game's code,
art or sound. Cuphead360 is a fan project and is not affiliated with or
endorsed by Studio MDHR.
