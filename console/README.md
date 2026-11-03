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
  list), `texture_file`, `texture_pixels` and `audio`, and `ogg_open`,
  `ogg_read`, `ogg_seek` and `ogg_close` for its clips: Vorbis is decoded
  natively by stb_vorbis (`ch360_ogg.c`, `third_party/stb_vorbis`), not by
  the engine's NVorbis, which as translated wasm took most of a frame on
  the world map.
- **The glue** (`ch360_imports.c`): those imports, and the start of the
  module under Spire360's wasm runtime (srt, WASI preview1) on a guest
  thread with a 16 MB stack; its stdout and stderr go to the log.
- **The host** (`xsdk/main.cpp`): Vulkan through vulkan4xenon (one pass a
  frame into the swapchain, a pipeline per sprite shader recipe, batched
  draws), textures loaded on first use from `GameData/ctex` through one
  8 MB staging ring, least recently used ones let go to stay within 96 MB
  and above 24 MB of free memory, items wholly off screen skipped, the pads through `XamInputGetState`,
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
split costs little).

A title runs in 32-bit mode (MSR[SF] clear), where a record-form
instruction (`mr.`, `and.`, `rldicl.` ...) sets CR0 from its result's low
word only, and xSDK's GCC uses record forms for some 64-bit compares: long
formatting tested `value >= 0` as `mr. r7,r5; bge` and printed longs with
their low word's sign (docs/VERIFICATION.md, round 11).
`xsdk/record_forms.py`, the build's compiler launcher for every C and C++
file, compiles to assembly with GCC's insn names, puts `cmpdi 0,rD,0` after
each record form of a 64-bit insn (not after those whose result fits in the
low 31 bits: `andi.`, `andis.` below 0x8000, `rldicl.` from bit 33, which
are most of them; a compare after each pushed a branch out of range), and
assembles that; it prints what it
changed per file, and any 64-bit carry patterns (carries also come from bit
32 in 32-bit mode). A change to it needs a clean build. Prebuilt libraries
(libxsdk, vulkan4xenon, newlib) are not covered. `probe64.c` still runs
the CPU probe per thread at start-up, and names built from 64-bit ids use
`IdText`.

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
