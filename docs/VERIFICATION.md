## 2026-10-31 player-damage engine checkpoint (scope corrected)

This is a checkpoint for the port runtime, not normal tutorial gameplay and
not M1 completion. `TutorialLevel` has no authored attack which damages the
player; `--combat-test` intentionally calls the original
`DamageDealer.NewEnemy()` and `DamageDealer.DealDamage(player)` methods to
exercise the game's damage, receiver, stats, hit, and death code.

Windows build and run:

```text
scripts\build_win32.bat Release
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --levelplay --combat-test ^
  --frames 272 --headless --screenshot work\m1_combat_checkpoint ^
  --probe-fields --log work\m1_checkpoint_combat.log
```

Observed process exit code:

```text
EXIT=0
```

Key output:

```text
[combat] probe begin playerHealth=3 targetHealth=20 targetCi=182
[combat] hit1-first invoke=1 returned=1.00 health=2
[combat] hit1-repeat-same-dealer invoke=1 returned=0.00 health=2
[combat] hit1-iframes-fresh-dealer invoke=1 returned=1.00 health=2
[combat] hit2-after-iframes invoke=1 returned=1.00 health=1
[combat] hit3-lethal invoke=1 returned=1.00 health=0
[combat] target health initial=20 final=-4 trackedInactive=1
[combat] sequence firstHit=1 sameDealerBlock=1 freshDealerIFrameBlock=1 postIFrameHit=1 lethal=1 finalHealth=0
PASS
[combat] single-player retry=UNPROVEN requestPropagated=0
[probe] health PlayerStatsManager.<Health>k__BackingField ci=918 value=0 active=0
[levelplay] ... result=PASS
```

Historical receiver diagnosis excerpts are preserved in
[`evidence/m1-player-damage-diagnosis.txt`](evidence/m1-player-damage-diagnosis.txt).
They show a non-null `PlayerDamageReceiver`, `CanTakeDamage=1`,
`DamageTypes.Player=true`, and initialized dealer timers. The excerpt's
`enabled=0 flags=1` fields are inconsistent with the current helper's enabled-bit
calculation; do not use that historical line as a current enabled-state oracle.
The investigation changed activity/enabled handling and float comparisons.
The lead's unresolved review findings are in
[`PAUSED_HANDOFF.md`](PAUSED_HANDOFF.md).

HUD evidence was captured from the same run:

- [`health3`](evidence/m1-hud.health3.png)
- [`health2`](evidence/m1-hud.health2.png)
- [`health1`](evidence/m1-hud.health1.png)
- [`health0`](evidence/m1-hud.health0.png)

The compact combat transcript is
[`evidence/m1-checkpoint-combat.txt`](evidence/m1-checkpoint-combat.txt).
The captures validate changing HUD rectangles, but also show major scene
rendering artifacts, including atlas-like character art in the death capture.
They do not validate visual gameplay parity.

Normal tutorial regression after the final stabilization:

```text
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --levelplay --frames 60 ^
  --headless --log work\checkpoint_tutorial_play_fixed2.log

[levelplay] start=-410.0,-270.0 max_x=80.0 ... result=PASS
```

Tutorial `--level` passed before the final offline-achievement no-op. The Xbox
360 Release build also passes:

```text
scripts\build_xbox360.bat Release
Xbox 360 build OK: build\xbox360\default.xex
```

Original single-player retry is not verified. The probe invokes the original
`LevelGameOverGUI.Retry()` and reaches `SceneLoader.ReloadLevel()`, but the
current incomplete transition stack leaves `SceneLoader.SceneName` unset. The
checkpoint deliberately reports `UNPROVEN`; it does not reset health or use
player revive as a substitute. This must be completed through the normal
opening-flow scene loader and boss accept/reject work.

Audio remains a muted headless-stub path. Manual shoot input is now wired to
the existing Rewired action through keyboard `J` and Xbox right trigger, but
has not been interactively play-tested.

Not run before the requested checkpoint pause:

- full platforming 1-1F `--level` / `--levelplay` regression;
- Train, Frogs, Flower and Veggies boot/play regressions;
- real-hardware Xbox execution;
- normal new-game, house, map, dialogue, Root Pack, retry, victory, and map
  return flows.

## Historical enemy-only combat probe (superseded)

Implemented and tested the enemy-side combat loop on the Windows build:

- virtual-delegate dispatch for weapon overrides (`WeaponPeashot.fireBasic`),
- world-space transform position and persistent euler rotation,
- cloned prefab root parenting,
- effective `DamageReceiver.type` tags and `GetInstanceID`,
- real `Object.Destroy` deactivation,
- target-first collision resolution,
- `--combat-test` and `--probe-fields` development probes.

