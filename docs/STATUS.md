# Current status and roadmap

This repository is a **development foundation for a native Xbox 360 port of
Cuphead**, not a finished game. It does not yet meet the goal of a fully
playable alpha release candidate with no content or behavior gaps versus the
Windows version. This document records exactly what is implemented, what has
been validated, and what remains.

## What works

### Build and platform

- Windows and Xbox 360 builds share `src/core`, `src/runtime`, `src/engine`,
  `src/game`, and `src/platform` code.
- `build\win32\Cuphead360.exe` and `build\xbox360\default.xex` build with
  VS2010 and the Xbox 360 XDK (`_MSC_VER=1600`, big-endian PowerPC).
- D3D9 presentation and a software sprite renderer run on both targets.
- The XEX runs under Xenia Canary; the native tutorial play slice reports
  `result=PASS` and writes real framebuffer screenshots.

### Original code execution

- A metadata package (`work\metadata\metadata.chm`) preserves the original
  `Assembly-CSharp.dll` IL, fields, methods, signatures, user strings, enum
  constants and FieldRVA initializer data.
- A big-endian-safe IL interpreter executes original methods, including
  objects, fields, statics, arrays, structs, vtables, generic MethodSpec
  calls, delegates/events, `List<T>`, `Dictionary<K,V>`, array enumeration,
  boxing/unboxing, `FieldRVA` initialization and enum values.
- Verified original-IL tests: `EaseUtils` (including switch dispatch),
  `PlayerData/PlayerInventory..ctor`, and
  `LevelPlayerMotor/VelocityManager.get_Total` (`(100,-202.5)`).

### Scene and asset pipeline

- `tools\extract_scene.py` reads the user's GOG installation and writes
  compact `CHSC` scene and `CHAN` animation packages plus cropped RGBA
  textures. It never modifies the installation.
- Serialized fields support primitives, enums, strings, PPtrs, arrays,
  nested `[Serializable]` classes (recursively), and prefab-internal PPtr
  rebasing. The `[NonSerialized]` field flag is honored.
- The tutorial scene includes real sprites, colliders and the runtime Level
  bounds colliders.
- The player prefab subtree is extracted with the full controller stack:
  `LevelPlayerController`, `LevelPlayerMotor`, `PlayerInput`,
  `LevelPlayerAnimationController`, `LevelPlayerColliderManager`,
  `LevelPlayerWeaponManager`, `PlayerDamageReceiver`, `PlayerStatsManager`,
  `PlayerCameraController`, `PlayerRecoveryHandler`, `Animator`,
  `SpriteRenderer`, `BoxCollider2D` and `Rigidbody2D`.

### Original player lifecycle

- `--stack` drives the tutorial player prefab through:
  - generic `GetComponent<LevelPlayerMotor>()`;
  - all 11 prefab component `Awake` methods;
  - `LevelPlayerController.LevelInit(PlayerId.PlayerOne)`;
  - `LevelPlayerController.LevelStart()`.
- Result: `PASS` on Windows.
- `--motor` additionally runs `LevelPlayerMotor.Start` and
  `LevelPlayerMotor.FixedUpdate` with synthesized input. The motor moves the
  player and keeps it on the tutorial ground:
  `max_x=816.67 min_y=-213.29 PASS`.
- `--motorplay` adds software rendering, camera follow, and a native sprite
  animation bridge. It draws an animated Cuphead in the tutorial scene by
  selecting extracted idle/run/jump/dash frames from the motor state. This
  is not the original Animator state machine; the player art remains
  oversized and the slice is a logic/rendering smoke test.

### Runtime primitives

- UnityEngine: `GameObject`/`Transform`/`Component`/`Behaviour`, generic and
  non-generic `GetComponent`, `FindObjectsOfType`, `GetComponentsInChildren`,
  scene and runtime GameObjects/Transforms, `Vector2`/`Vector3`/`Rect`/
  `Color`, `Time`, `Random`, `Animator.speed`/`StringToHash`, `Renderer.
  material`, `Resources.Load` (dialogue scaffold), `Physics2D.BoxCast`,
  `Raycast`, `BoxCastNonAlloc`, `RaycastNonAlloc`, `RaycastHit2D`, collider
  and Rigidbody2D properties, delegates and C# events.
- BCL: strings, chars, `List<T>`, `Dictionary<K,V>`, arrays, enums, boxing,
  `RuntimeHelpers.InitializeArray`, `Interlocked.CompareExchange`, and minimal
  XML/StringWriter support.

## What is not done

- **Level flow**: `Level.Awake`/`Level.Start`, UI/HUD creation, scene
  loading/transitions, checkpoints, intros and win/lose sequences.
- **Gameplay**: weapons, projectiles, damage, parries, supers, charms,
  enemies, bosses, hazards, coins, shops, death/revive.
- **Animation**: the Animator state machine, animation clips/parameters,
  original sprite animation, materials/shaders.
- **Audio**: the audio manager, banks/clips, music and SFX.
- **UI and menus**: title, maps, pause, equip, HUD, dialogue boxes, results.
- **Saves and player data**: the real `PlayerData` save/serialization flow and
  the real Dialoguer database. The current dialogue database is an explicit
  empty scaffold; `resources.assets` MonoBehaviour 1175 still needs its
  nested graph extracted.
- **Content**: all 79 scenes, every boss and platforming level, cutscenes,
  localization, and both DLC campaigns.
- **Physics and timing**: the current physics is an AABB approximation, not a
  faithful Box2D simulation; coroutines, fixed timestep and the camera stack
  are incomplete.
- **Hardware validation and performance**: Xenia is used for development;
  real Xbox 360 hardware and performance have not been validated.

No complete level or boss is playable with original game logic. The only
visually validated playable slice is the native tutorial movement slice,
which uses original sprites and the original velocity calculation but native
jump/dash/collision scaffolding rather than the full original controller.

## Validation commands

```
python tools\build_metadata.py game\Cuphead_Data\Managed\Assembly-CSharp.dll work\metadata\metadata.chm
python tools\extract_scene.py --game game\Cuphead_Data --scene scene_level_tutorial --out work\extract_prefab --add-player-prefab

build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm --command logic --headless --log build\logs\logic.log
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm --scene work\extract_prefab\scene.chs --stack --headless --log build\logs\stack.log
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm --scene work\extract_prefab\scene.chs --motor --headless --log build\logs\motor.log
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm --scene work\extract_prefab\scene.chs --motorplay --frames 240 --headless --screenshot work\extract_prefab\motorplay --log build\logs\motorplay.log
```

The native tutorial play slice is prepared with `--add-player` and run with
`--play`; see `docs\PLAYABLE_SLICE.md` and `docs\VERIFICATION.md`.

## Next steps toward the stated goal

1. Extract and deserialize the real `dialoguer_data_object` graph, replacing
   the empty dialogue scaffold.
2. Implement the real `Level.Awake`/`Level.Start` flow, UI/HUD, audio manager
   and scene transitions.
3. Implement the Animator state machine and sprite animation so original
   controllers render correctly.
4. Implement weapons, projectiles, damage, parry/super and enemy/boss
   behaviors, starting with the tutorial and one complete boss.
5. Expand the content pipeline to every scene and asset bundle, then validate
   each level for load/render/behavior.
6. Replace the AABB physics approximation with a faithful 2D physics layer.
7. Validate on real Xbox 360 hardware, including memory and frame time.

This is a multi-year scope; the current repository is a verified foundation,
not a claimed completion.
