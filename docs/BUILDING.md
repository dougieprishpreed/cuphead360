# Building Cuphead360

This guide takes you from your own copy of Cuphead to a folder on a USB drive
(or the console's hard drive) that an Xbox 360 can run. Everything is built on your PC from your game's
files; nothing of the game is downloaded or shared. Follow the steps in
order. Each one ends with a check, so you (or an agent working through this
for you) know it worked before going on.

The folder holds two things: `default.xex`, the console program you build
here (steps 1 to 4, 6 and 7), and the game's data converted for the
console. **360convert.exe** makes the data for you (step 5): run it, pick
your `Cuphead.exe`, and it fills a `cuphead360` folder. Step 8 adds your
build to that folder.

The first build takes about three to four hours, most of it waiting:
building the Xbox 360 compiler once (about an hour) and converting the
textures (about an hour and a half). Later builds take minutes.

## What you need

- A Windows 10 or 11 PC (64-bit) with 16 GB of memory.
- About 60 GB free on an NTFS drive for the build, and about 10 GB on the
  drive Windows keeps WSL on.
- **Cuphead on Steam**, version 1.3.4 (build 9318602), with or without The Delicious Last
  Course, installed.
- A USB drive formatted FAT32 with about 6 GB free, or as much room on the
  console's hard drive.
- An Xbox 360 that runs homebrew (such as with ABadAvatar).

Keep the folders below on a local drive, in paths without spaces (for example
`D:\dev`). The commands are for the Windows Command Prompt unless a step says
they run in WSL.

## 1. Install the Windows tools

1. **Git**: `winget install Git.Git`
2. **Python 3.11 or newer** (3.13 is what it was tested with):
   `winget install Python.Python.3.13`, then
   ```
   python -m pip install UnityPy==1.25.4 numpy Pillow texture2ddecoder lz4 soundfile
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

The tools read the game from `cuphead360\game`. Link your Steam installation
there (nothing is copied, and the tools never write to it):

```
cd /d D:\dev\cuphead360
mklink /J game "C:\Program Files (x86)\Steam\steamapps\common\Cuphead"
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
NuGet feed). It writes the module's data segments to
`work\managed\c\PLAYER.DAT` as LZ4 blocks (the `lz4` package from step 1),
which the console reads from its drive and decodes into the player's memory
at start, so the executable the console keeps in memory doesn't carry them.

Functions that rarely run are compiled for size (10 MB less resident on
the console). This needs a list of the functions that run often, sampled
while a PC build of the console code (`python tools\build_console.py
--stage native`, which writes `work\console\native\host_native.exe`)
plays every level. The list is made with `tools\sample_native.py --raw
--save-stacks` and `tools\hot_functions.py` (see their help) as
`work\managed\hot_functions.txt`. With it the C stage splits each file
into its often-run functions and a `_cold.c` twin for the rest; without
it, or with `--no-cold-os`, every function stays -O2. A function the list
doesn't name (new since it was sampled) stays -O2. `python tools\build_console.py --stage xsdk --test64` on that C makes
`cuphead360_test.xex` and `GameData\PLAYER_TEST.DAT`, which go beside the
play build. The test build checks .NET's 64-bit number code and probes the
library code start-up uses, writes every result to `cuphead360_test.txt`,
and quits before the game reads the saves. `tools\t64_diff.py` compares
that log with the same build's run on the PC.

**Check:** both commands end with `BUILD_MANAGED OK`, and
`work\managed\c\player.h` exists with about 78 `.c` files beside it, and
`PLAYER.DAT`.

## 5. Convert the game's data

