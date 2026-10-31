# Testing

## Principles

A build is not a test. Each result must state which of these was actually
done:

1. compiled;
2. linked;
3. executed on Windows;
4. compared against the original game or its IL;
5. executed in Xenia;
6. executed on Xbox 360 hardware.

## Automated Windows harness

`build\win32\Cuphead360.exe` supports:

```
--headless             no visible window; render offscreen
--frames N             run N fixed simulation steps then exit
--fixed-dt SECONDS     simulation step (default 1/60)
--seed N               deterministic RNG seed
--log PATH             structured log file
--screenshot PATH      write a screenshot at exit
--script PATH          scripted input sequence
--speed N              run faster than real time (headless)
--mute                 process audio but do not output it
```

The default test run is headless, fixed-step, muted, and writes a structured
log with scene loads, errors, frame times, and state snapshots.

## Test categories

- `tests/unit/`: core containers, endian readers, metadata parser, IL
  decoder, math.
- `tests/integration/`: load the real metadata package; run selected real
  methods and compare against a .NET reference harness.
- `tests/gameplay/`: scripted input against real scenes; assert positions,
  damage, transitions, and content loading.
- `tests/regression/`: golden logs/screenshots for known checkpoints.

## Reference oracle

The original `Assembly-CSharp.dll` runs under .NET on Windows for pure
managed methods that do not enter Unity native code. `tests/reference/`
contains a small C# harness built with VS2010's `csc.exe`; it loads the
user's assembly and prints deterministic results for selected methods. The
interpreter is compared against those results with explicit float tolerances.

For Unity-dependent behaviour, the original game is the oracle: scripted
input, repeated observations, and logged state. Comparisons must be
programmatic where possible.

## Xbox 360 / Xenia procedure

1. Build `default.xex`.
2. Prepare `GameData` with `tools\prepare_game.py`.
3. Deploy the folder `Cuphead360\` containing `default.xex` and `GameData\`.
4. Launch in Xenia Canary (or on hardware running unsigned XEX).
5. Expected initial behaviour and log paths are documented in
   `docs/HANDOFF.md` as milestones become runnable.
6. Report: build commit, Xenia/hardware version, exact steps, last log lines,
   screenshot/video, and whether the failure is a crash, hang, content-load
   error, or visual/gameplay defect.

Xenia success is not hardware validation, and compilation is not Xenia
success.
