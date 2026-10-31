# Handoff

## Environment

- Project: `F:\downloads\temp\python\hermes\cuphead360`
- Game (gitignored): `game/`
- XDK: `C:\Program Files (x86)\Microsoft Xbox 360 SDK` (`XEDK`)
- XDK compiler: `_MSC_VER=1600`, big-endian PowerPC
- VS2010: `C:\Program Files (x86)\Microsoft Visual Studio 10.0`
- MSBuild: `C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe`
- ILSpy CLI: `F:\downloads\temp\python\hermes\spire360\out\tools\ilspycmd.exe`
- Xenia Canary: `F:\downloads\xenia_canary_windows\xenia_canary.exe`
  (local test copy in `work/xenia_test/`)

## Build

```
scripts\build_win32.bat Release
scripts\build_xbox360.bat Release
```

Outputs: `build\win32\Cuphead360.exe`, `build\xbox360\default.xex`.

## Generate metadata and content

```
python tools\build_metadata.py game\Cuphead_Data\Managed\Assembly-CSharp.dll work\metadata\metadata.chm
python tools\extract_scene.py --game game\Cuphead_Data --scene scene_level_tutorial --out work\extract_tutorial_anim2
```

Metadata package: `work\metadata\metadata.chm` (8.4 MB).
Scene package: `scene.chs`, `anim.chs`, cropped `sprites/*.rgba` + `.png`.

## Run tests

Windows logic (all original-IL checks):

```
build\win32\Cuphead360.exe --logic --metadata work\metadata\metadata.chm --log build\logs\logic.log
```

Windows scene render:

```
build\win32\Cuphead360.exe --scene work\extract_tutorial_anim2\scene.chs --anim-time 0.5 --screenshot build\tutorial.bmp
```

Xbox 360 scene:

1. Copy `scene.chs`, `sprites/` into `build\xbox360\GameData\`.
2. Run `build\xbox360\default.xex` in Xenia or on hardware.
3. The current XEX boots the scene viewer (see `src/platform/xbox360/main_xbox360.cpp`).
   It writes `game:\cuphead360.log` and `game:\frame.bmp`.

Xbox 360 logic mode: set `cfg.command = "logic"` in
`src/platform/xbox360/main_xbox360.cpp`, copy `metadata.chm` to
`build\xbox360\GameData\metadata.chm`, rebuild and run. Last verified log:
all Ease/PlayerInventory/VelocityManager tests PASS under Xenia.

Xbox 360 Unity-bridge mode: set `cfg.command = "unity"` and provide both
`metadata.chm` and `scene.chs`. Last verified log: 185 GameObjects / 276
components bound; `TransformExtensions.ResetLocalTransforms`,
`AnimationHelper.Update`, and `AbstractMonoBehaviour.Awake` all PASS under
Xenia big-endian.

The currently committed XEX source boots `play` mode: a scripted 240-frame
tutorial movement/dash/jump slice with D3D9 presentation and
`game:\play.*.bmp` screenshots.

Playable slice preparation and commands are in `docs/PLAYABLE_SLICE.md`:

```
python tools\extract_scene.py --game game\Cuphead_Data ^
  --scene scene_level_tutorial --out work\extract_play --add-player
```

Windows automated play:

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_play\scene.chs --play --frames 240 --headless ^
  --screenshot work\extract_play\play --log build\logs\play.log
```

Unity-bridge test scene with full serialized-field recovery:
`work\extract_tutorial_full4\scene.chs`. `--unity` verifies
`TransformExtensions.ResetLocalTransforms`, serialized
`speed/layer`/string/PPtr/array/nested values, and the lifecycle sequence
`LevelPlatform.Awake`, `PlayerDeathEffect.Start`, `AnimationHelper.Update`,
plus `AbstractMonoBehaviour.Awake`, on both Windows and Xenia.

Original player stack harness:

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_prefab\scene.chs --stack --log build\logs\stack.log
```

Prepare the prefab scene with:

```
python tools\extract_scene.py --game game\Cuphead_Data ^
  --scene scene_level_tutorial --out work\extract_prefab --add-player-prefab
```

Current observed state: `get_motor`, player `Awake`, and all 11 prefab
component `Awake` methods pass. `LevelInit` stops in
`PlayerStatsManager.LevelInit -> PlayerData -> Dialoguer` at
`UnityEngine.Resources.Load`; save/dialogue content loading is the next
blocker.

Command:

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_tutorial_full4\scene.chs --unity --log build\logs\unity.log
```

## Current code map

- `src/core/` — endian, files, logging, memory, buffers.
- `src/runtime/metadata.*` — `.chm` loader, tokens, fields/methods.
- `src/runtime/interpreter.*` — IL execution, objects, statics, vtables.
- `src/runtime/native.cpp` — Mathf, String, Vector2, List<T>.
- `src/engine/scene.*` — `.chs` scene loader, transforms.
- `src/engine/renderer_soft.*` — software sprite renderer, BMP.
- `src/engine/animation.*` — `.chs` animation loader/player.
- `src/platform/display.cpp` — Win32 window / D3D9 present.
- `src/app/app.cpp` — commands: selftest, inspect, logic, scene.

## Known issues / next actions

- `display.cpp` Xbox D3D9 uses `D3DUSAGE_CPU_CACHED_MEMORY` and a managed
  texture; device init and presentation execute in Xenia, but visual capture
  of the host window was black (likely PrintWindow limitation). The
  framebuffer screenshot proves rendering; confirm D3D output manually.
- `Scene::LoadTextures` keeps the whole `FileReadAll` block and points
  `pixels` at offset 16 (owned pointer stored separately).
- Unity external types are stubs; only Mathf/String/Vector2/List are native.
- MonoBehaviour serialized fields are not parsed yet.
- `Scene`/`AnimationPlayer` are a viewer, not the game loop.
- The XEX currently runs the scene viewer for 120 frames; the final boot
  flow must be replaced with the real game.
- Metadata decompilation/analysis lives in `work/` (gitignored).

## Git

- Remote: `https://github.com/dougieprishpreed/cuphead360` (private)
- Do not commit `game/`, `work/`, `build/`, or extracted assets.
