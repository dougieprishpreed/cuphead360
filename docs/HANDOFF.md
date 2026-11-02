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
  - `98b0439` round 3 cleanup + docs ("Remove the retired interpreter tree ...").
  - `cdb14c0` round 4 ("Render state, Animator, UI and a Windows preview of
    the title").
  - `8f3201d` round 5: input (Unity Input fed from XInput/scripts, Rewired
    on its Unity fallback), Physics2D, dynamic TTF/CFF fonts, the bot.
  - `5c2b512` round 5: live Windows window (`play`), texture export,
    collision response, RectTransform/clone/text/mask/clip fixes.
  - `7d766c8` round 5 docs.
  - `acd078c` round 6: audio, the 2.2 GB -> 255 MB heap cut, the xSDK
    console host. Round 6 commit after it: NativeAOT fixes from the
    translated build, console GC/texture budgets, live window on console
    textures, docs.
- Side branch `interpreter-d1-r4-wip` (`ecbff5b`): the interrupted D1 r4
  interpreter edits, committed as found, not accepted, not continued.
- Uncommitted: nothing intended. `managed/lib`, `managed/**/Generated`,
  `work/`, `game/` are git-ignored derived data.
- Round 3 removed the interpreter-era tree (`src/`, `projects/`, `scripts/`,
  `tests/`, metadata/scene tools, interpreter docs, M1 evidence) with
  `git rm`; history keeps it. The untracked interpreter-era derived data
  (old extracts, logs, probe scripts, copied DLLs, `build/`; 9.5 GB) was moved
  to `..\cuphead360-interpreter-leftovers\`; its contents were deleted at the
  user's request in round 6 (the empty folder is the user's to remove).

## Console build (round 6)

`E:\Apps\cuphead360` on the user's USB holds the first console build for a
hardware test: `default.xex` (xSDK + vulkan4xenon, 99.5 MB) and `GameData`
(objects, ctex, audio, fonts), `save\`, `licenses\`. The layout, build and
pipeline are in `console/README.md`. The translated C passed the full
opening loop headless on PC with 0 exceptions; nothing has run on the
console yet. When the user reports, read `E:\Apps\cuphead360\cuphead360.txt`
(copy it into work/ first) for start-up memory, per-600-frame timing,
textures, audio underruns and the player's own exceptions.

## Verified (Windows, JIT oracle)

Round 5 (`run63.log`, `run65.log`, `live4.log`): the full normal loop runs
from the original code under scripted pad input, headless and in the live
window: title, slot select, new game, intro, Elder Kettle's house, world map
(walking the real paths), Root Pack on Simple, won, results screen, back on
the map, King Dice's tooltip. 0 exceptions, 0 data problems; the remaining
gaps are audio, render textures and `Texture2D.SetPixel`. The user's two
reports (walking through map scenery; misplaced bubble text) are fixed at
their causes (collision response; RectTransform anchoredPosition, cloned
RectOffset padding, line spacing), and the same work fixed HUD placement, a
stale duplicate bubble and UGUI masks. selftest 23/23; saves unchanged.
Details and earlier rounds: VERIFICATION.md.

Not verified: audio, the post-effect camera and render textures (not drawn),
a comparison with the user's PC game, a full hand-played run (the user plays
`play`; their reports drive fixes), XDK/xSDK compile of the translated C,
Xenia, hardware, memory.

## Next round (in order)

1. **The first hardware test.** The user runs `E:\Apps\cuphead360\default.xex`.
   Expect memory to be the first limit (XEX ~100 MB + linear memory up to
   ~315 MB + textures up to 112 MB of 512 MB): the log's per-600-frame lines
   say how much is free. A silent freeze means a CPU fault (xSDK has no
   fault handler): trace with write+fsync to the drive around the last log
   lines. Fix from the log, rebuild (`build_managed.py --stage wasm,c`,
   `build_console.py --stage native` to check, then `--stage xsdk`) and
   restage (`stage_console.py --to E:\Apps\cuphead360` copies only what
   changed).
2. **Console gaps known now**: stencil masks (UGUI Mask children draw
   unclipped), render textures and the post-effect camera (also missing on
   Windows), per-object reads of large converted files (many small USB
   reads at scene loads), the managed heap (~260 MB in a level: its biggest
   holders are not yet measured).
3. **User play reports on Windows** (`play`): root causes, then a sweep.
4. **More of the game** beyond the opening loop.

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
