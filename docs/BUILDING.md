# Build

## Prerequisites

- Windows, .NET 10 SDK, Python 3 with UnityPy 1.25 (`pip install UnityPy`).
- The player's GOG Cuphead installation (default `game\`).
- Console C (stages `wasm`, `c`): wasi-sdk 29 at
  `C:\wasi-sdk-29.0-x86_64-windows` (or `WASI_SDK_PATH`), the
  dotnet-experimental NuGet feed (`managed/wasm-nuget.config`), and a built
  w2c2 (Spire360's `out/logic/w2c2-build` next to this repository, or
  `--w2c2`).
- Console host (not written yet): the XDK (VS2010 + Xbox 360 SDK) until the
  Xenia check, then xSDK + vulkan4xenon in the WSL distro `CTR360-XSDK`
  (shared with other projects; don't change its checkouts).

## Managed player

```
python tools/build_managed.py                  # patcher .. selftest
python tools/build_managed.py --stage wasm,c   # console C
```

Stages run in order and each can be run alone (`--stage gen,engine,redirect,jit`
after changing `Impl/`). See the docstring of `tools/build_managed.py`.
Outputs: `managed/lib/{public,patched}` (patched game DLLs),
`managed/Cuphead360.Engine/Generated/` (stubs), `work/managed/{patcher,engine,jit,wasm,c}`.
All of it is derived from the player's game and git-ignored.

## Game data

```
python -I tools/convert_objects.py --game game --out work/GameData            # serialized files + built-in resources
python -I tools/convert_objects.py --game game --out work/GameData --files none --bundles
```

Writes `work/GameData/objects/*.chob` (format in the script's docstring).
Reads the game folder only. Pixels and audio samples are not converted yet.

## Run and check

```
python tools/save_guard.py check                                  # installed saves untouched (before and after)
dotnet work/managed/jit/Cuphead360.Player.dll selftest
dotnet work/managed/jit/Cuphead360.Player.dll run --frames 300 [--seed 360]
dotnet work/managed/jit/Cuphead360.Player.dll resource "<path>" [Type]
dotnet work/managed/jit/Cuphead360.Player.dll fields <file> <pathId>
```

`run` writes the port's saves and PlayerPrefs under `work/managed/save`
(guarded so it can't resolve into the PC's real AppData/Documents). Each run
ends with the missing-engine-method, property-bag and data-problem ledgers.
Exception reports give IL offsets; match them with
`dotnet work/managed/patcher/Cuphead360.Patcher.dll dump --in managed/lib/public/<dll> --type <T> --method <M>`.
For full stacks without inlining, set `DOTNET_JitNoInline=1` and
`DOTNET_TieredCompilation=0`.