**With 360convert.exe** (the easy way): put `360convert.exe` in the folder
where you want the `cuphead360` folder (not inside the game's folder) and
start it. It uses the `cuphead360` folder there, or makes it, asks for your
`Cuphead.exe` in a file dialog, and converts and stages the game's data in
`cuphead360\GameData`: the texture and sound packs, the converted objects,
the read-ahead lists and the fonts. It needs about 36 GB free for its
working files (`cuphead360_work` beside `cuphead360`, or the fixed drive
with the most room; deleted at the end) and about 5 GB for the folder, and
takes an hour or two. If it stops, start it again in the same folder: it
goes on from the last step it finished. Its window says what it is doing,
and `cuphead360_convert.log` beside the folder keeps it all.
`360convert.exe --game "C:\Program Files (x86)\Steam\steamapps\common\Cuphead"` skips the dialog,
`--workers N` sets how many texture processes run, `--work <folder>` puts
the working files elsewhere, `--selftest` checks its libraries load (with
`--game`, by converting one of the game's sounds too) and
`--licenses` prints the licenses of what is inside it.

360convert.exe is these tools in one program, built with
`python tools\build_convert360.py` (PyInstaller: `python -m pip install
pyinstaller`; it writes `dist\360convert.exe` and runs its `--selftest`).
From source it is `python tools\convert360.py`, with the packages from
step 1.

**With the tools, one by one** (the way to convert again after changing
them): these read `game\` and write `work\GameData`. Run them one at a
time, in this order:

```
python -I tools\convert_objects.py --game game --out work\GameData
python -I tools\convert_objects.py --game game --out work\GameData --files none --bundles
python -I tools\convert_textures.py --game game --out work\GameData --bundles
python -I tools\convert_console.py --data work\GameData
python -I tools\convert_console.py --data work\GameData --max-size 8192 --out work\GameData\ctex4k --halved-only --workers 2
python -I tools\cluster_console.py --data work\GameData
python -I tools\scale_pages.py --data work\GameData
python -I tools\simplify_meshes.py --data work\GameData
python -I tools\convert_audio.py --game game --out work\GameData --bundles
```

| Step | Writes | Time | Done when it prints |
| --- | --- | --- | --- |
| `convert_objects` (twice) | `objects\*.chob`, about 580 files, 770 MB | 2 min | `0 errors` |
| `convert_textures` | `textures\*.tex`, 14 GB | 2 min | `0 errors` |
| `convert_console` | `ctex\*.ctx`, halved pages, 4 GB | 30 min | `0 errors` |
| `convert_console --max-size 8192` | `ctex4k\*.ctx`, full-size pages, 13 GB | 45 min | `0 errors` |
| `cluster_console` | `ctexc\`, sprite pages (4 MB at most) and `ctexmap.bin`, 13 GB | 5 min | `cluster_console: ... sprite pages` |
| `scale_pages` | `ctexs\`, smaller pages of the atlases `console\artscale.txt` names | 4 min | `scale_pages: ... 0 errors` |
| `simplify_meshes` | `objects\meshes.bin`: lighter meshes for the sprites with the most triangles (skies, cloud banks), drawing the same pixels | 15 min | `wrote ...meshes.bin` |
| `convert_audio` | `audio\*.ogg`, about 4,000 clips, 390 MB (Ogg Vorbis, libvorbis through the `soundfile` package) | 10 min | `0 errors` |

The texture steps run their workers at below-normal priority, so the PC
stays usable. When they are done you can delete `work\GameData\textures`
(14 GB): only `convert_console` reads it, so convert again before running
that step another time.

**Check:** `work\GameData` holds `objects`, `ctex`, `ctex4k`, `ctexc`
(with `ctexmap.bin`) and `audio`.

To cut one atlas's sprite pages again without the whole `ctex4k` folder (it
can be deleted once `ctexc` is made), convert just its bundle and merge it
into the table, for example the DLC island map's:

```
python -I tools\convert_textures.py --game game --out work\GameData --files none --bundles atlas_world_dlc
python -I tools\convert_console.py --data work\GameData --max-size 8192 --out work\GameData\ctex4k --halved-only --keys <its texture's key>
python -I tools\cluster_console.py --data work\GameData --only bundles/atlas_world_dlc/ --merge
```

The texture's key is its `.tex` file's name in `work\GameData\textures`
without `.tex`. The atlas's sprite pages now hold other sprites under the
same names, so drop its lines from the learned lists (`console\learned`,
`work\console\learned` and the `learned` folder next to the XEX).

If The Delicious Last Course is installed, its asset bundles are converted
with the rest (`work\GameData\objects\bundles\atlas_world_dlc` is there),
and the port turns the DLC on: its island, Ms. Chalice and its shop items.
Without them it plays as the base game.

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
python tools\build_console.py --stage xsdk --release --distro Debian
```

`--release` makes the release build: the default log and nothing else. The
development switches described under "Play" (`invincible.txt`, `env.txt`,
`profile.txt`, `full60.txt`, `nofold.txt`, `nodecommit.txt`) and the CPU
probes at start are left out of it; its log's first line ends with
"release build". Without `--release` you get the development build, with
all of them. Switching between the two compiles only two files again.

It runs `console/xsdk/build.sh` in the distro, which compiles the shaders,
the player's C and the host program, two files at a time (GCC takes over a
gigabyte for each of the player's large files; `--jobs 4` compiles four at
once on a PC with the memory for it). The first build takes 30 to 60
minutes; after a change only what changed is compiled again.

**Check:** it ends with `BUILD_CONSOLE OK: xsdk`, and
`work\console\xsdk\default.xex` (about 100 MB) and `work\console\xsdk\licenses`
exist.

## 8. Put it on the USB drive

**If 360convert.exe made the data**, add your build to its folder (here
`D:\cuphead360`):

```
python tools\stage_console.py --to D:\cuphead360 --build-only
```

It copies `default.xex`, `GameData\PLAYER.DAT` and `licenses\` there and
makes `save\`. Then copy the whole `cuphead360` folder to the USB drive's
`Apps` folder (`E:\Apps\cuphead360`). After a later build, run the same
command and copy `default.xex` and `GameData\PLAYER.DAT` again.

**If you converted the data with the tools**, stage everything at once.
With the USB drive as `E:`:

```
python tools\stage_console.py --to E:\Apps\cuphead360
```

The first time, it packs the textures and sounds into a few large files
(`CTEX0.PAK`, `CTEX1.PAK`, `AUDIO0.PAK`, each under 2 GB) in
`work\console\packs`, which takes a while; later it packs again only what
changed. It also writes `AUDIOINFO.BIN` there, each sound's length read
from its file, so the console can start long sounds without waiting for
them to load. Then it copies what is new or changed to the drive. Saves already
on the drive are kept.

**To play from the console's hard drive**, copy the same `cuphead360` folder
there instead (for example to `Hdd1:\Apps\cuphead360` over FTP) and start
`default.xex` from it; the saves go in its `save\` folder. Every name in the
folder is at most 42 characters, the longest the hard drive takes (staging
stops if one isn't). Data converted before 2026-11-28 had eight longer folder
names: staging renames them, and what is keyed by them, in place and packs the
changed files again, so run the staging command once more before copying.

**Check:** `E:\Apps\cuphead360` holds `default.xex`, `GameData\` (with
`objects`, `fonts`, the `.PAK` files, `AUDIOINFO.BIN` and `PLAYER.DAT`),
`licenses\` and `save\`. `default.xex` and `PLAYER.DAT` belong together:
copy both after every build (the console stops at start, saying so in its
log, if they come from different builds).

## 9. Play

Eject the drive, plug it into the Xbox 360, and start
`Apps\cuphead360\default.xex` from your homebrew launcher. Hold **Back +
Start** to quit to the dashboard.

The game writes `cuphead360.txt` next to `default.xex`. If something goes
wrong, include that file when you report it, and say what you were doing.

To see how fast each scene ran, copy that file to your PC and run:

```
python tools\perf_report.py cuphead360.txt
```

It prints a row per scene: the time between frames, the share of frames at
60, 30 and 20 fps, where the game's time went, the GPU's time by kind of
pass, and what holds the scene back (the game on the CPU, the GPU, or
hitches).

To compare runs scene by scene, keep each run's log in its own folder
(`work\console\<run>\cuphead360.txt`) and run
`python tools\compare_runs.py <run> <run> ...`.

To find what will be slow on the console before playing it there, run
`python tools\level_costs.py` after building the PC player. It plays every
level and map on the PC with cost counters (the engine's phases, physics
pairs and casts, what each frame draws, the scripts' time) and writes
`work\costs\report.txt`: per level an estimate of the console's frame time,
its worst stretch and what leads it, then the causes shared by several
levels. The estimate is calibrated from a console run's log
(`--console <run>`), so it is only as current as that run.

By default the game times its own phases and logs each scene's frame times,
hitches and texture counts. Two empty files next to `default.xex` change
that: `quiet.txt` turns the phase timing off too (only frame times and
hitch counts stay), and `profile.txt` (a development build only) turns on
everything (each script's time, a line per texture loaded and per slow
frame), which costs some smoothness. `nolog.txt` writes nothing after the log's first line, to judge
by feel what logging costs. Delete the file to go back. The log is written
to the drive only while a loading screen is up, when the game stops for a
moment, and when you quit, so play never waits behind it.

The run-and-gun levels run at a locked 30 frames a second, drawn at 60 (the
console draws a frame in between, each sprite moved on by half its last
step); the boss fights run at a real 60. `full60.txt` next to `default.xex`
(a development build only) turns the locked 30 off everywhere. `locked30.txt` names more levels to run
that way, one a line, by their scene name with or without `scene_level_`
(for example `flying_blimp` for Hilda Berg); `#` starts a note.

For playing every level through as a test, `invincible.txt` next to
`default.xex` (a development build only) keeps the game's own debug
invincibility on: hits and pits
take no hearts. The log's first lines say so. Delete it for normal play.
On the PC the same switch is the player's `--invincible 1` argument.

To see where a level's frames wait for textures without a console run, run
the PC host (`work/console/native/host_native.exe`) with
`CH360_TRACE_DRAWN=<file>` and a bot, from a folder whose `GameData` holds the
texture packs, and give the file to `tools/texture_cache_sim.py`: it replays
the frames through a model of the console's texture memory and loader and
prints the frames that would wait, for a room you choose (`--decoded`),
the locked 30 (`--hz 30 --every 2`) and other settings.

Two more PC checks for the player itself (set in `CH360_GUEST_ENV` for the
PC host, or the environment for the .NET player): `CH360_LOG_STACK=<text>`
writes the stack behind the first game log messages holding the text, and
`CH360_TRACE_VOICES=<n>` lists the clips playing whenever n, 2n, 3n...
sounds play at once. Like Unity, at most 32 sounds are heard at once; the
least important past that play silently until they end.
`CH360_TRACE_SPAWN=<name>,<name>` follows every object made from a prefab
whose name holds one of the names: `SPAWN` lines for where it was made,
each of its first 24 frames' sprites (drawn where, at what alpha, or why
not) and how many frames drew it before it went (`*` lists every spawn's
name, to find them). The draw lines need a frame list built every frame:
the PC host, the console, or the .NET player with `CH360_LIST_STATS=1`.
`CH360_NO_UI_CULL=1` lists every canvas mesh, on the view or not (the
renderer leaves out UI meshes wholly off the screen: the credits' 330 lines
of text, a few on screen).

