# The console build

`default.xex` for an Xbox 360 that runs homebrew (such as with ABadAvatar),
built with the open [xsdk](https://github.com/xenosworks/xsdk) toolchain and
[vulkan4xenon](https://github.com/xenosworks/vulkan4xenon), as Spire360 is.
It runs the player's own Cuphead code: the game, Unity and Rewired
assemblies with the engine (`managed/`), compiled by NativeAOT-LLVM to a
WASI module, translated to C by w2c2 and compiled for the console's CPU.

## What runs where

- **The player** (`work/managed/c`, from `tools/build_managed.py --stage
  wasm,c`): the game loop, the engine, audio mixing, the render list. Its
  `ConsoleHost` (`managed/Cuphead360.Player/ConsoleHost.cs`) calls six
  imports each frame: `frame_begin`, `pad`, `submit` (the frame's draw
  list), `texture_file`, `texture_pixels` and `audio`.
- **The glue** (`ch360_imports.c`): those imports, and the start of the
  module under Spire360's wasm runtime (srt, WASI preview1) on a guest
  thread with a 16 MB stack; its stdout and stderr go to the log.
- **The host** (`xsdk/main.cpp`): Vulkan through vulkan4xenon (one pass a
  frame into the swapchain, a pipeline per sprite shader recipe, batched
  draws), textures loaded on first use from `GameData/ctex` and evicted
  least-recently-used above 112 MB, the pads through `XamInputGetState`,
  audio through xsdk's feeder, the log through a writer thread.
- **The PC check** (`host_native.c`): the same translated C and glue as a
  headless Windows program (`tools/build_console.py --stage native`): it
  runs the game with a bot script and checks each frame's draw list.

## Build

On this PC (Windows, with WSL distro `CTR360-XSDK` holding xsdk,
vulkan4xenon, glslang and the toolchain in `/opt/ctr360-xsdk`):

```
python tools/build_managed.py --stage wasm,c            # player.wasm -> work/managed/c
python tools/build_console.py --stage native            # optional PC check (clang)
python tools/build_console.py --stage xsdk              # -> work/console/xsdk/default.xex
```

The player and runtime compile against `console/w2c2/w2c2_base.h`, a copy
of Spire360's w2c2 header whose only change makes each 64-bit access to the
player's memory two 32-bit ones: GCC turns w2c2's 8-byte copies into plain
doubleword loads and stores, the player keeps many 64-bit values only
4-byte aligned (the third run showed alignment wasn't the problem; the
split costs little). An open question (docs/VERIFICATION.md, rounds 7-10):
on the game's thread a 64-bit compare of a value just built has taken the
low word's sign, while the same instructions are right at start-up;
`probe64.c` runs the CPU probe on the main thread, on fresh threads and on
the game thread (frames 1 and 600) with each thread's MSR. Names built
from 64-bit ids (converted textures and clips) use `IdText`, 32-bit
arithmetic only, so they are right whatever the cause.

`console/xsdk/build.sh` takes `XSDK`, `VULKAN4XENON`, `GLSLANG`,
`XSDK_TOOLCHAIN` and `CH360_RUNTIME` (Spire360's checkout) to build
elsewhere; it never builds inside the shared checkouts. `JOBS` compilers
run at once (2 by default).

## Data

```
python -I tools/convert_objects.py --game game --out work/GameData
python -I tools/convert_objects.py --game game --out work/GameData --files none --bundles
python -I tools/convert_textures.py --game game --out work/GameData --bundles
python -I tools/convert_console.py --data work/GameData          # BC7 -> DXT1/DXT5/BGRA8, pages halved to 2048
python -I tools/convert_audio.py --game game --out work/GameData --bundles
```

Converted objects from before round 8 still load, but convert them again:
byte data (fonts' m_FontData) is now stored as bytes, which loads the title
fonts several times faster.

## Stage and run

```
python tools/stage_console.py --to E:\Games\Cuphead360
```

That folder holds `default.xex`, `GameData\` (objects, ctex, audio, fonts),
`save\` and `licenses\`. Start `default.xex` from the dashboard. The game
writes `cuphead360.txt` next to it: the memory probe, start-up memory,
I64CHECK, each slow converted-file load (CHOB lines) and scene load (SCENE
built/activated times), any frame over half a second with its player-loop
phases (SLOW lines), the first 40 texture loads with their times, frames
1-3, 10, 60 and 300, every 600 frames frame time (tenths of a ms), memory,
textures and audio, the player's own output and exceptions (drained live by
the log thread), and a heartbeat (every 2 s for two minutes, then every 10
s: the frame count, where the game thread is (the player's code, a host
call, a renderer step, a runtime wait or file call), the runtime's call
count and its file opens, reads, megabytes and milliseconds). The heartbeat lines are
written through to the drive, so a log that stops means the console froze,
and one that keeps beating on one stage shows where it is stuck. Back
writes the log at once; hold Back + Start to quit.

Everything built and staged here holds the player's own game code and
data: keep it on the player's own PC and console.