Build and run:

```
scripts\build_win32.bat Release
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --levelplay --combat-test ^
  --frames 80 --headless --probe-fields ^
  --log work\m1_final_combat2.log
```

Observed (exit code 0):

```
[combat] enemy damage frame=12 result=0.00
[combat] enemy damage frame=40 result=0.00
[combat] enemy damage frame=68 result=0.00
[combat] tutorial target seen=1 active=0
[probe] health TutorialLevelDamagableBox.boxHealth ci=182 value=-4.000 active=0
[probe] health TutorialLevelDamagableBox.boxHealth ci=1009 value=0.000 active=0
[levelplay] start=3000.0,81.0 max_x=3008.2 ... collisions=169 result=PASS
```

Tutorial regression:

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --levelplay --frames 120 --headless ^
  --log work\m1_final_reg.log
```

Observed:

```
[levelplay] start=-410.0,-270.0 max_x=938.7 ... result=PASS
```

Caveat at the time: the synthetic enemy-damage attempt returned `0.0` and
player health remained `3`. This historical result was superseded by the
player-damage engine checkpoint above and did not demonstrate retry.

## Train boss scene boot (2026-10-31)


Implemented the `System.Array.Copy` overloads used by boss timelines and
validated `scene_level_train`:

```
python tools\extract_scene.py --game game\Cuphead_Data ^
  --scene scene_level_train --out work\extract_train --add-level-resources

build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_train\scene.chs --level --headless
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_train\scene.chs --levelplay --frames 30 --headless
```

Observed:

```
[INFO] [level] result=PASS
[INFO] [levelplay] start=-330.0,-250.0 max_x=-85.0 max_y=-171.0 min_y=-283.3 collisions=74 result=PASS
```

Tutorial regression remains PASS. Boss attack/timeline behavior is still not
validated.

## Veggies boss scene boot (2026-10-31)

Added coroutine liveness validation and a temporary safety filter for known
unstable coroutine state machines. Extracted `scene_level_veggies` and ran:

```
python tools\extract_scene.py --game game\Cuphead_Data ^
  --scene scene_level_veggies --out work\extract_veggies --add-level-resources

build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_veggies\scene.chs --level --headless
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_veggies\scene.chs --levelplay --frames 30 --headless
```

Observed:

```
[INFO] [level] result=PASS
[INFO] [levelplay] start=-370.0,-240.0 max_x=655.0 max_y=-137.2 min_y=-249.5 collisions=483 result=PASS
```

The safety filter currently skips Veggies boss, homing-projectile, UI and
death-effect coroutines; the level boots and the player moves, but those
skipped behaviors remain a known parity gap.

## Flower boss scene boot (2026-10-31)

Added the generic `AbstractLevelProperties<TState,...>.CurrentState` getter
(array or list backed) and explicit most-derived `PartialInit` dispatch after
`Level.Awake`. This lets levels whose properties are created by `PartialInit`
(Flower) initialize their state machine.

Extracted and ran `scene_level_flower`:

```
python tools\extract_scene.py --game game\Cuphead_Data ^
  --scene scene_level_flower --out work\extract_flower --add-level-resources

build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_flower\scene.chs --level --headless
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_flower\scene.chs --levelplay --frames 30 --headless
```

Observed:

```
[INFO] [level] result=PASS
[INFO] [levelplay] start=-410.0,-195.0 max_x=375.0 max_y=-87.2 min_y=-199.5 collisions=693 result=PASS
```

Tutorial and Frogs regressions remain PASS after the change. Boss attacks and
phase logic are still not validated.

## Frogs boss scene boot (2026-10-31)

Fixed the content pipeline to skip Unity `Dictionary`/`HashSet` fields that
occupy no bytes in serialized MonoBehaviour payloads. This unblocked
`Level/Bounds.colliders` and, in turn, the derived `FrogsLevel` fields
(`tall`, `small`, `morphed`, `demonTrigger`).

Extracted and ran `scene_level_frogs`:

```
python tools\extract_scene.py --game game\Cuphead_Data ^
  --scene scene_level_frogs --out work\extract_frogs --add-level-resources