A development build takes the same switches from `env.txt` next to `default.xex`:
`NAME=VALUE` lines (`#` starts a note) added to the player's environment,
and the log's first lines say how many. For example
`CH360_TRACE_SPAWN=OldMan_FX_DeathPuff,Oldman_FX_Hat,Oldman_FX_Shoe`
follows Glumstone's gnomes' death pieces. Delete it for normal play.

The console reads textures ahead by what each level drew before: it keeps,
per scene, the textures in the order its plays first drew them
(`learned/<scene>.txt` next to `default.xex`, updated as you play) and asks
for the next few while you play. Its starting lists come from
`python tools/learned_lists.py --jobs 5`, which plays every level and scene
with the bot on the PC host (every boss state, then the results screen) and
writes them to `work/console/learned`. Lists from your own console plays
ship ahead of those: after a console session,
`python tools/import_learned.py --from E:\Apps\cuphead360\learned` merges
the console's lists into `console/learned` (kept in the repository), and
`tools/stage_console.py` copies each scene's list into `GameData/learned`,
the console's where there is one, else the bot's. Delete the `learned` folder
next to the XEX to start over from the shipped lists. As a scene starts, the
pages its list says the first two seconds draw are read while the loading
screen holds (the log's `HOST first seconds' pages` line), along with the
equipped weapons' shots, EX shots and super (the `LEVEL hold` line's "for
the player's weapons and super"). The loading screen's frames are kept and
drawn again whenever the game is held up behind it, with the hourglass or
behind the plain black screen King Dice's casino and the King's Leap use.

The game's .NET runtime reserves its memory in 16 MB blocks and uses part of
each. The console gives the unused parts back to the textures and takes them
again when the runtime asks (the log's `frame` lines say how much is given
back). `nodecommit.txt` next to `default.xex` turns that off, so every block
stays whole as it did before; use it only to tell whether a problem comes
from there.

`python tools/level_textures.py --jobs 5` plays every level with the bot on
the PC host and prints, per level, how many MB of sprite pages one frame
draws and how many are drawn within 2 and 5 seconds (a looping animation's
frames all come round within its loop, so that is what has to be in memory
together), against the room the console's textures have (`--room`), and the
image effects the level runs on cameras of its own. `--report` prints the
table again from the traces it kept in `work/leveltex`.
`python tools/texture_density.py <trace>` reads the same traces and says how
many screen pixels a texel of each page covered at most: at 720p nearly all
of the art is drawn at one pixel a texel, so smaller pages would show.
With `--dump 60` it also keeps every 60th frame's draw list and prints, per
level, the render passes, the pixels drawn into render textures and an
estimate of the console's GPU time a frame (fitted to a console run's
measured scenes); `--gpu` prints that table again. With `--cover` it records
which 4x4 blocks of each atlas page the sprites' triangles read, in every
level and scene, merged into `work/console/cover.bin`.
`python tools/remap_check.py <dump> work/GameData` checks a draw dump against
the full-size pages: each item's texels, and every texel under its triangles
that bilinear filtering can read.

Image effects that run over an empty layer are left out: a camera that draws
nothing, blurred and copied, gives a transparent picture, so its passes and
the full-screen sprite that shows it are skipped (Glumstone's geese, about
half its GPU time), and a clear that a later pass overwrites unread is
dropped. The picture is the same. `nofold.txt` next to `default.xex` draws
every pass as the game asks.

Some levels draw more art within a couple of seconds than the console has
room for (Glumstone's first phase about 150 MB against about 195), and they
keep reading pages they let go a moment before. Those levels are drawn from
smaller sprite pages. `console\artscale.txt` names them: a line such as
`oldman 75` ships that atlas at 75% (the pack holds its pages only at that
size, under the full pages' names), and `try flyinggenielevel 75,85` packs
copies beside the full pages to try. `python tools/scale_pages.py --data
work\GameData` makes the smaller pages it names (in `work\GameData\ctexs`);
stage_console.py packs them and writes `GameData\artscale.txt`. An
`artscale.txt` next to `default.xex` chooses copies to try, a line such as
`flyinggenielevel 85`. A line for one atlas overrides the line for a group
that covers it (the longest name that matches wins: `oldman 75` with
`oldman_phase1 65`). The log's `artscale:` lines say what was used. `python tools/level_textures.py --phases` prints, per boss phase (and
per 20 seconds of a run-and-gun level), how many MB of pages it draws within
2 seconds, and marks the phases in Glumstone's range.

For testing levels one after another, `python tools/unlock_save.py --from
<a slot .sav> --to <another slot .sav>` writes a slot with every level won on
Normal (the Devil and Chef Saltbaker left as they were), so the maps' bridges,
boats and doors are open; it won't replace a slot that holds progress
without `--force`, and keeps the old file as `.bak`.

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
