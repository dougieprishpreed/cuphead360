# Testing

## Principles

A build is not a test. Each result states which of these was actually done:

1. compiled;
2. executed on Windows (JIT oracle, the shared managed code);
3. compared against the original game (captures, traces from the user's PC game);
4. executed in Xenia (XDK build only; functional, never timing);
5. executed on Xbox 360 hardware (xSDK build; the user runs it).

Synthetic stimulus (self-tests, probes) is labelled as such and never counts
as gameplay.

## Windows checks

- `census`: every type loads and every method JIT-compiles.
- `selftest`: Unity lifecycle semantics through the patched assemblies
  (`managed/Cuphead360.Player/SelfTest.cs`).
- `run --frames N`: the original boot from `scene_start`, deterministic with
  `--seed`. A run is judged by its ledgers: exceptions (with IL offsets),
  missing engine methods, property-bag accessors (state only), data problems
  and field-decode mismatches. Keep them visible; never silence a gap.
- `tools/save_guard.py check` before and after any run.

- `run --capture ...` + `tools/preview.py`: PNGs of the engine's render list
  (what the port would draw). Compare against captures of the user's PC game
  at the same moments; the preview leaves out the game's post-effect camera.
- The loop check: `run --bot tests/input/bot_veggies.txt` with a fresh
  `--save` dir plays new game -> house -> map -> Root Pack (won) -> results
  -> map. The bot presses the buttons a player would (scripted pad input
  through Rewired); it drives the game, it doesn't force states.
- `play`: the live window; the user's hand-played reports are the
  acceptance signal on Windows.

Planned (CONSOLE_PLAN.md V1): render fixtures per material recipe, and a
test-only trace mod for the user's PC game recording per-frame state under
recorded input, diffed against port traces.

## Xenia (XDK build)

Generally not needed: the Windows build runs the same managed code and
answers behaviour questions faster. The one required Xenia run verifies
genuine in-game status of the XDK build before switching to xSDK +
vulkan4xenon. Launch Xenia hidden, with audio disabled, and drive the input
from the harness. xSDK builds don't run on Xenia.

## Hardware (xSDK build)

The user runs builds on the console and sends logs/captures. Report: build
commit, steps, last log lines, capture, and whether a failure is a crash,
hang, content error or visual/gameplay defect.

Xenia success is not hardware validation, and compilation is not Xenia
success.
