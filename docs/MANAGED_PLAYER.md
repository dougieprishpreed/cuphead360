# Managed player (current architecture, 2026-11-01)

This replaced the custom IL interpreter as the way Cuphead's code runs. The
interpreter, its C++ Unity layer and the D1/CP1 work were removed from the
tree in round 3; they are in git history (up to `bb73ff0`), as is the port
review explaining why that path was not viable. Its still-valid content,
packaging, budget and gate sections live on in [CONSOLE_PLAN.md](CONSOLE_PLAN.md).

## Decision

Run the player's own managed assemblies as their original IL on the real .NET
runtime, and compile them ahead of time for the console with the pipeline the
user's Spire360 port already ships on Xbox 360 hardware:

```
GOG Cuphead_Data/Managed/*.dll
  -> Cuphead360.Patcher publicize   (engine-side internals made public)
  -> Cuphead360.Patcher gen         (Icalls.g.cs: a stub per engine method)
  -> Cuphead360.Engine              (C#: the native half of the Unity player)
  -> Cuphead360.Patcher redirect    (every InternalCall/P/Invoke body calls the engine)
  -> Cuphead360.Player              (host)
       JIT on Windows               the oracle: quick, debuggable
       NativeAOT-LLVM -> wasm32 -> w2c2 -> C -> XDK (now) / xSDK (later) XEX
```

Why:
- The interpreter still lacked generic types, exceptions, `finally`, GC,
  unsigned/unordered compares and much of the BCL, ran
  Root Pack at ~0.8 FPS on a PC, and every gap needed its own oracle. .NET
  supplies all of that as tested code; NativeAOT compiles it.
- Unity's own managed code (Vector3, Mathf, MonoBehaviour, UI, coroutine
  wrappers) runs unchanged, so its semantics are exact by construction. Only
  the engine boundary (what Unity implements natively) is reimplemented.
- Spire360 (Slay the Spire 2, a .NET game) runs whole games on hardware with
  this pipeline under both the XDK and xSDK, which settles the toolchain
  questions the interpreter path still had open.
- Distribution: everything built from the player's files is built on their
  PC. The repository holds only the patcher, engine, host and tools.

## Layout

