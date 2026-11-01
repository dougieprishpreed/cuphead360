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

Build and check (Windows, .NET 10 SDK; wasm/c also need wasi-sdk 29 at
`C:\wasi-sdk-29.0-x86_64-windows` and Spire360's w2c2 build):

```
python tools/build_managed.py                       # patcher..selftest
python tools/build_managed.py --stage wasm,c        # console C
python -I tools/convert_objects.py --game game --out work/GameData [--bundles]
dotnet work/managed/jit/Cuphead360.Player.dll run --frames 300
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

Round 3 added the Galaxy boundary (original unavailable path), AudioMixer
state from the mixer constant, TextAsset, correct bundle LoadAllAssets, Shader
stubs and Unity's built-in resources in the converter. `run --frames 300`
goes scene_start -> scene_title through the original `LoadSceneAsync` with 0
exceptions, 0 data problems and 556 script objects decoded with 0 mismatches.

## Gaps, in the order the title meets them

1. Rendering state: Camera rect/size, SpriteRenderer (sprite, color, flip,
   sorting), Material properties and `Shader.PropertyToID`, Mesh,
   CanvasRenderer/RectTransform (UI), so a Windows preview renderer can draw
   the title from an engine snapshot (PNG captures).
2. Animator: controllers, states, clips (sprite/transform/property curves),
   events, `GetCurrentAnimatorStateInfo`, `Play`, `StringToHash`.
3. Input: Rewired's native input layer (redirected P/Invokes) fed from XInput,
   so the original title "press any button" and slot select work.
4. Audio playback from AudioSource + mixer state; then the opening flow
   (OPENING_FLOW.md): slot select, intro, Elder Kettle, world map, Root Pack.
5. Physics2D (queries, triggers), ParticleSystem, UI/TextMesh Pro layout.
6. Console: the srt WASI runtime + translated C in an XDK project; memory and
   frame-time measurement (CONSOLE_PLAN.md gates G-A/G-B).

## Toolchain transition (user requirement)

Work with the XDK build until genuine in-game status is verified on Xenia with
the XDK version (a hidden, muted, self-driven run, per the user), then move to
xSDK + vulkan4xenon to finish the port. Behaviour is otherwise checked with the
shared Windows build, which runs the same managed code. A boot screen or the
old native movement scaffold is not in-game status; Xenia evidence is not
hardware acceptance.
