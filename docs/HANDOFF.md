# Handoff

Canonical recovery state. Update this file and
[NEXT_SESSION_PROMPT.md](NEXT_SESSION_PROMPT.md) together at the end of every
round. Evidence goes in [VERIFICATION.md](VERIFICATION.md) (newest first);
the current architecture is [MANAGED_PLAYER.md](MANAGED_PLAYER.md).

## Workflow (since 2026-11-01)

One agent: Claude Opus 5.5 on the user's Max subscription, $0 API spend. No
subagents, workers, reviewers or the former host/lead/worker routing; the
history of that arrangement is in git and `work/restart-20261031/`. The agent
plans, implements, builds, tests and documents, with full authority over the
code and plan. Run one heavy job at a time (builds, conversions, ILC) so the
user's PC stays usable; `tools/build_managed.py` uses 2 jobs.

## Checkpoint

- Branch `main`. Round 1 of the single-agent workflow (2026-11-01):
  - `5b4ef49` managed AOT player foundation (patcher, engine, host, pipeline).
  - `9126b86` boot of original scene_start from converted data.
  - `446a2da` docs for the decision and the single-agent workflow.
  - Round 2: asset bundles, JsonUtility, property bag, Random/Font/Input
    (commit "Load asset bundles and reach the Galaxy boundary in the boot").
- Uncommitted: nothing intended. `managed/lib`, `managed/**/Generated`,
  `work/`, `game/`, `build/` are git-ignored derived data.
- The interrupted D1 r4 interpreter work was committed as found to branch
  `interpreter-d1-r4-wip` (`ecbff5b`): it compiles; `build_d1_fixture.bat`
  fails its T4 modifier-mutation assertion, so its dispatch test never ran.
  It is **not accepted** and is not being continued (see Decision).

## Decision taken this round

The custom IL interpreter is retired as the execution strategy. The game's
own assemblies now run as original IL on .NET 10, with Unity's 7,946 engine
methods (InternalCall + P/Invoke) redirected into a C# engine; NativeAOT-LLVM
-> wasm -> w2c2 -> C makes the console build, as the user's Spire360 does on
hardware. Reasons and rules: [MANAGED_PLAYER.md](MANAGED_PLAYER.md). The old
`src/` tree (interpreter, C++ Unity layer, PlayerSlice scaffold, XDK projects)
is kept as history and as a source of platform code for the console host; it
is not extended.

## Verified (Windows, JIT oracle unless noted)

Round 1:

- census: 8,438 types load, 60,077 methods JIT-compile, 0 failures.
- selftest: 23/23 Unity lifecycle semantics (Awake/OnEnable/Start order,
  FixedUpdate 50/s at 0.02, coroutines incl. nested/IEnumerator Start/Stop,
  SetActive, deferred Destroy + fake null, transforms, Instantiate rules,
  SendMessage, GetComponent<T> fast path).
- AOT: NativeAOT-LLVM publish of the whole rooted set -> 26.4 MB wasm core
  module; w2c2 -> 40 C files (186 MB). Not yet compiled by the XDK or run.
- Data: 161 serialized files -> 178,326 CHOB objects, 0 converter errors.
- Boot: `run --frames 300` loads scene_start; original CupheadStartScene.Awake
  -> Cuphead.Init loads Core/CupheadCore through Resources.Load; 496 script
  objects decode with 0 byte mismatches; start_cr reaches AssetBundleLoader and
  stops (AssetBundle API not implemented). 22 exceptions, all downstream of
  missing engine features (list in the run log, `work/managed/run3.log`).
- Saves: `%APPDATA%\Cuphead` (3 files) unchanged by SHA-256; the port wrote
  only `work/managed/save/LocalApplicationData/playerprefs.txt`.

Round 2 (same day):
- `convert_objects.py --bundles`: 420 bundles, 89,711 objects, 0 errors
  (508 MB of CHOB; pixels and audio samples omitted).
