# Building Cuphead360

This guide takes you from your own copy of Cuphead to a folder on a USB drive
that an Xbox 360 can run. Everything is built on your PC from your game's
files; nothing of the game is downloaded or shared. Follow the steps in
order. Each one ends with a check, so you (or an agent working through this
for you) know it worked before going on.

The first build takes about three to four hours, most of it waiting:
building the Xbox 360 compiler once (about an hour) and converting the
textures (about an hour and a half). Later builds take minutes.

## What you need

- A Windows 10 or 11 PC (64-bit) with 16 GB of memory.
- About 60 GB free on an NTFS drive for the build, and about 10 GB on the
  drive Windows keeps WSL on.
- **Cuphead from GOG**, version 1.3.2, with or without The Delicious Last
  Course, installed.
- A USB drive formatted FAT32 with about 6 GB free.
- An Xbox 360 that runs homebrew (such as with ABadAvatar).

Keep the folders below on a local drive, in paths without spaces (for example
`D:\dev`). The commands are for the Windows Command Prompt unless a step says
they run in WSL.

## 1. Install the Windows tools

1. **Git**: `winget install Git.Git`
2. **Python 3.11 or newer** (3.13 is what it was tested with):
   `winget install Python.Python.3.13`, then
   ```
   python -m pip install UnityPy==1.25.4 numpy Pillow texture2ddecoder lz4
   ```