build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_frogs\scene.chs --level --headless
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_frogs\scene.chs --levelplay --frames 30 --headless
```

Observed:

```
[INFO] [level] result=PASS
[INFO] [levelplay] start=-410.0,-240.0 max_x=225.9 max_y=-132.2 min_y=-244.5 collisions=653 result=PASS
```

This validates boss-scene extraction, level startup and the movement/play
harness on a second boss scene. Boss attacks, phase logic, damage, UI/audio and
full-scene rendering are still not validated.

## Synthetic weapon firing and collision milestone

The level-play harness now invokes the original
`LevelPlayerWeaponManager.StartBasic` once when synthesized shooting starts,
and the runtime coroutine scheduler runs the original
`AbstractLevelWeapon.<fireWeapon_cr>` state machines. Observed on the tutorial
scene:

```
[INFO] [levelplay] instances objects=2403 projectiles=114
[INFO] [levelplay] start=-410.0,-270.0 max_x=1327.6 ... collisions=16224 result=PASS
```

Projectile clones are created and trigger collisions are dispatched. This is
not yet a validated shooting slice: the spawned projectiles sit at the weapon
prefab position, do not reach the target, and the tutorial target's
`boxHealth` remains `20.0`. The real Rewired shoot-button path, projectile
movement/despawn, damage delivery and enemy reaction are still open. The
harness trigger is a temporary adapter, not parity with the Windows game.


The extractor now reads Unity `m_TagString` for scene and prefab objects and
writes a parallel tag-offset table into `CHSC` (header word 24). The runtime
binds those tags to `GameObject.tag` / `Component.CompareTag`, so the original
`AbstractCollidableObject.checkCollision` routes walls, ground, enemies, player
and projectile hits by their real Unity tags instead of name heuristics.

Re-extracted tutorial and platforming 1-1F scenes after this change. Windows
results:

```
tutorial --level PASS
tutorial --levelplay 120 frames: start=-410,-270 max_x=1327.6 collisions=204 PASS
tutorial instances: objects=1844 projectiles=24
platforming --level PASS
platforming --levelplay 100 frames: start=-520,-93 max_x=-209.7 collisions=104 PASS
```

The projectile count includes the 24 projectile templates emitted with the
player prefab graph; end-to-end projectile damage/despawn and enemy behavior
are still not validated.

## Animator default-state playback

`--levelplay` now loads the extracted `anim.chs` alongside the scene and
advances the default Animator state of every extracted controller before the
native player sprite bridge runs. On the tutorial scene this loads 84
controllers / 304 clips / 6687 frames and drives 91 animator instances over 216
sprite targets. Animator script parameters/transitions and animation events are
still not implemented; only default states play.

## Xbox 360 / Xenia revalidation (2026-10-31)

Rebuilt with `scripts\build_xbox360.bat Release`, copied the fresh
`build\xbox360\default.xex` into `work\xenia_test`, and ran the XEX under
Xenia Canary. Observed:

```
[INFO] [boot] event=command name=play
[INFO] [metadata] loaded game:\GameData\metadata.chm types=7417 ...
[INFO] [scene] loaded game:\GameData\scene.chs objects=186 components=277 ...
[INFO] [unity] bound 186 gameobjects, 277 components
[INFO] [play] player object=185 component=276 idle=5 run=54 jump=22 dash=16
[INFO] [display] D3D9 1280x720 initialized
[INFO] [play] scene lifecycle LevelPlatform.Awake=1 PlayerDeathEffect.Start=1
[INFO] [play] frame=30 state=Jump pos=220.5,-165.2 vel=489.5,1190.0 grounded=0
[INFO] [play] screenshot=game:\play.45.bmp
[INFO] [play] frame=90 state=Run pos=753.7,-185.0 vel=359.5,0.0 grounded=1
[INFO] [play] screenshot=game:\play.100.bmp
```

Fresh `work\xenia_test\play.45.bmp` and `play.100.bmp` were written. This
validates the newest runtime on the Xbox 360 target for the native tutorial
play slice. It is not a full-level or boss validation, and the XEX does not
yet run the complete original scene graph on Xbox.

`UnityEngine.Object.Instantiate` now clones the template hierarchy and its
managed components, remaps internal GameObject/Transform/component references,
repairs cloned `LevelPlayerController` and `_basePlayer` wiring, and runs
`Awake`. Instance overloads apply position/parent arguments. Player-prefab
creation still reuses the extracted template until player cloning is validated
end-to-end; projectile/enemy prefabs use the clone path.

The level-play harness now discovers active runtime-created components and
starts/ticks their original `Start`/`Update` methods. Inactive extracted
prefab templates are no longer ticked as gameplay.

Dictionary `KeyCollection`/`ValueCollection` enumerators are implemented, so
`PlayerManager.get_Count()` and projectile damage-multiplier lookups complete.

Validation after this increment (Windows):
- tutorial `--level` PASS
- tutorial `--levelplay` PASS (`start=-410,-270 max_x=1327.6`)
- tutorial `--stack` PASS
- platforming `--level` PASS
- platforming `--levelplay` PASS (`start=-520,-93 max_x=174.2 min_y=-95.5`)

Cloned projectile components now reach `Awake`; their `BasicProjectile.Awake`
still fails on collision/tag setup, so projectile damage/despawn is the next
gameplay blocker.


All results below were produced locally against the user's GOG installation.
They are development-verification results, not a claim that the full game is
playable.

## Latest Windows runtime pass (2026-10-31)

```
scripts\build_win32.bat Release