- Engine: AssetBundle API (LoadFromFile(Async), LoadAsset(Async), LoadAll,
  sub-assets, unload, requests), JsonUtility (Unity field rules, writer and
  parser), QualitySettings, Random (xorshift128, deterministic `--seed`,
  float mapping still to verify against an oracle), Font registration,
  legacy Input (no keyboard/mouse/touch), mixer asset class mapping.
- Patcher `gen` now routes 1,775 plain `get_X`/`set_X` accessors of Unity
  objects to `Props` (state seeded from converted data, kept when set,
  listed in a PROPS ledger) instead of returning default.
- `run --frames 600` (`work/managed/run5.log`): bundle loads succeed; the full
  `Cuphead.Init` runs and stops in `OnlineInterfaceGog.Init` at the GOG
  Galaxy SDK P/Invokes; `AudioManager.masterVolume` needs AudioMixer.GetFloat;
  Rewired's own initialization throws (InputManager_Base.Initialize ->
  ReInput setup); `Vibrator.Update` throws every frame as a consequence.
  Still scene_start only. selftest 23/23; saves unchanged (SHA-256).

Not verified: rendering, audio, input, any scene after scene_start, XDK
compile of the translated C, Xenia, hardware, performance or memory.

## Next round (in order)

1. **GOG Galaxy boundary.** Read OnlineInterfaceGog/GalaxyCSharp to find the
   original "Galaxy unavailable" path (Init failure, exceptions, listener
   callbacks) and implement only the SDK P/Invokes so the original code takes
   it: local stats/achievements bookkeeping must still run (PORT_REVIEW A2).
   No blanket success.
2. **Boot to scene_title** with the original `LoadSceneAsync(1)`, fixing in
   ledger order: AudioMixer (GetFloat/SetFloat state from the converted
   mixer), Rewired initialization (find what its InputManager data or
   platform layer needs; Rewired_Core P/Invokes are redirected), convert
   `Resources/unity default resources` (missing objects 140/146 and
   PPtr(2,...) in resources.assets point there). Keep every missing call
   visible.
3. **Windows preview renderer** from an engine render snapshot (sprites,
   cameras, sorting) with PNG screenshots, so the title can be seen; then
   Rewired input from XInput.
4. **Opening flow** on Windows (OPENING_FLOW.md), driven by original code.
5. **Console host (XDK)**: srt runtime + translated C in an XDK project,
   D3D9 renderer; Xenia once, hidden, muted, self-driven, to verify genuine
   in-game status; then **switch to xSDK + vulkan4xenon** (required by the
   user) and test on hardware with the user.

## Rules that stay

- Original game logic and content; no harness shortcuts, forced states or
  scene carousels standing in for gameplay. Synthetic tests are labelled.
- Never read or write the installed game's saves or settings; port storage is
  under the host's save root (guarded) and port-local PlayerPrefs.
- Never commit game files, assemblies, decompiled code, SDK files or
  anything derived from them (`managed/lib`, `Generated/`, `work/`, `build/`).
- Commit coherent checkpoints with honest messages; no pushes unless the user
  asks. Explicit allowlists when staging.
- Report exactly what was verified, on which build, and what was not.

## Environment

- .NET 10 SDK; wasi-sdk 29 at `C:\wasi-sdk-29.0-x86_64-windows`; w2c2 and
  the srt runtime from `..\spire360` (`out/logic/w2c2-build`, `runtime/`,
  `tools/w2c2_*.py`); NativeAOT-LLVM packages from the dotnet-experimental
  feed (`managed/wasm-nuget.config`).
- Game: the user's GOG Cuphead 1.3.2 (Unity 2017.4.9f1) in `game/`.
  Converted data: `work/GameData/objects`.
- XDK toolchain (VS2010 + Xbox 360 SDK) and Xenia Canary as listed in
  BUILDING.md; xSDK/vulkan4xenon in the WSL distro CTR360-XSDK (shared with
  other projects: don't change its checkouts).
- ILSpy: `..\spire360\out\tools\ilspycmd.exe`; decompiled game code in
  `work/decomp/` (private).
