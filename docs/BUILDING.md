# Build

## Prerequisites

- Windows with Visual Studio 2010 (the XDK integration requires the full
  edition; Express cannot load the Xbox 360 platform).
- Microsoft Xbox 360 XDK installed. On this machine:
  - `XEDK=C:\Program Files (x86)\Microsoft Xbox 360 SDK`
  - PowerPC compiler: `%XEDK%\bin\win32\cl.exe` (`_MSC_VER=1600`)
  - `MSBuild`: `C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe`
- Python 3 for the metadata/asset tools.
- The user's GOG Cuphead installation (default `game\`).

The XDK environment is set by `%XEDK%\bin\win32\xdkvars.bat`. Do **not** run
`vsvars32.bat` in the same prompt: it puts the x86 compiler ahead of the XDK
PowerPC compiler.

## Windows development build

```
scripts\build_win32.bat
```

Output: `build\win32\Cuphead360.exe` (plus `.pdb`).

For a Visual Studio build, open `projects\win32\Cuphead360.sln`, select
`Release|Win32`, and build.

The Windows build is a first-class target: it uses the same `src/core`,
`src/runtime`, `src/engine`, and `src/game` sources as the Xbox 360 build.
Only `src/platform/win32` differs.

## Xbox 360 build

```
scripts\build_xbox360.bat
```

This sets `XEDK`, `INCLUDE`, `LIB`, and `PATH` locally in the batch process,
then invokes MSBuild on `projects\xbox360\Cuphead360.vcxproj` with
`Configuration=Release` and `Platform=Xbox 360`.

Output: `build\xbox360\default.xex`.

The XEX is not validated by compiling. See `docs/TESTING.md` for Xenia and
hardware procedures.

## Data preparation

```
python tools\prepare_game.py --input game --output build\xbox360\GameData
```

The preparation tool verifies the expected build, writes a manifest, and
converts only the data the runtime needs. It never modifies `game/`.

## Reproducibility

- Source builds should be reproducible from a clean checkout with the same
  XDK/VS2010 toolchain.
- Generated data is deterministic for a given source installation; timestamps
  are not embedded in content files.
- Local game data, decompiled output, and generated assets are gitignored.
