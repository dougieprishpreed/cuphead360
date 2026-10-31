# Handoff

## Environment facts

- Project root: `F:\downloads\temp\python\hermes\cuphead360`
- Game install: `F:\downloads\temp\python\hermes\cuphead360\game` (gitignored)
- XDK: `C:\Program Files (x86)\Microsoft Xbox 360 SDK` (`XEDK` env var set)
- XDK compiler: `%XEDK%\bin\win32\cl.exe`, `_MSC_VER=1600`
- VS2010: `C:\Program Files (x86)\Microsoft Visual Studio 10.0`
- MSBuild: `C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe`
- ILSpy CLI: `F:\downloads\temp\python\hermes\spire360\out\tools\ilspycmd.exe`
- Working analysis/decompile output: `work/` (gitignored)
- Remote: `https://github.com/dougieprishpreed/cuphead360` (private, initially
  empty; Git credential manager authenticated)

## Verified source build

- Cuphead GOG bundleVersion 1.3.2, GOG build `55543280706943035`
- Unity 2017.4.9f1, Mono backend
- 79 scenes including all DLC scenes
- `Assembly-CSharp.dll`: 5,640 TypeDefs, 34,747 MethodDefs, 34,266 methods
  with IL, 2,590,233 IL bytes, no generic definitions
- StreamingAssets bundles: 5.22 GB, 87,659 sprites, 1,108 textures, 132
  AudioClips

## Toolchain verified

Spire360's `xdk\hello` project was rebuilt locally and its XEX/report were
examined. The report confirms big-endian PowerPC, `_MSC_VER=1600`, working
D3D9, XInput, VirtualAlloc, and `game:\` file writes. This environment can
produce XEXs.

## Current code state

See `docs/PROGRESS.md`. At the time of writing, the repository contains the
investigation/architecture documentation and is being brought up to the
first shared-core dual-target build.

## Immediate next steps

1. Build `src/core` + `src/app` for Win32 and Xbox 360.
2. Add `tools/build_metadata.py`; generate a compact metadata package from the
   user's `Assembly-CSharp.dll`.
3. Implement `src/runtime/metadata.*` and verify on both targets.
4. Implement the interpreter subset needed for a real game method; compare
   with a .NET reference harness.
5. Implement sprite/atlas extraction and a headless Win32 render test.

## Important constraints

- Never commit `game/`, `work/`, extracted assets, XDK files, or game code.
- Both targets must compile from the same shared sources.
- All serialized reads must be explicitly little-endian.
- Do not claim Xenia/hardware validation without an actual run.
- The private repository is the only remote; verify before pushing.