3. **The .NET 10 SDK**: `winget install Microsoft.DotNet.SDK.10`
4. **wasi-sdk 29**: download `wasi-sdk-29.0-x86_64-windows.tar.gz` from
   <https://github.com/WebAssembly/wasi-sdk/releases/tag/wasi-sdk-29> and
   extract it to `C:\`:
   ```
   tar -xzf wasi-sdk-29.0-x86_64-windows.tar.gz -C C:\
   ```
   (Somewhere else works too: set `WASI_SDK_PATH` to the folder.)
5. **CMake and a C compiler** (to build w2c2 once):
   `winget install Kitware.CMake` and the Visual Studio Build Tools with the
   "Desktop development with C++" workload
   (`winget install Microsoft.VisualStudio.2022.BuildTools`, then add the
   workload in the Visual Studio Installer).

**Check:** in a new Command Prompt, `git --version`, `python --version`,
`dotnet --version` (prints `10.`...), `cmake --version` and
`C:\wasi-sdk-29.0-x86_64-windows\bin\clang --version` all print versions.

## 2. Get the sources

Cuphead360 uses the WebAssembly runtime and the w2c2 translator from
[Spire360](https://github.com/dougieprishpreed/spire360), which must sit
next to it:

```
cd /d D:\dev
git clone https://github.com/dougieprishpreed/cuphead360.git
git clone https://github.com/dougieprishpreed/spire360.git
```

Then build w2c2 (into the place Cuphead360 looks for it):

```
cmake -S spire360\third_party\w2c2\w2c2 -B spire360\out\logic\w2c2-build -DCMAKE_BUILD_TYPE=Release
cmake --build spire360\out\logic\w2c2-build --config Release --target w2c2
```

**Check:** `D:\dev\spire360\out\logic\w2c2-build\Release\w2c2.exe` exists.

## 3. Point it at your game

The tools read the game from `cuphead360\game`. Link your GOG installation
there (nothing is copied, and the tools never write to it):

```
cd /d D:\dev\cuphead360
mklink /J game "C:\GOG Games\Cuphead"
```

Use your own install folder: the one holding `Cuphead.exe` and
`Cuphead_Data`.

**Check:** `dir game\Cuphead_Data\Managed\Assembly-CSharp.dll` lists the
file.

## 4. Build the player

The player is the game's own code with Unity's engine rewritten in C#,
compiled ahead of time to WebAssembly and translated to C for the console.

```
python tools\build_managed.py
python tools\build_managed.py --stage wasm,c
```

The first command patches the game's assemblies and checks them on .NET
(a few minutes); it ends with `BUILD_MANAGED OK`. The second compiles the
player to WebAssembly and C (about 15 minutes; the first run also downloads
the NativeAOT-LLVM packages, a few hundred MB, from the dotnet-experimental
NuGet feed).

**Check:** both commands end with `BUILD_MANAGED OK`, and
`work\managed\c\player.h` exists with about 78 `.c` files beside it.

## 5. Convert the game's data

These read `game\` and write `work\GameData`. Run them one at a time, in
this order:

```
python -I tools\convert_objects.py --game game --out work\GameData
python -I tools\convert_objects.py --game game --out work\GameData --files none --bundles
python -I tools\convert_textures.py --game game --out work\GameData --bundles
python -I tools\convert_console.py --data work\GameData
python -I tools\convert_console.py --data work\GameData --max-size 4096 --out work\GameData\ctex4k --halved-only --workers 2
python -I tools\cluster_console.py --data work\GameData
python -I tools\convert_audio.py --game game --out work\GameData --bundles
```

| Step | Writes | Time | Done when it prints |
| --- | --- | --- | --- |
| `convert_objects` (twice) | `objects\*.chob`, about 580 files, 770 MB | 2 min | `0 errors` |
| `convert_textures` | `textures\*.tex`, 14 GB | 2 min | `0 errors` |
| `convert_console` | `ctex\*.ctx`, halved pages, 4 GB | 30 min | `0 errors` |
| `convert_console --max-size 4096` | `ctex4k\*.ctx`, full-size pages, 13 GB | 45 min | `0 errors` |
| `cluster_console` | `ctexc\`, sprite pages and `ctexmap.bin`, 14 GB | 5 min | `cluster_console: ... sprite pages` |
| `convert_audio` | `audio\*.ogg`, about 4,000 clips, 390 MB | 10 min | `0 errors` |

The texture steps run their workers at below-normal priority, so the PC
stays usable. When they are done you can delete `work\GameData\textures`
(14 GB): only `convert_console` reads it, so convert again before running
that step another time.

**Check:** `work\GameData` holds `objects`, `ctex`, `ctex4k`, `ctexc`
(with `ctexmap.bin`) and `audio`.

## 6. Set up the Xbox 360 toolchain in WSL

The console program is built with the open
[xsdk](https://github.com/xenosworks/xsdk) toolchain and
[vulkan4xenon](https://github.com/xenosworks/vulkan4xenon), in WSL. This is
done once.

Install the Debian distro (what this is built and tested with), in an
administrator Command Prompt:

```
wsl --install -d Debian
```

Create your Linux user when it asks. Then **in the Debian window** (or
`wsl -d Debian`), run:

```
sudo apt-get update
sudo apt-get install -y build-essential bison flex texinfo libgmp-dev libmpfr-dev libmpc-dev curl xz-utils patch python3 cmake ninja-build git ca-certificates
sudo mkdir -p /opt/ctr360-xsdk /opt/cuphead360-xsdk
sudo chown "$USER" /opt/ctr360-xsdk /opt/cuphead360-xsdk
cd /opt/ctr360-xsdk
git clone --depth 1 https://github.com/xenosworks/xsdk.git
git clone --depth 1 https://github.com/xenosworks/vulkan4xenon.git
git clone --depth 1 --branch xenon https://github.com/xenosworks/glslang-xenon.git glslang
(cd xsdk && PREFIX=/opt/ctr360-xsdk/toolchain WORK=/tmp/xsdk-toolchain JOBS=4 bash tools/toolchain/build.sh)
(cd glslang && python3 update_glslang_sources.py)
cmake -S glslang -B glslang/build-host -G Ninja -DCMAKE_BUILD_TYPE=Release -DENABLE_OPT=ON -DSPIRV_SKIP_EXECUTABLES=ON -DGLSLANG_TESTS=OFF
cmake --build glslang/build-host
make -C xsdk -j4 TOOLCHAIN=/opt/ctr360-xsdk/toolchain build/libxsdk.a build/host/libxsc.so
make -C vulkan4xenon -j4 XSDK=/opt/ctr360-xsdk/xsdk TOOLCHAIN=/opt/ctr360-xsdk/toolchain build/libvulkan4xenon.a
```

The toolchain step builds GCC for the console's CPU and takes about an hour.
The paths matter: the build looks for everything under `/opt/ctr360-xsdk`.

**Check:** these four files exist:

```
ls /opt/ctr360-xsdk/toolchain/bin/powerpc-xsdk-elf-gcc /opt/ctr360-xsdk/glslang/build-host/StandAlone/glslang /opt/ctr360-xsdk/xsdk/build/libxsdk.a /opt/ctr360-xsdk/vulkan4xenon/build/libvulkan4xenon.a
```

## 7. Build default.xex

Back in the Windows Command Prompt, in `cuphead360`:

```
python tools\build_console.py --stage xsdk --distro Debian
```

It runs `console/xsdk/build.sh` in the distro, which compiles the shaders,
the player's C and the host program, two files at a time (GCC takes over a
gigabyte for each of the player's large files). The first build takes 30 to
60 minutes; after a change only what changed is compiled again.

**Check:** it ends with `BUILD_CONSOLE OK: xsdk`, and
`work\console\xsdk\default.xex` (about 100 MB) and `work\console\xsdk\licenses`
exist.

## 8. Put it on the USB drive

With the USB drive as `E:`:

```
python tools\stage_console.py --to E:\Apps\cuphead360
```

The first time, it packs the textures and sounds into a few large files
(`CTEX0.PAK`, `CTEX1.PAK`, `AUDIO0.PAK`, each under 2 GB) in
`work\console\packs`, which takes a while; later it packs again only what
changed. Then it copies what is new or changed to the drive. Saves already
on the drive are kept.

**Check:** `E:\Apps\cuphead360` holds `default.xex`, `GameData\` (with
`objects`, `fonts` and the `.PAK` files), `licenses\` and `save\`.

## 9. Play

Eject the drive, plug it into the Xbox 360, and start
`Apps\cuphead360\default.xex` from your homebrew launcher. Hold **Back +
Start** to quit to the dashboard.

The game writes `cuphead360.txt` next to `default.xex`. If something goes
wrong, include that file when you report it, and say what you were doing.

## After an update

After `git pull` in `cuphead360` (and in `spire360`):

```
python tools\build_managed.py
python tools\build_managed.py --stage wasm,c
python tools\build_console.py --stage xsdk --distro Debian
python tools\stage_console.py --to E:\Apps\cuphead360
```

Convert the data again (step 5) only when the update's notes say to.

## If something fails

- **`wasi-sdk 29 not found`**: set `WASI_SDK_PATH` to the folder holding
  `bin\clang.exe`.
- **`w2c2 not found`**: step 2's w2c2 build, or pass
  `--w2c2 <path to w2c2.exe>` to `build_managed.py`.
- **`missing /opt/ctr360-xsdk/...`** from `build.sh`: one of step 6's
  builds didn't finish; run it again and look at its last lines.
- **`$'\r': command not found`** from `build.sh`: the checkout has Windows
  line endings. Run `git rm --cached -r . && git reset --hard` in
  `cuphead360` (the repository's `.gitattributes` keeps `.sh` files LF).
- **`... has N GB free; staging needs M GB more`**: free space on the USB
  drive, or use a bigger one.
- **The console returns to the dashboard or freezes**: send
  `cuphead360.txt`; its last lines say where it stopped.

## Keep your build to yourself

Everything under `work\`, `managed\lib\` and the USB folder is made from your
copy of the game. Keep it on your own PC and console.
