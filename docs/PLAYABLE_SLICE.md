# Playable tutorial vertical slice

## What runs

`--play` loads the tutorial scene with an added Cuphead player entity and runs
a deterministic 240-frame gameplay slice:

- horizontal run using the original `LevelPlayerMotor/VelocityManager.get_Total`
  IL as the velocity target;
- jump with variable hold, gravity, terminal velocity, landing;
- two dashes with cooldown;
- ground collision (native AABB floor);
- camera follow;
- idle/run/jump/dash sprite selection from the real Cuphead player atlas;
- scene lifecycle calls: `LevelPlatform.Awake`, `PlayerDeathEffect.Start`,
  and `AnimationHelper.Update` each frame.

## Prepare the slice

```
python tools\extract_scene.py --game game\Cuphead_Data ^
  --scene scene_level_tutorial --out work\extract_play --add-player
```

`--add-player` loads `atlas_player`, crops the Cuphead idle/run/jump/dash
sprites, and adds a `Player` GameObject with a SpriteRenderer to the scene.

## Windows automated run

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_play\scene.chs --play --frames 240 --headless ^
  --screenshot work\extract_play\play --log build\logs\play.log
```

Expected last line:

```
[INFO] [play] result=PASS ... jumped=1 landed=1 dashes=2
```

Screenshots are written as `play.45.bmp`, `play.100.bmp`, `play.200.bmp`, and
`play.final.bmp`.

## Manual Windows controls

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_play\scene.chs --play --manual --windowed
```

- A / D: move
- Space: jump (hold for higher jump)
- Shift: dash
- Escape: exit

## Xbox 360

The current `projects/xbox360` source boots `play` mode with scripted input,
240 frames, and writes `game:\play.*.bmp` plus `game:\cuphead360.log`.
Copy `work\extract_play\scene.chs`, `sprites/`, and `metadata.chm` into
`build\xbox360\GameData/`, then run `default.xex` in Xenia or on hardware.

## Honest scope

This is a playable movement/rendering slice, not the full original player
controller. Jump/dash state, the AABB floor, and camera follow are native
scaffolding for the slice. The original controller scripts are bound and
their lifecycle methods run, but `LevelPlayerController`,
`LevelPlayerAnimationController`, weapon management, damage, enemies, audio,
UI, and saves are not yet integrated. The tutorial level's original colliders
and level flow are not yet used.