| Path | What |
| --- | --- |
| `managed/Cuphead360.Patcher` | Cecil tool: `publicize`, `gen`, `redirect` (plus BCL call replacement), `dump` |
| `managed/Cuphead360.Engine/Impl` | the engine: object table, lifecycle, player loop, coroutines, scenes, assets, field decoder, icalls |
| `managed/Cuphead360.Engine/Generated` | `Icalls.g.cs`, `Publicized.g.cs` (generated from the player's DLLs; git-ignored) |
| `managed/Cuphead360.Player` | host: `census`, `selftest`, `run`, probes `resource`, `fields` |
| `managed/lib/{public,patched}` | the player's patched assemblies (git-ignored) |
| `tools/build_managed.py` | the pipeline, stage by stage |
| `tools/convert_objects.py` | Unity serialized files -> CHOB value trees (`GameData/objects`) |
| `tools/save_guard.py` | fingerprint/check of the installed saves |
| `tools/chob.py` | read CHOB files: class counts, object dumps, scene hierarchies |
| `tools/preview.py` | draw `run --capture` frames to PNG (Windows preview of engine render state) |

Build and check (Windows, .NET 10 SDK; wasm/c also need wasi-sdk 29 at
`C:\wasi-sdk-29.0-x86_64-windows` and Spire360's w2c2 build):

```
python tools/build_managed.py                       # patcher..selftest
python tools/build_managed.py --stage wasm,c        # console C
python -I tools/convert_objects.py --game game --out work/GameData [--bundles]
dotnet work/managed/jit/Cuphead360.Player.dll run --frames 600 --capture 60,300,600
python -I tools/preview.py --sheet                  # work/preview/frame_*.png, sheet.png
```

## Engine rules that matter

- **Object identity.** `m_CachedPtr` points at an 8-byte native block (slot,
  instance id); `GetInstanceID` reads it at the offset the engine reports.
  Destroy zeroes `m_CachedPtr`, which is what Unity's managed `== null` tests.
- **Lifecycle.** AddComponent/Instantiate/scene activation run Awake then
  OnEnable at once; Start runs before a behaviour's first Update; Destroy is
  deferred to the end of the frame (OnDisable, OnDestroy); deactivating a
  GameObject stops its coroutines. Fixed steps use TimeManager's 1/60.
- **Coroutines.** Run to the first yield inside StartCoroutine; nothing
  resumes in the frame it yielded in (except WaitForEndOfFrame and a
  WaitForFixedUpdate yielded inside FixedUpdate); nested coroutines resume
  their waiter in the frame they end.
- **Serialized fields.** No type trees in the GOG files, so script fields are
  decoded from raw bytes by walking the managed type: public or
  `[SerializeField]` (original visibility, before publicize), no delegates or
  `object`, 4-byte alignment after sub-4-byte values and after arrays/strings,
  depth limit 7, Unity structs by layout. Every object must consume its bytes
  exactly or it is reported (`DATA serialized fields of ...`).
- **Saves.** Game calls to `Environment.GetFolderPath` are patched to
  `Platform.GetFolderPath`, rooted at the host's save folder, which refuses to
  lie inside the PC's real special folders. PlayerPrefs is a port-local file,
  never the registry.
- **Missing engine methods** return default and are listed with hit counts at
  the end of a run (`Icall.Missing`); `Icall.Strict` makes them throw.
- **Engine-created objects.** Built-in classes the engine creates (a loaded
  Material, TextAsset, GameObject) get no constructor call, as in Unity;
  MonoBehaviours and ScriptableObjects run theirs.
- **Platform boundaries follow the original fallback paths.** GOG Galaxy's
  Init raises the glue's RUNTIME_ERROR (peer unavailable), so the original
  code logs "Failed to initialize GOG Galaxy" and its guarded calls stay off.
- **Exceptions** in behaviour messages and coroutines are reported with IL
  offsets per frame (the game DLLs have no PDBs) and the frame continues, as
  in Unity.
- **Built-in components** are engine records (`NCamera`, `NSpriteRenderer`,
  `NAnimator`, `NCanvas`, `NCanvasRenderer`, `NCanvasGroup`, `NRectTransform`)
  made by `Builtins.Create`; they load their converted fields (`Load`), copy
  state on Instantiate (`CopyFrom`) and react to activation.
- **Sprite atlases are late-bound, as in the shipped game.** Title and level
  sprites carry atlas tags and no texture. A sprite that is loaded or assigned
  queues its tag; the engine calls Unity's managed
  `SpriteAtlasManager.RequestAtlas`, the game's `SpriteAtlasLoader` loads the
  atlas bundle and answers through `SpriteAtlasManager.Register`, which binds
  every sprite whose render-data key the atlas holds. UVs come from the atlas
  entry's `uvTransform` and packing flips (horizontal, vertical, 180; 90 is
  unverified and reported as a data problem if met).
- **Animator** runs the controller's runtime constant: layers, states,
  exit-time and condition transitions (triggers consumed), any-state and
  selector (entry/exit) transitions, parameters, `Play`/`CrossFade` applied
  at the next animator update. Clips sample streamed (keyed cubic segments),
  dense and constant curves; bindings: sprite (PPtr curve), transform
  position/rotation/scale/euler, GameObject active, Behaviour enabled,
  SpriteRenderer color/flip, script float/int/bool fields; write defaults.
  Events go to every script on the GameObject with Unity's parameter rules.
  The pass runs after Update and coroutines, before LateUpdate (AnimatePhysics
  in the fixed step, UnscaledTime with unscaled delta). Transition
  interruptions and multi-clip blend trees aren't implemented (a blend tree
  with more than one node is reported as a data problem).
- **UI**: `Canvas.SendWillRenderCanvases` runs after LateUpdate so the game's
  UGUI and TextMesh Pro build their meshes; CanvasRenderers keep a copy of
  each mesh, materials, texture, color; CanvasGroup alpha is inherited down
  the hierarchy; screen-space root canvases are sized to the screen or camera
  each frame; RectTransform keeps anchoredPosition across parent resizes.
