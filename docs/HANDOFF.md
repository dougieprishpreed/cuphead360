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
  - `39bbd48` round 7: a 64-bit compare rewrite (removed in round 8: the
    compares were right), the heartbeat log, I64CHECK.
  - Round 8 commit after it: 64-bit memory accesses as two 32-bit ones
    (`console/w2c2/w2c2_base.h`), the memory probe, live log drain, file
    I/O stats, slow-frame/CHOB/SCENE timing; staged for the third run.
  - `63678ba` round 9: parsed-tree budget, byte lists as bytes, the
    hazard probe; `dc88a62` round 10: frame rate (allocations, GC,
    physics), texture memory retry, IdText names, the per-thread probe.
  - Round 11 commit after it: record_forms.py (the sign bug's fix), the
    staging ring and texture budget/floor, CHOB font holes and block
    cache; staged for the sixth run.
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

## Console build (rounds 6-8)

`E:\Apps\cuphead360` on the user's USB holds the console build:
`default.xex` (xSDK + vulkan4xenon) and `GameData` (objects, ctex, audio,
fonts), `save\`, `licenses\`. The layout, build and pipeline are in
`console/README.md`. When the user reports, copy
`E:\Apps\cuphead360\cuphead360.txt`, `default.log` and `save\` into
`work/console/hwNN/` first.

Two hardware runs so far (`work/console/hw01/`, `hw02/`; VERIFICATION
rounds 7-8), both a black screen the user switched off:
- **Not a freeze but a crawl:** the heartbeat beat for 53 s with the game
  thread in the player's code; start-up took 12 s (PC: 0.36 s), frame 4
  (Galaxy, scene_title) over 20 s (PC: 1.35 s). Nothing drew because the
  title never finished loading.
- **64-bit values through memory read wrong:** path ids and I64CHECK's
  longs format with the low word's sign, so two title atlases (and, the same
  way, audio clips) aren't found. The compares themselves were proven right
  (hw02 probe), so round 7's compare rewrite was removed. Suspect:
  doubleword loads/stores at 4-byte-aligned addresses, possibly emulated by
  the kernel (which would also explain part of the slowness). The player's
  64-bit memory accesses are now two word accesses.
- **Start-up cost found on PC:** the title's six font loads stepped over
  every byte of each font, stored as an integer list, twice. The converter
  now writes byte data as bytes (objects reconverted: 811 MB), which cut
  those frames from ~600 to ~150 ms on PC. What remains: scene_start's
  activation (`Cuphead.Init`, Rewired; ~310 ms on PC) and Rewired's
  exceptions while it probes Windows input APIs.

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

0. **The eighth run (round 12 build staged: map frame rate, chips).** The
   seventh run (VERIFICATION "seventh hardware run"): the map loaded and
   held memory (150 MB, as on PC) but ran at 2-3 fps: "end" (the audio mix:
   12-13 voices, long clips decoded by NVorbis as wasm, up to 0.25 s mixed
   per slow frame) 220-470 ms and the draw list 165-280 ms a frame. Round
   12: clips decoded natively (stb_vorbis, `console/ch360_ogg.c`), the
   mixer without per-sample double conversions, the render list without
   per-vertex camera inversions or per-frame allocations, and every console
   texture re-encoded with a fitted DXT encoder (the title's chips). Read:
   the `FRAMES a-b` lines (300-frame averages per phase, audio decode
   time, voices) on the title, map and a level; whatever phase now leads
   is next. Beyond that, the translated code itself is ~20x slower than on
   PC: float loads from wasm memory go through integer registers
   (byte-swapped) and memory on the console's CPU; a host-side mixer on
   another hardware thread would take audio off the game thread.
   Round 11b's item, kept: the seventh run (round 11b build: the sixth run's crash fixed).
   The sixth run (VERIFICATION "sixth hardware run"): I64CHECK ok, the
   title in ~20 s, the map built in 6.9 s, then a crash in the map's first
   frame: a texture over the budget freed its own new image while making
   room (fixed), and the player's memory reached 183 MB against the PC's
   150 MB (unexplained: the PC check's bot keeps a 1/60 step, the console
   steps by real time; next, a PC run with the real-time step). Now
   textures also make room when the player grows (`__wrap_srt_plat_commit`),
   and each growth is logged with frame and stage. Read: the "memory:
   player a -> b MB" lines against the PC's growth, whether the map plays,
   frame times there, then a level.
   Round 11 found the sign bug's cause: the
   title runs in 32-bit mode (the game thread's MSR 0x200b030, SF clear),
   where a record form (`mr.`, `and.` ...) sets CR0 from its result's low
   word, and xSDK's GCC uses record forms for some 64-bit compares
   (`Number.Int64ToDecStr`: `mr. r7,r5; bge`). `console/xsdk/record_forms.py`
   (the build's compiler launcher) puts `cmpdi 0,rD,0` after each 64-bit
   record form; prebuilt libxsdk/libvulkan4xenon/newlib are not covered,
   and the fix belongs upstream in xSDK's GCC (report it to xenosworks).
   The map froze on texture uploads (68 MB of textures plus a staging copy
   each, on top of the 53 MB still loaded): uploads now go through one 8 MB
   ring, textures stay near the budget and above a 24 MB memory floor,
   off-view items load nothing. CHOB: font bundles indexed without reading
   their font data; large files' objects read through an 8 MB cache of
   32 KB blocks with a few files kept open (the map's 6,000 opens).
   Read in order: I64CHECK (now expected **ok**; a FAIL entry names what
   32-bit mode still breaks: signs, division, carries, unsigned compares);
   the heartbeat's free memory, player and texture MB through the map
   load; the texture lines (free memory per load) and any "no memory"
   lines; CHOB lines and the CONSOLE lines' file/object read counts
   (start-up and map load times against hw05: ~40 s to the title, map
   built 9.8 s + 4.4 s); frame times and GC counts on the map. Then
   frame rate on the map and in a level, from those numbers.
   Fifth run, kept for reference (VERIFICATION "fifth hardware run"):
   0 of 200 probe results wrong on every thread, the title right (logo,
   no early "press any button"), the save's map load froze in its first
   frame's texture loading.
   Round 10's item, kept for reference: the "cpu probe" lines
   per thread (main, two fresh threads, the game thread at frames 1 and
   600, each with its MSR); I64CHECK; whether the map loads and plays;
   frame times and GC counts. Next for frame rate: profile the console
   path again from those numbers (JIT dotnet-trace of the house and map
   without the bot: `dotnet-trace collect -p PID --format Speedscope`, then
   the scratchpad speedscope summarizer; allocations with `--profile
   gc-verbose`).
   Round 9's item, kept for reference: the "cpu probe" table
   first (which producer/consumer pairs are wrong at which nop distance:
   that decides whether hazard.py's gap of 4 and its reader list are right,
   too wide or too narrow), then I64CHECK (ok means atlases, TMP labels and
   music load), then whether the map loads (memory: the tree budget cut the
   heap ~150 MB on PC) and the frame and GC timings.
   Earlier state, hw03 (VERIFICATION "third hardware run"): the
   game reaches the tutorial on hardware; the map load stops on memory, and
   the sign bug (a suspected high-half forwarding hazard) loses atlases,
   TMP labels and music. In order:
   - Probe the hazard (raw asm in probe64_raw.S): producers `or`, `sldi`,
     `add`, `ld`, `lwbrx`+`sldi`+`or`, `mulld` writing a register already
     holding a known value; consumers `cmpdi`, `cmpd`, `srdi`, `std`, `mr`,
     `add`; 0-12 `nop`s between. The rule decides the fix: most likely an
     assembly pass (a compiler launcher, like round 7's removed
     safecmp.py) that separates high-half consumers from their producers.
   - Memory: `dotnet-gcdump` / CH360_TRACE_MEM on PC at the tutorial and
     map; cut the managed heap (parsed CHOB trees kept after building,
     duplicated data), size the texture budget to what's left; the map
     must fit with headroom. Smaller heap also shortens the GC pauses.
   - Then load speed (thousands of small USB reads; packs) and frame time.
1. **The third hardware run (done, see 0).** Read the log in this order:
   - the "mem probe" lines: which of ld/std/ldbrx/stdbrx/lwz/stw are wrong
     at which offsets, and the ns per load at offsets 0-7 (a misaligned cost
     in the hundreds means the kernel emulates it); "player load64/store64"
     must be 0 wrong;
   - I64CHECK (ok means the two-word accesses fixed the sign bug);
   - CHOB and SCENE timing, SLOW frame phases, the heartbeat's file stats
     (MB and ms read from USB against total time) and runtime call counts:
     they split the 30x slowness into USB reads, parsing and game code.
   Then fix the biggest share. If misaligned accesses are emulated, other
   GCC-generated misaligned doubleword accesses in our code (host side)
   matter too. Rebuild (`build_managed.py --stage wasm,c`,
   `build_console.py --stage native` to check, then `--stage xsdk`) and
   restage (`stage_console.py --to E:\Apps\cuphead360` copies only what
   changed).
2. **Packs and 720p textures** (the user's request, once the console's load
   times are known): staging writes one pack each for textures,
   objects and audio (an index, compressed blocks, the files grouped as a
   scene reads them) instead of 5,900 loose files: converted texture names
   run to ~90 characters, past FATX's 42 (the internal drive), each open
   searches a huge FAT32 folder, and USB 2 is ~25-30 MB/s. PC development
   keeps the loose files. Texture pages get sized from their largest
   on-screen scale at 1280x720 (from the game's sprite sizes and
   transforms) rather than one factor for all: the current pages are half
   the source everywhere, so some are likely below what 720p shows and
   others above. Weigh fidelity against performance with measurements;
   compression, packing and streaming cut load time and memory without
   costing detail.
3. **Console gaps known now**: stencil masks (UGUI Mask children draw
   unclipped), render textures and the post-effect camera (also missing on
   Windows), audio stops during long frames (the player mixes per frame:
   8,382 underruns by frame 600 in hw05), the managed heap (~260 MB in a
   level: its biggest holders are not yet measured), the record-form fix
   not reaching prebuilt libraries.
4. **User play reports on Windows** (`play`): root causes, then a sweep.
5. **More of the game** beyond the opening loop.

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
