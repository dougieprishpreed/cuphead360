# Next session prompt

Continue the Cuphead360 project at `F:\downloads\temp\python\hermes\cuphead360`.

You are Claude Opus 5.5, the only agent, on the existing Max subscription with
$0 API spend. Do not spawn subagents or other models.

Read `docs/HANDOFF.md` first, then `docs/MANAGED_PLAYER.md`, then check the
actual `git status` / `git log` against the checkpoint HANDOFF records.
`docs/CONSOLE_PLAN.md` holds the console end state, budgets and gates;
`docs/OPENING_FLOW.md` defines the first playable milestone.

The project now runs the user's own Cuphead assemblies as original IL on .NET
with Unity's engine methods redirected into `managed/Cuphead360.Engine`, and
builds the console version through NativeAOT-LLVM -> wasm -> w2c2 -> C (the
pipeline the user's Spire360 ships on hardware). You have full authority to
reconsider or rewrite any part of the code, architecture, converter, formats,
rendering, build, tests, docs and plan; existing work is evidence, not a
binding design.

Objective: a genuinely playable alpha release candidate on real Xbox 360
hardware, with gameplay and presentation virtually identical to the user's GOG
version, plus a practical converter for the user's own game files.

The full normal gameplay loop (new game through Root Pack and back to the
map) runs on Windows with sound, headless and in the live window (`play`),
and as translated C (the console build's code) headless on PC. Five
hardware runs so far (`work/console/hw01`-`hw05`): the title and the house
run on the console, slowly; the map froze while loading its textures
(fixed in round 11: one staging ring, a texture budget and memory floor);
long values were formatted with their low word's sign, because the title
runs in 32-bit mode and xSDK's GCC uses record forms for some 64-bit
compares (fixed by `console/xsdk/record_forms.py`, the build's compiler
launcher). The sixth run confirmed the sign fix (I64CHECK ok) and much faster loads,
then crashed at the map on a texture-budget bug (fixed in round 11b).
The seventh run reached the map and held memory, at 2-3 fps (audio decode
and the draw list; round 12 works on both and re-encodes the textures).
The eighth run beat the Root Pack (~16 fps, hitches everywhere; the map
~7 fps); round 13 removed the staging-copy hitches, wrote sprites
natively, clipped UGUI masks, added 3D sound. The ninth run: fights
nearly smooth, the map ~112 ms a frame, the Ooze hung after phase 1 and
drew black rays; round 14 fixed exit time 0 in the animator (the hang and
truncated animations everywhere), rewrote the host's frame path in the
GPU's vertex layout (fewer load-hit-store stalls), translated the light
ray shader and streams clips over 1.5 s. The tenth run: rays and phases
right, the map ~88 ms a frame, the potato still blurry (its sprites drawn
at 2.7 screen pixels a stored texel). Round 15 draws every atlas sprite at
full size from per-animation pages (`tools/cluster_console.py`, the glue's
remap), puts textures and clips in packs opened once (`ch360_pack.c`; each
USB open cost ~110 ms), and cut the map's CPU (type names asked for every
coroutine step, invisible UI drawn; PC map frame 4.0 -> 1.7 ms), with a TOP
line naming the costliest scripts. The eleventh run: the map ~36.5 ms a
frame, the Root Pack 22-25 ms, loads ~10x faster, but textures past 2 GB in
the pack drew black (xsdk's 32-bit file offsets); round 15b splits the
packs under 2 GB. The twelfth run: textures right, fights near full speed
with hitches (texture reloads under the memory floor, gen0 GCs), the first
run-and-gun level ~187 ms a frame; round 16 puts each animation on pages of
its own, cuts the physics queries' allocations (GCs 3.6x fewer there on
PC), grids the glue's sprite lookup and frees 12 MB. The Spire360 session
shares the CTR360-XSDK distro, its xsdk/vulkan4xenon checkouts and the USB
drive (stage only E:\Apps\cuphead360). Round 17 (with round 16, not yet
on hardware) indexes the converted object files (no walk over their values
at load), builds scenes a slice a frame so the loading screen animates, and
logs a MEMORY line after each load. Continue with HANDOFF.md's "Next round"
item 0: the thirteenth run's SCENE and MEMORY lines, Forest Follies and the
fights, the user's look at loads and hitches, then the memory growth, the roadmap to 30 fps (what TOP names, the native
mixer, the UI walk and rebuilds, GC, frame pacing), preloading during async
scene loads, and the other custom sprite shaders. The user's later goal:
replace the bundled game code with our own so the XEX can be shared.
Rebuild
with `python tools/build_managed.py --stage jit` after engine changes; check
with `selftest`, the bot loop (`run --frames 15301 --save
work/managed/save_bot --bot tests/input/bot_veggies.txt`, deleting the save
dir first), `--capture` + `tools/preview.py`, `--inspect <GameObject>
--dump-at N`, and `play ... --unlocked 1 --shot N`; run
`tools/save_guard.py check` around runs, and keep the missing-engine-method,
property-bag and data-problem ledgers visible rather than hiding gaps.

Verify behaviour with the shared Windows build; Xenia runs are generally not
needed. Required transition: once genuine in-game status is verified on Xenia
with the XDK build (launch Xenia hidden, audio disabled, and drive the input
yourself), immediately move to xSDK and vulkan4xenon to finish the port;
xSDK builds don't run on Xenia and need console tests by the user. Record
exactly what was verified. A compiled XEX, a boot screen or the old native
movement scaffold is not in-game behaviour, and Xenia evidence is not
hardware acceptance.

Protect the installed saves (`%APPDATA%\Cuphead`; fingerprint before and after
runs), the original game files and source history. Never commit proprietary
game assets, assemblies, decompiled code, SDK files or generated bundles
(`managed/lib`, `managed/**/Generated`, `work/`, `build/`, `game/`). Don't
push unless the user asks.

Work autonomously toward the console alpha candidate. At the end of every
round, update HANDOFF.md and this NEXT_SESSION_PROMPT.md together, with an
honest checkpoint and verification record (VERIFICATION.md, newest first).
