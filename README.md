# Cuphead360

An Xbox 360 port of **Cuphead** and **The Delicious Last Course**, built on
your own PC from your own copy of the game.

It runs the game's original code. Unity's engine is rewritten in C#, and the
whole game is compiled ahead of time into native code for the Xbox 360. The
game's textures, sounds and scenes are converted for the console from your
installed copy.

## What you need

- An Xbox 360 that runs homebrew (such as with ABadAvatar).
- Your own copy of Cuphead on Steam (version 1.3.4).
- About 4.5 GB free on your Xbox 360
- A Windows PC to build it on (see below).

There's no ready-made download. The console build contains the game's own
code compiled for the Xbox 360, so you have to make it yourself from your
own copy.

## Building and installing

You need some development tools: the .NET 10 SDK, Python 3, the WASI SDK, and
a WSL (Linux) setup with the xSDK toolchain and vulkan4xenon. Every step,
from installing the tools to putting the build on a USB drive, is in
[docs/BUILDING.md](docs/BUILDING.md). In outline:

1. Build the console program, `default.xex`, following the guide.
2. Run **360convert.exe**: it makes a `cuphead360` folder where you run it,
   asks for your `Cuphead.exe`, and converts the game's objects, textures and
   sounds into that folder. It needs about 40 GB free while it works and
   takes an hour or two.
3. Add your build to that folder: `python tools\stage_console.py --to
   <your cuphead360 folder> --build-only`.
4. Copy the `cuphead360` folder to your USB drive's `Apps` folder (or to the
   console's hard drive), and on the console start `default.xex` from it.

The first build takes a few hours, mostly building the Xbox 360 compiler;
later builds take minutes.

## Controls

The game's own controls on a standard Xbox 360 controller. Hold **Back +
Start** to quit to the dashboard.

## Reporting problems

The game writes a log, `cuphead360.txt`, next to `default.xex`. Please include it, and say what you were doing, when you report a
problem.

## Keep your build to yourself

Everything this project makes comes from your own copy of the game: keep it
on your own PC and console, and don't share it. This repository holds only
the tools, the engine and the documentation, with none of the game's code,
art or sound.
