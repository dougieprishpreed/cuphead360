# Handoff

Canonical recovery state. Update this file and
[NEXT_SESSION_PROMPT.md](NEXT_SESSION_PROMPT.md) together at the end of every
round. Evidence goes in [VERIFICATION.md](VERIFICATION.md) (newest first);
the architecture is [MANAGED_PLAYER.md](MANAGED_PLAYER.md); the console end
state and gates are [CONSOLE_PLAN.md](CONSOLE_PLAN.md).

## Workflow (since 2026-11-01)

One agent: Claude Opus 5.5 on the user's Max subscription, $0 API spend. No
subagents, workers or reviewers. The agent plans, implements, builds, tests
and documents, with full authority over code and plan. One heavy job at a
time (builds, conversions, ILC) so the user's PC stays usable;
`tools/build_managed.py` uses 2 jobs.

## Checkpoint

- Branch `main`, nothing pushed:
  - `5b4ef49` managed AOT player foundation; `9126b86` boot of scene_start;
    `446a2da` decision docs; `2a4d8bc` asset bundles, Galaxy boundary reached.
  - `bb73ff0` round 3: original code boots to scene_title with no exceptions.
  - Round 3 cleanup + docs commit ("Remove the retired interpreter tree ...").
- Side branch `interpreter-d1-r4-wip` (`ecbff5b`): the interrupted D1 r4
  interpreter edits, committed as found, not accepted, not continued.
- Uncommitted: nothing intended. `managed/lib`, `managed/**/Generated`,
  `work/`, `game/` are git-ignored derived data.
- Round 3 removed the interpreter-era tree (`src/`, `projects/`, `scripts/`,
  `tests/`, metadata/scene tools, interpreter docs, M1 evidence) with
  `git rm`; history keeps it. The untracked interpreter-era derived data
  (old extracts, logs, probe scripts, copied DLLs, `build/`; 9.5 GB) was moved,
  not deleted, to `..\cuphead360-interpreter-leftovers\` for the user to
  review and delete (deleting it from here was blocked as irreversible).

## Verified (Windows, JIT oracle)

Round 3 (`run13.log`): scene_start -> scene_title through the original
`LoadSceneAsync`, 300 frames, 0 exceptions, 0 data problems, 556 script
objects decoded with 0 mismatches; GOG Galaxy takes its original "Failed to
initialize" path; the game writes its default settings to port PlayerPrefs.
selftest 23/23. Saves unchanged (SHA-256, `tools/save_guard.py`). Details and
rounds 1-2: VERIFICATION.md.

Not verified: rendering, Animator, input, audio, any scene past the title,
XDK/xSDK compile of the translated C, Xenia, hardware, performance or memory.

## Next round (in order)

1. **Render state + Windows preview.** Implement the title's ledger: Camera
   rect/orthographicSize, SpriteRenderer (sprite, color, flip, sorting
   layer/order, material), Material properties and `Shader.PropertyToID`,
   Mesh, CanvasRenderer/RectTransform. Build an engine render snapshot and a
   Windows preview renderer that writes PNGs, converting the sprites' atlas
   pixels (texture2ddecoder for BC7) on demand. Compare a capture of the
   title with the user's PC game (ask the user for a screenshot if needed).
2. **Animator**: controllers, states, transitions, clips (sprite, transform,
   float curves), events; `GetCurrentAnimatorStateInfo`, `Play`,
   `StringToHash` (verify the hash).
3. **Input**: Rewired's redirected native input fed from XInput so the
   original title and slot select respond; scripted input for repeatable
   runs.
4. **Opening flow** on Windows (OPENING_FLOW.md), then audio playback.
5. **Console host (XDK)**: srt runtime + translated C; Xenia once, hidden,
   muted, self-driven, to verify genuine in-game status; then **switch to
   xSDK + vulkan4xenon** and test on hardware with the user.

## Rules that stay

- Original game logic and content; no harness shortcuts, forced states or
  scene carousels standing in for gameplay. Synthetic tests are labelled.
- Never read or write the installed game's saves or settings; port storage is
  under the host's save root (guarded) and port-local PlayerPrefs. Run
  `tools/save_guard.py check` around runs.
- Never commit game files, assemblies, decompiled code, SDK files or anything
  derived from them (`managed/lib`, `Generated/`, `work/`).
- Commit coherent checkpoints with honest messages; no pushes unless the user
  asks. Explicit allowlists when staging.
- Report exactly what was verified, on which build, and what was not.

## Environment

- .NET 10 SDK; Python 3 + UnityPy 1.25.4; wasi-sdk 29 at
  `C:\wasi-sdk-29.0-x86_64-windows`; w2c2 and the srt runtime from
  `..\spire360` (`out/logic/w2c2-build`, `runtime/`, `tools/w2c2_*.py`);
  NativeAOT-LLVM from the dotnet-experimental feed (`managed/wasm-nuget.config`).
- Game: the user's GOG Cuphead 1.3.2 (Unity 2017.4.9f1) in `game/`.
  Converted data: `work/GameData/objects`. Decompiled game code (private):
  `work/decomp/`; ILSpy at `..\spire360\out\tools\ilspycmd.exe`.
- XDK toolchain (VS2010 + Xbox 360 SDK) and Xenia Canary on this PC;
  xSDK/vulkan4xenon in the WSL distro CTR360-XSDK (shared with other
  projects: don't change its checkouts).
