# Historical foundation progress

This is an early, superseded milestone record, retained for its historical
evidence. Its checklist and next steps are not current acceptance claims.
Use [STATUS.md](STATUS.md) and [VERIFICATION.md](VERIFICATION.md) for current
results, [HANDOFF.md](HANDOFF.md) for recovery state, and
[NEXT_SESSION_PROMPT.md](NEXT_SESSION_PROMPT.md) to continue in a new session.

Status legend: **Not investigated / Recovered / Implemented / Windows tested /
XDK built / Xenia tested / Hardware tested**

## Completed in this session

### Environment and source identification

- [x] Located and verified the GOG installation at `game/`.
- [x] Identified Cuphead `bundleVersion` 1.3.2, GOG build
  `55543280706943035`, Unity 2017.4.9f1, Mono backend.
- [x] Confirmed The Delicious Last Course is present in the same build:
  DLC scenes in `BuildSettings`, DLC types in `Assembly-CSharp.dll`,
  DLC asset bundles, DLC progression fields.
- [x] Inventoried managed assemblies, 79 scenes, 5.22 GB of AssetBundles,
  87,659 sprites, 1,108 textures, 132 AudioClips.
- [x] Verified the XDK PowerPC compiler (`_MSC_VER=1600`), VS2010 x86
  compiler, MSBuild, XDK libs/headers, and Xenia Canary.
- [x] Confirmed the private GitHub repository and credential access.

### Shared platform foundation

- [x] Portable core: fixed-width types, explicit LE/BE readers, file I/O,
  logging, tracked allocator, dynamic buffers, string builder.
- [x] Windows executable and Xbox 360 XEX built from the same core sources.
- [x] Windows executable runs self-tests (endian/memory/file/containers) PASS.
- [x] XEX loads and logs on Xenia.
- [x] Xbox 360 build produces `build\xbox360\default.xex`.

### Metadata recovery

- [x] Python metadata reader over the original `Assembly-CSharp.dll`:
  5,639 game TypeDefs, 37,524 fields, 34,747 methods, 34,266 methods with IL,
  2,590,233 IL bytes, 6,834 unique user strings.
- [x] Signature parser for all CLI element types, generics, arrays,
  byrefs, custom modifiers, locals, method refs, type specs.
- [x] IL decoder validated against all 34,266 method bodies with zero
  undecoded methods.
- [x] Field layout/static offsets/vtable slots computed; no generic game types
  or methods (generics are BCL-only).
- [x] Compact `metadata.chm` package (8.4 MB) generated reproducibly.
- [x] C++ package loader works identically on x86 Windows and big-endian
  Xbox 360 under Xenia (verified in logs).

### Runtime / interpreter

- [x] Metadata-driven IL execution runtime with objects, fields, statics,
  arrays, structs, virtual dispatch, static constructors, delegates
  (basic), and native method dispatch.
- [x] Native `UnityEngine.Mathf` functions.
- [x] Native `System.Object`, `System.String` (core operations).
- [x] Native `UnityEngine.Vector2` value type and operators/fields.
- [x] Native `System.Collections.Generic.List<T>` (Add/Count/Indexer/Remove/
  Clear/Contains/GetEnumerator) with native enumerator.
- [x] Original `EaseUtils` pure methods execute and match a .NET reference
  harness for 18 cases.
- [x] Original `EaseUtils.Ease` switch dispatcher executes and matches.
- [x] Original `PlayerData/PlayerInventory..ctor` executes: creates a
  `List<Weapon>` and adds both starting weapons (count 2).
- [x] Original `LevelPlayerMotor/VelocityManager.get_Total` executes with
  real movement math and returns expected `(100, -202.5)` for a controlled
  state.
- [x] All the above PASS on Windows **and** on the Xbox 360 XEX in Xenia
  (big-endian), with logs captured.

### Managed Unity bridge

- [x] Bind scene GameObject/Transform/SpriteRenderer/Animator/Camera and
  scene MonoBehaviour components to managed interpreter objects.
- [x] Implement native `GameObject`, `Component`, `Transform`, `Behaviour`,
  `MonoBehaviour` basics, `Vector2`/`Vector3`, `GetComponent<T>`.
- [x] Execute original `TransformExtensions.ResetLocalTransforms` IL against a
  bound scene: position/scale changed in the real scene graph.
- [x] Execute original `AnimationHelper.Update` and
  `AbstractMonoBehaviour.Awake` lifecycle methods.
- [x] Recover serialized MonoBehaviour primitive/enum/Vector2/Vector3 fields
  from scene data and initialize script object fields in the runtime.
