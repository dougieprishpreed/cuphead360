# Verification

Newest first. Each entry says what was run, on which build, and what it
showed. Windows entries use the JIT oracle (`work/managed/jit`).

## 2026-11-01 managed player, round 3

Source: `bb73ff0` (engine) plus the round-3 docs/cleanup commit.

| Command | Result |
| --- | --- |
| `python -I tools/convert_objects.py --game game --out work/GameData` (Shader stubs, TextAsset bytes, built-in resources) | 161 files, 178,391 objects, 0 errors, 39.6 s; built-in resources 2 files, 110 objects |
| `... --files none --bundles` | 420 files, 89,771 objects, 0 errors, 73.1 s |
| `python tools/build_managed.py --stage gen,engine,redirect,jit,selftest` | gen: 419 implemented, 1,773 property-bag accessors, 5,754 stubs; `SELFTEST PASS: 23 passed, 0 failed` |
| `run --frames 300` (`work/managed/run13.log`) | scene_start -> scene_title (36 GameObjects, 92 components) through the original `LoadSceneAsync`; `UNITY Error: Failed to initialize GOG Galaxy: ... RuntimeError: Galaxy peer is not available on this platform` (original unavailable path); game wrote its default settings JSON to port PlayerPrefs; 0 exceptions, 0 data problems, 556 script objects decoded, 0 mismatches; 17 missing engine methods (render state, Animator, PlayOneShot, vSyncCount) |
| `tools/save_guard.py check` before/after the runs | `OK, 3 files unchanged (SHA-256)` |

Notes: run8 showed the game's own behaviour with a stale port-local
PlayerPrefs file written by a pre-fix round-2 run (BOM, empty settings value):
`SettingsData.Data` stays null, as it would in Unity. The file was the port's
own (`work/managed/save`), deleted, and regenerated correctly. Not verified:
rendering, Animator, input, audio, anything past the title, any console build.

## 2026-11-01 managed player, round 2

| Command | Result |
| --- | --- |
| `python -I tools/convert_objects.py --game game --out work/GameData --files none --bundles` | 420 files, 89,711 objects, 0 errors, 71.3 s |
| `python tools/build_managed.py` stages through selftest | gen: 387 implemented, 1,775 property-bag accessors, 5,784 stubs; `SELFTEST PASS: 23 passed, 0 failed` |
| `Cuphead360.Player run --frames 600 --seed 360` (`work/managed/run5.log`) | asset bundles load; full `Cuphead.Init` reaches `OnlineInterfaceGog.Init` and throws at the unimplemented Galaxy SDK P/Invokes; `Cuphead.Awake` throws in `AudioManager.masterVolume` (AudioMixer.GetFloat); Rewired initialization throws; `Vibrator.Update` throws each frame (600); still scene_start |
| `%APPDATA%\Cuphead` SHA-256 before/after | unchanged (3 files) |

## 2026-11-01 managed player, round 1 (single-agent)

Architecture: [MANAGED_PLAYER.md](MANAGED_PLAYER.md). Source: `5b4ef49`,
`9126b86` and the docs commit that follows them. Windows, .NET 10.0.401 JIT
unless noted; one run each.

| Command | Result |
| --- | --- |
| `python tools/build_managed.py` (patcher, publicize, gen, engine, redirect, jit, census, selftest) | `BUILD_MANAGED OK`; publicize 960 types / 5,986 fields / 7,940 methods; gen 7,946 engine methods (6,113 icall, 1,833 P/Invoke); redirect 7,946 patched, 0 left native, 2 `GetFolderPath` calls redirected |
| `Cuphead360.Player census` | 8,438 types (0 failed), 60,077 methods compiled (0 failed) |
| `Cuphead360.Player selftest` | `SELFTEST PASS: 23 passed, 0 failed` |
| `dotnet publish -r wasi-wasm -p:CupheadWasm=true` (ILCompiler.LLVM 10.0.0-rc.1.26357.1, wasi-sdk 29) | exit 0; `Cuphead360.Player.wasm` 26,400,236 bytes, core module (`0061736d01000000`) |
| w2c2 `-f 2000` + Spire360 `w2c2_fix_tables.py` / `w2c2_imports.py` | 40 C files, 186 MB; 64 imports, 30 implemented by srt, 34 missing (all `wasi:http`/`wasi:io` streams) |
| `python -I tools/convert_objects.py --game game --out work/GameData` | 161 files, 178,326 objects, 0 errors, 38.4 s |
| `Cuphead360.Player run --frames 300` (`work/managed/run3.log`) | scene_start loaded (2 GameObjects, 7 components); original `CupheadStartScene.Awake` -> `Cuphead.Init` instantiates `Core/CupheadCore`; fields decoded 496, mismatched 0; 22 exceptions and 9 missing engine methods, all at unimplemented AssetBundle/audio/font/JsonUtility calls; no scene after scene_start |
| `%APPDATA%\Cuphead` SHA-256 before/after all runs | unchanged (3 files) |

Not covered: rendering, audio, input, later scenes, the XDK compile or any run
of the translated C, Xenia, hardware, performance, memory.

Interpreter-era note: the interrupted D1 r4 source was committed unvalidated
to branch `interpreter-d1-r4-wip`; its fixture build fails the T4 mutation
assertion. The interpreter is retired; the entries below are history.

## Earlier records

The interpreter-era records (CP1, D1, M1 combat probes, boss scene boots,
the native movement slice) and their evidence were removed from the tree in
round 3 with the code they tested. They are in git history: `git show
bb73ff0:docs/VERIFICATION.md` (and `docs/evidence/` at the same commit).
They do not validate anything in the managed player.
