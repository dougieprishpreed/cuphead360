# Managed player (current architecture, 2026-11-01)

This replaces the custom IL interpreter (`src/runtime`) as the way Cuphead's
code runs. The interpreter, its C++ Unity layer and the D1/CP1 work stay in
the tree as history; they are not extended. The decision and its evidence are
below; PORT_REVIEW.md Part A remains the record of why the interpreter path
was not viable as it stood.

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
  unsigned/unordered compares and much of the BCL (PORT_REVIEW A5/A11), ran
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
| `managed/Cuphead360.Player` | host: `census`, `selftest`, `run`, `fields` |
| `managed/lib/{public,patched}` | the player's patched assemblies (git-ignored) |
| `tools/build_managed.py` | the pipeline, stage by stage |
| `tools/convert_objects.py` | Unity serialized files -> CHOB value trees (`GameData/objects`) |

Build and check (Windows, .NET 10 SDK; wasm/c also need wasi-sdk 29 at
`C:\wasi-sdk-29.0-x86_64-windows` and Spire360's w2c2 build):

```
python tools/build_managed.py                       # patcher..selftest
python tools/build_managed.py --stage wasm,c        # console C
python -I tools/convert_objects.py --game game --out work/GameData
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

Not verified: anything rendered, audio, input, any scene after scene_start,
the C compiled by the XDK, anything on Xenia or hardware, performance.

## Gaps, in the order the boot meets them

1. AssetBundle API + converting bundle contents (`convert_objects.py --bundles`,
   4.9 GB of bundles; atlases, audio, Texture2D[] "screen_fx").
2. `JsonUtility` (settings/save data), Font, AudioSource/AudioClip/AudioMixer
   state, QualitySettings, `library/unity default resources`.
3. scene_title via the original `LoadSceneAsync(1)`; then slot select and the
   opening flow (OPENING_FLOW.md).
4. Rendering: SpriteRenderer/Camera/sorting snapshot -> Windows preview
   renderer (screenshots), then the console renderer (D3D9 under the XDK,
   Vulkan via vulkan4xenon under xSDK). Sprite meshes, atlases, materials
   (PORT_REVIEW A4).
5. Input: Rewired's native input calls (redirected) fed from XInput pads.
6. Animator, Physics2D (queries, triggers), ParticleSystem, UI/TextMesh, audio
   playback, achievements/stats at the Galaxy boundary (offline path).
7. Console: the srt WASI runtime + translated C in the XDK project; memory
   and frame-time measurement (gates G-A/G-B in PORT_REVIEW A7).

## Toolchain transition (user requirement)

Work with the XDK build until genuine in-game status is verified on Xenia with
the XDK version (a hidden, muted, self-driven run, per the user), then move to
xSDK + vulkan4xenon to finish the port. Behaviour is otherwise checked with the
shared Windows build, which runs the same managed code. A boot screen or the
old native movement scaffold is not in-game status; Xenia evidence is not
hardware acceptance.