- [x] Recover serialized strings (`DeferredAudioSource.audioClipName`),
  GameObject/component PPtrs (`PlayerDeathEffect.cuphead`), arrays
  (`CupheadGlyph.rectTransformTexts`, length 6), and nested serializable
  objects (`TutorialLevel.intro`).
- [x] Two-pass scene binding: all component objects exist before PPtr field
  values are applied, so forward references resolve.
- [x] Execute a complete original lifecycle sequence on real scene scripts:
  `LevelPlatform.Awake`, `PlayerDeathEffect.Start`, and
  `AnimationHelper.Update`.
- [x] Verify all of the above on Windows and on the Xbox 360 XEX in Xenia.
- [x] `AnimationHelper` serialized `speed`/`layer` values verified on both.

### Playable tutorial vertical slice

- [x] Added `--add-player` scene preparation using real `atlas_player`
  Cuphead idle/run/jump/dash sprites.
- [x] Added Windows/Xbox input backend (keyboard/XInput).
- [x] Added a playable movement slice: run, jump (variable hold), gravity,
  two dashes with cooldown, ground collision, camera follow, and
  idle/run/jump/dash animation selection.
- [x] Horizontal velocity target comes from the original
  `LevelPlayerMotor/VelocityManager.get_Total` IL.
- [x] Runs `LevelPlatform.Awake`, `PlayerDeathEffect.Start`, and
  `AnimationHelper.Update` on bound scene scripts during the slice.
- [x] Windows automated run PASS: moved >100, jump height >80, landed,
  2 dashes.
- [x] Xbox 360 XEX in Xenia PASS with the same state trace and screenshots.
- [ ] The full original `LevelPlayerController`, weapon manager, damage,
  enemies, audio, UI, saves, and original level colliders are not yet
  integrated. See `PLAYABLE_SLICE.md`.

### Content pipeline and renderer

- [x] Scene extractor for Unity 2017 serialized scenes:
  GameObject/Transform hierarchy, SpriteRenderer, Animator, Camera.
- [x] Atlas lookup by `SpriteAtlas` render-data key; crops used sprites into
  portable RGBA `CHTX` files.
- [x] Compact `scene.chs` format consumed by C++.
- [x] Animation extractor for Unity StreamedClip frames, controller states,
  sprite PPtr curves, path-CRC binding to GameObjects.
- [x] Compact `anim.chs` format; native animation player applies default
  state sprite tracks.
- [x] Software renderer with camera, sprite quads, alpha blending, sorting,
  colour tint, flips; BMP screenshots.
- [x] Windows and Xbox 360 software-rendered tutorial scene screenshots
  produced from real game data.
- [x] D3D9 presentation backend for Xbox 360 (device/texture/quad/present).

## Verified evidence

- Windows logic log: `build/logs/logic7.log` — `result=PASS` with all
  Ease/PlayerInventory/VelocityManager cases.
- Xenia logic log: `build/xbox360/cuphead360.log` — same cases PASS on
  big-endian PowerPC, including movement math.
- Xenia scene log: `build/xbox360/cuphead360.log` — scene, 25 textures,
  D3D9 device, screenshot written.
- 360 framebuffer screenshot: `build/xbox360/frame.bmp` (rendered by the
  XEX; can be compared with `work/extract_tutorial_anim2/render.png`).

## Not yet implemented

- Managed Unity object model (`GameObject`, `Transform`, `Component`,
  `MonoBehaviour`) and scene-instance binding.
- MonoBehaviour serialized-field recovery/initialization (objects bind but
  fields are zeroed).
- Unity message lifecycle/coroutines driven by game scripts (basic
  `Awake`/`Update` calls work; no scheduler).
- Input (Rewired-facing API / XInput), physics/collision, audio.
- Full player controller, boss logic, menus, saves, DLC progression.
- All 79 levels and DLC playable content.
- Xenia visual validation of the D3D9 presentation (software framebuffer
  screenshot is validated; D3D9 output capture is limited by Xenia's
  window capture).

## Next priorities

1. Build the managed Unity object model and bind scene objects/components to
   interpreter objects.
2. Parse MonoBehaviour serialized fields (using the game's metadata field
   order) and instantiate original scripts.
3. Drive `Awake/Start/Update` and coroutines; implement Transform/Renderer/
   Animator/Input natively.
4. Implement the player controller and a tutorial-level playable slice.
5. Add physics, audio, and save/progression.
6. Expand content coverage and regression tests.
