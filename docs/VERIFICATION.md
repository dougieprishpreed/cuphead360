# Verification log

All results below were produced locally against the user's GOG installation.
They are development-verification results, not a claim that the full game is
playable.

## Build

```
scripts\build_win32.bat Release
scripts\build_xbox360.bat Release
```

Both complete successfully:
- `build\win32\Cuphead360.exe`
- `build\xbox360\default.xex`

## Windows original-IL logic

```
build\win32\Cuphead360.exe --logic --metadata work\metadata\metadata.chm --log build\logs\final_logic.log
```

Observed:
```
[INFO] [logic] PlayerInventory.ctor weapons=2 expect=2 PASS
[INFO] [logic] VelocityManager.Total=(100.000,-202.500) expect=(100.000,-202.500) PASS
[INFO] [logic] result=PASS
```

## Windows serialized fields + lifecycle

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_tutorial_full4\scene.chs --unity --log build\logs\unity_lifecycle.log
```

Observed:
```
[INFO] [unity] bound 185 gameobjects, 276 components
[INFO] [unity] ResetLocalTransforms after pos=0.00,0.00 scale=1.00,1.00 PASS
[INFO] [unity] AnimationHelper.Update PASS
[INFO] [unity] AnimationHelper serialized speed=1.00 layer=1 PASS
[INFO] [unity] DeferredAudioSource string 'MUS_Tutorial' PASS
[INFO] [unity] CupheadGlyph array length=6 expect=6 PASS
[INFO] [unity] PlayerDeathEffect PPtr PASS
[INFO] [unity] lifecycle LevelPlatform.Awake PASS
[INFO] [unity] lifecycle PlayerDeathEffect.Start PASS
[INFO] [unity] lifecycle AnimationHelper.Update PASS
[INFO] [unity] TutorialLevel nested intro=object child=1 PASS
[INFO] [unity] AbstractMonoBehaviour.Awake PASS
```

## Xbox 360 in Xenia

The same logic and Unity-bridge modes were run from the XEX in Xenia Canary
(big-endian PowerPC). The captured `game:\cuphead360.log` showed the same
PASS lines, including:

```
[INFO] [unity] bound 185 gameobjects, 276 components
[INFO] [unity] lifecycle LevelPlatform.Awake PASS
[INFO] [unity] lifecycle PlayerDeathEffect.Start PASS
[INFO] [unity] lifecycle AnimationHelper.Update PASS
[INFO] [unity] TutorialLevel nested intro=object child=1 PASS
```

The scene-viewer mode also loaded `scene.chs`/`anim.chs`, initialized D3D9,
rendered the real tutorial scene, and wrote `game:\frame.bmp` under Xenia.
The D3D9 host-window contents could not be captured through PrintWindow
(black capture); the framebuffer screenshot is the validated render output.

## Playable tutorial slice

Windows:

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_play\scene.chs --play --frames 240 --headless ^
  --screenshot work\extract_play\play --log build\logs\play8.log
```

Observed:

```
[INFO] [play] player object=185 component=276 idle=5 run=54 jump=22 dash=16
[INFO] [play] scene lifecycle LevelPlatform.Awake=1 PlayerDeathEffect.Start=1
[INFO] [play] frame=30 state=Jump ...
[INFO] [play] frame=90 state=Run ...
[INFO] [play] frame=180 state=Idle ...
[INFO] [play] result=PASS ... jumped=1 landed=1 dashes=2
```

Xbox 360 XEX in Xenia produced the same state trace and a PASS result, and
wrote `game:\play.45.bmp`, `play.100.bmp`, `play.200.bmp`, and
`play.final.bmp` (real framebuffer renders of the tutorial scene with
Cuphead).

## Status

This verifies the runtime/metadata/scene/script foundation. It does **not**
verify any complete level, boss, menu, save, audio, or DLC gameplay.