build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --level --headless
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --levelplay --frames 120 --headless
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --stack --headless
```

Observed:
```
[INFO] [level] TutorialLevel.Awake PASS
[INFO] [level] TutorialLevel.Start PASS
[INFO] [level] result=PASS
[INFO] [levelplay] player LevelInit invoked
[INFO] [levelplay] player LevelStart invoked
[INFO] [levelplay] start=-410.0,-270.0 max_x=1327.6 max_y=-159.7 min_y=-274.5 result=PASS
[INFO] [stack] player LevelInit PASS
[INFO] [stack] player LevelStart PASS
[INFO] [stack] result=PASS
```

The latest runtime fixes in this pass:

- `Enum.GetValues`/`EnumUtils.GetValues<T>()` + `constrained.` enum
  `ToString` now agree, so weapon-prefab dictionaries populate instead of
  silently remaining empty.
- Dictionary keys compare boxed enum values numerically.
- `GameObject.activeInHierarchy` walks the parent chain instead of failing.
- The `--levelplay` harness invokes the original player `LevelInit` before
  `LevelStart`, which creates weapon prefabs/loadouts.
- The `--stack` harness repairs serialized `_basePlayer` references after
  construction, so original child-component `LevelInit` paths run.

The synthetic `--motor` harness still reports a failure in the motor's
serialized component wiring; the real `--levelplay` path passes. `--play`
requires a scene extracted with `--add-player`.

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
- `tools\extract_scene.py --add-level-resources` now resolves cross-file
  PPtrs and includes `LevelResources`, `CupheadCore`, `LevelGUI`, `LevelHUD`,
  `LevelAudio`, `PlayerJoinEffect`, the dialogue UI and the real player
  prefab. The generated tutorial scene binds 1515 GameObjects and 1993
  components.
- The original `--level` flow now PASSes on Windows:
  `TutorialLevel.Awake PASS`, `TutorialLevel.Start PASS`, `result=PASS`.
- `--levelplay` drives the real player/motor created by `Level.Start` with
  synthesized input, camera follow and software rendering:
  `start=-410.0,-270.0 max_x=804.9 max_y=-270.0 min_y=-274.5 result=PASS`.
  Screenshots: `work\extract_level\levelplay.45.bmp`,
  `levelplay.100.bmp`, `levelplay.200.bmp`.
- The level-play harness now also runs a bounded scene-wide `Awake`/`Start`
  lifecycle and ticks original non-UI gameplay scripts. Shooting spawns
  projectiles whose original `Update` methods execute. `Object.Instantiate`
  is still template reuse rather than true cloning, and damage/target
  destruction are not yet validated.
- The content pipeline now loads all shared assets, follows scene-authored
  external PPtrs and maps `RectTransform` references. The first normal
  platforming level (`scene_level_platforming_1_1F`) was converted with 2346
  GameObjects / 4619 sprites; `PlatformingLevel.Awake` and
  `PlatformingLevel.Start` both PASS on Windows.
- Generic runtime support now includes nested method-generic substitution,
  `Comparison<T>` delegates, `List<T>.Sort` and a GOG achievement-sync no-op.
- `--levelplay` on the platforming level still fails: the player falls
  through the start area. Platform/ground collider runtime support and the
  full UI/audio/Animator stack remain missing, so no normal level is playable
  yet.
- Sprite world size now uses the cropped sprite rect instead of the atlas
  rect, fixing the oversized character art in both the native play slice and
  the original-level play harness.
- The Xbox 360 XEX is rebuilt from the current runtime. Full 240-frame play
  revalidation under Xenia is recorded below once complete.
- This milestone validates original level startup and a movement/rendering
  slice. It does **not** validate complete gameplay: weapons, enemies,
  bosses, damage, parry/super, hazards, real Animator state, audio playback,
  UI rendering, saves, dialogue, menus, cutscenes, transitions and DLC remain
  absent. See `docs/STATUS.md` for the honest breakdown.

This verifies the runtime/metadata/scene/script foundation, original
`TutorialLevel.Awake`/`Start`, and the beginning of the original player
lifecycle. It does **not** verify any complete level, boss, menu, save,
audio, or DLC gameplay.
