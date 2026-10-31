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

Rebuilt XEX with the expanded metadata format (MethodSpec MemberRefs, FieldRVA
static-array data and enum constants) and the playable tutorial slice:

```
[INFO] [boot] platform=xbox360 _MSC_VER=1600
[INFO] [boot] big_endian=1
[INFO] [metadata] loaded game:\GameData\metadata.chm types=7417 ...
[INFO] [unity] bound 186 gameobjects, 277 components
[INFO] [play] result=PASS start=0.0,-180.0 max_x=900.0 max_y=21.7 min_y=-185.0 jumped=1 landed=1 dashes=2
```

The XEX wrote `game:\play.45.bmp`, `play.100.bmp`, `play.200.bmp`, and
`play.final.bmp`. This verifies the expanded runtime on big-endian PowerPC.

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

## Original player stack harness

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_prefab\scene.chs --stack --headless ^
  --log build\logs\stack.log
```

Observed:

```
[INFO] [stack] Level.Current set, PlayersCreated=true
[INFO] [stack] get_motor ok=1 type=LevelPlayerMotor PASS
[INFO] [stack] player Awake PASS
[INFO] [stack] prefab component Awakes 11/11
[INFO] [stack] player LevelInit PASS (last_error=0)
[INFO] [stack] player LevelStart PASS
[INFO] [stack] result=PASS
```

This verifies generic `GetComponent<T>`, the entire player-prefab `Awake`
chain (motor, animation, collider, weapon, damage, stats, camera), and the
original `LevelPlayerController.LevelInit`/`LevelStart`. The dialogue database
is an explicit empty scaffold for now; the real asset still needs extraction.
The next gameplay step is running the original motor/input loop.

## Original player motor

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_prefab\scene.chs --motor --headless ^
  --log build\logs\motor.log
```

Observed:

```
[INFO] [stack] motor moved start=0.00,-180.00 max_x=816.67 end=816.67,-211.26 min_y=-213.29 PASS
[INFO] [stack] result=PASS
```

The motor moves and stays on the tutorial level with synthesized input.
`--motorplay` adds rendering/camera follow; its logic passes, but the
captured camera-follow frames were black because the prefab scene's drawable
content is the oversized static player art rather than the tutorial level.
This is not a validated visual slice.

## Latest status (current runtime)

- Windows `--logic`, `--stack`, `--motor` and the native `--play` tutorial
  slice all PASS with the current runtime; `--play` reports
  `result=PASS ... jumped=1 landed=1 dashes=2`.
- The Xbox 360 XEX builds and boots under Xenia. A full 240-frame play run
  passed before the newest runtime changes. The most recent Xenia run reached
  frame 90 before the background harness ended, so full big-endian
  revalidation of the newest changes is still pending.
- The original player motor moves and grounds on Windows
  (`motor moved ... PASS`). Its rendered mode (`--motorplay`) now draws
  Cuphead in the tutorial scene with camera follow after loading textures
  and honoring scene active flags. The player art is still oversized and
  partly overlapping because the Animator state machine/sprite animation are
  not implemented; it is a logic and rendering smoke test, not a polished
  playable level.

This verifies the runtime/metadata/scene/script foundation and the beginning
of the original player lifecycle. It does **not** verify any complete level,
boss, menu, save, audio, or DLC gameplay. See `docs/STATUS.md` for the
implemented/validated/missing breakdown and roadmap.