- **Render list**: cameras by depth; per camera, sprites and canvas meshes
  sorted by sorting layer, sorting order, then view depth (far first), canvas
  meshes in hierarchy order; overlay canvases last. `run --capture` writes it
  as JSON lines; the preview tool draws it. The game's own post-effect camera
  (`CupheadRenderer` -> `ChromaticAberrationFilmGrain`, `BlurGamma`,
  `OnRenderImage`) is not drawn yet.

## Verified (2026-11-01, Windows, this machine)

| Check | Result |
| --- | --- |
| `census`: every type loads and every method JIT-compiles on .NET 10 | 8,438 types, 60,077 methods, 0 failures (game, Unity, Rewired, Galaxy) |
| `selftest`: Unity lifecycle semantics through the patched assemblies | 23/23 pass |
| NativeAOT-LLVM wasm publish of the rooted game + Unity + engine | 26.4 MB core module |
| w2c2 translation | 40 C files, 186 MB; only `wasi:http` imports missing (unused networking) |
| `convert_objects.py` on all top-level serialized files | 161 files, 178,326 objects, 0 errors, 38 s |
| `run --frames 300` | scene_start (build 0) loads; original `CupheadStartScene.Awake` -> `Cuphead.Init` loads `Core/CupheadCore` via Resources.Load; 496 script objects decoded, 0 mismatches; `start_cr` proceeds to `AssetBundleLoader` and stops at the unimplemented AssetBundle API |
| Installed saves | `%APPDATA%\Cuphead` (3 files) unchanged by SHA-256 across the runs |

Not verified: anything rendered, audio, input, any scene after scene_title,
the C compiled by the XDK, anything on Xenia or hardware, performance.

Round 2 added asset bundles (420 converted, engine API), JsonUtility,
QualitySettings, Random, Font, legacy Input and the property bag (`Props`):
plain `get_X`/`set_X` accessors of Unity objects keep state seeded from the
converted data and are listed in a PROPS ledger, so state-only behaviour stays
visible. The boot now runs the full `Cuphead.Init` and stops at the GOG Galaxy
SDK boundary.

Round 4 added render state (cameras, sprite renderers, sprites, late-bound
sprite atlases, materials, shader property ids), the Animator, UI (Canvas,
CanvasRenderer, CanvasGroup, RectTransform, Mesh) and the render capture +
Windows preview. `run --frames 900` plays the original title sequence: 1 s
black, fader fade-in, the MDHR logo animation (100 frames), its fade-out, then
the dancing title with the flashing "Press Any Button" (TextMesh Pro), 0
exceptions, one missing engine method (`AudioSource.PlayOneShotHelper`).

Round 3 added the Galaxy boundary (original unavailable path), AudioMixer
state from the mixer constant, TextAsset, correct bundle LoadAllAssets, Shader
stubs and Unity's built-in resources in the converter. `run --frames 300`
goes scene_start -> scene_title through the original `LoadSceneAsync` with 0
exceptions, 0 data problems and 556 script objects decoded with 0 mismatches.

## Gaps, in the order the game meets them

1. Input: Rewired's native input layer (redirected P/Invokes) fed from XInput,
   plus scripted input for repeatable runs, so the title's "press any button"
   (PlayerManager join) and slot select work.
2. Audio playback from AudioSource + mixer state (the title already calls
   `PlayOneShotHelper`; every remaining PROPS entry is audio).
3. The opening flow (OPENING_FLOW.md): slot select, intro book, Elder Kettle,
   world map, Root Pack; Physics2D (queries, triggers), ParticleSystem,
   SpriteMask, render textures as they appear.
4. The game's post-effect camera (film grain, chromatic aberration, blur):
   the console renderer needs it; the preview doesn't draw it.
5. Animator gaps: transition interruptions, multi-node blend trees, root
   motion (none met on the title).
6. Console: the srt WASI runtime + translated C in an XDK project, a GPU
   backend for the render list; memory and frame-time measurement
   (CONSOLE_PLAN.md gates G-A/G-B).

## Toolchain transition (user requirement)

Work with the XDK build until genuine in-game status is verified on Xenia with
the XDK version (a hidden, muted, self-driven run, per the user), then move to
xSDK + vulkan4xenon to finish the port. Behaviour is otherwise checked with the
shared Windows build, which runs the same managed code. A boot screen or the
old native movement scaffold is not in-game status; Xenia evidence is not
hardware acceptance.
