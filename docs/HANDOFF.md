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

1. **User play reports first.** The user plays `play` (live window, pad)
   and reports defects; fix each at its root cause, then sweep for the same
   cause elsewhere (this round: anchoredPosition, cloned native state, clip
   planes, masks).
2. **Audio playback**: AudioSource/AudioClip/mixer state, `PlayOneShotHelper`,
   `Play`/`Stop`/`UnPause`, `AudioListener.pause`; clip sample conversion
   (Vorbis in the .resS files and bundles); a Windows audio backend
   (XAudio2 or WASAPI) in the live host.
3. **Render textures and the post-effect camera** (GlowBossTitle, film
   grain, chromatic aberration), `Texture2D.SetPixel/Apply`, scene-load
   hitches (texture reads: stream or preload).
4. **More of the game**: other bosses, run-and-gun levels, the shop, saves
   across sessions; Animator interruptions and blend trees.
5. **Console host (XDK)**: srt runtime + translated C, a GPU backend for the
   render list (the GL host is the reference); Xenia once, hidden, muted,
   self-driven, to verify genuine in-game status; then **switch to xSDK +
   vulkan4xenon** and test on hardware with the user.

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
