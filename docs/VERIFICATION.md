# Verification

Newest first. Each entry says what was run, on which build, and what it
showed. Windows entries use the JIT oracle (`work/managed/jit`).

## 2026-11-03 eighth hardware run and round 13 (hitches, the map, the results board, sound)

The eighth run (`work/console/hw08/`), round 12 build: the title's chips
were clean. The map ran at ~7 fps: 145 ms a frame, of which list 38.6,
submit 30.5, audio 30.1, update 20.7, fixed 8.2 and physics 4.8 ms. The
Root Pack ran at 55-65 ms a frame (~16 fps; 16 ms of it fixed), with
hitches throughout; the user beat it. The user reported:
- the boss's textures blurry and "compressed" next to the rest;
- the results board's text layered;
- the map's music quiet under loud water and animal noise;
- no EX pose for Cuphead (the EX shot itself fires);
- a hitch in the opening logo.

| Finding | How |
| --- | --- |
| Every texture load cost at least ~100 ms, a 4 KB one included, and a clip's first play cost ~650 ms (`audio=640-700` frames after a shot). `GameData/ctex` held 1,337 and `GameData/audio` 3,991 files with 70-110 character names, and FAT32 scans a folder from the start for each open. A clip also ran a `File.Exists` before its open, so two scans. The object folder (163 files) opened in well under 25 ms | the texture lines' times against their sizes (a fixed ~100 ms plus ~40 ms/MB); the heartbeat's file counts at the map load; file counts per staged folder |
| A 4 MB atlas cost ~220 ms more in the frame's submit than its load took. vulkan4xenon copies DXT data into images on the CPU, row by row, from the staging buffer, and that buffer was in the GPU's write-combined memory (read back at 18.5 MB/s by vulkan4xenon's own measurement). vulkan4xenon offers a cached memory type for buffers that are only copied from | `SLOW frame` submit against the texture line; vulkan4xenon `src/command_buffer.c` (drawn_upload: only 8_8_8_8 on the GPU), `src/transfer.c`, `src/memory.c` |
| The results board's layered lines: two banner texts and their flash copies sit under a UGUI Mask that is 0 px wide at that point (a wipe). The console host ignored masks, so they drew over "TOP-GRADE" | PC capture of scene_win (frame 20140 of the bot run): items with stencil state, and their screen boxes |
| The map's ambience (8 looping hint and bridge sources: water, frogs, flower, blimp...) is fully 3D: spatial blend 1, custom rolloff falling to 0 at 4-5.6 units. The engine ignored 3D settings and played them all at full volume everywhere | the scene's AudioSources (UnityPy: panLevelCustomCurve, rolloffCustomCurve, Min/MaxDistance); the AudioListener is on MapCamera |
| The boss's atlases are 4096 px, halved to 2048. The potato is drawn 648x516 px at 720p from 244x194 console texels: 2.8 px a texel (Cuphead: 2.2). Halving makes its one-texel outlines half a texel, so they go grey; that is resolution, and no filter recovers it (below) | a fight frame's capture: screen pixels per texel per sprite; source and console crops side by side |
| EX: not reproduced on PC. The JIT at 1/60 and 1/30, and the translated build at 1/60, all draw the pose. For the game's 0.083 s after shooting stops, the shooting layers (1, 2) keep weight 1 over layer 0's EX state, so the first frames show idle or run (the game's own code); the pose follows. The console build now traces its first three EX animations | JIT bot with `fight ex` and its captures; CH360_STEP=1/30 from the fight; animator traces; native bot traces (below) |

| Change | Check |
| --- | --- |
| 3D audio: the AudioSource's 3D fields (rolloff mode, min/max distance, the rolloff, spatial blend and spread curves) and an NAudioListener. The mixer attenuates and pans each 3D voice from the newest live listener: Unity's logarithmic, linear or custom rolloff weighted by the spatial blend, and a pan from the listener's right narrowed by the spread (no doppler). The 3D getters and setters are implemented | JIT bot: the title, intro and house mix is identical (RMS 1428 both); the map's mix drops from 1846 to 671 RMS. CH360_TRACE_AUDIO3D: the listener is on MapCamera at z 0; near the Root Pack entrance its hint plays at full volume (distance 0.36) and the others at 0 |
| Staged short names for ctex and audio (`stage_console.py`; `ch360_staged_name` in C), with a collision check. `ogg_open` maps clip paths, and the texture loader opens XX/YYYYYYYY.CTX. A clip open is one lookup (no `File.Exists` first). HostClip closes a handle that has no channels | dry run over all 5,328 names: no collisions; the C and Python names agree (test program); not yet on the console |
| Staging buffers take vulkan4xenon's cached memory (buffers only copied from); texture files are read straight into it | build only; the next log should say "vk: staging in cached memory" |
| Native draw path: ConsoleHost calls `draw_view`/`draw_item`/`draw_end`, and the glue writes the CHF1 list. With RenderList.HostTransforms, a sprite under an orthographic camera carries its mesh vertices and one affine map (ProjView * LocalToWorld, the camera rect and the flips) instead of per-vertex screen positions. The old path stays behind CH360_DRAW=managed | native bot, 20,400 frames, run both ways, with every 500th frame's draw list dumped (CH360_DUMP_DRAW): 40 frames, 3,720 items and 286,185 vertices match in count, texture, mode, colour and uv, with positions within 0.0005 px. The one frame that differs is the results board (34 items against 38: the four masked banners clipped away). BOT lines identical. PC map frames: list 2.0 -> 0.85 ms, submit 0.55 -> 0.4 |
| UGUI masks on the console: nested stencil values are kept as rectangles per view, and items that test them are clipped (Sutherland-Hodgman per triangle, in the glue); an empty mask hides them | the results board frame above |
| Converter: halving uses Pillow's Lanczos-3 (premultiplied alpha); the 14 boss and player atlases are re-encoded. The other 866 halved pages keep the 2x2 average for now | filter_compare (scratch) on the potato's and Cuphead's atlases (encode, decode, magnify 2x bilinearly, compare with the 4096 source). Colour PSNR, potato / Cuphead: box 22.92 / 23.64 dB (gradient 0.647 / 0.698 of the source's); premultiplied box 22.94 / 23.70; Mitchell 22.14 (0.598); Catmull-Rom 22.74 (0.641); Lanczos-3 23.03 / 23.75 (0.675 / 0.720) |
| EX trace on the console: the first three Plays of `Ex.*` states on PlayerOne open a 30-frame trace (layers, weights, bools, and the sprite drawn) | native bot: it opened at frames 6975, 7335 and 8415; the EX pose draws from its 7th frame (the game's 0.083 s shooting-layer hold), as on the JIT |
| Bot and tools: `fight N ex` presses B every 2 s. CH360_STEP sets the frame step for headless JIT runs (`<s>[@frame]`) and scripted console runs. Added CH360_TRACE_ANIM_FRAMES. The animator trace no longer crashes on transitions without conditions, and an animator update no longer allocates an array per layer | JIT bot with ex: the Root Pack won on the 4th attempt (frame 19779), then the results board, then back on the map; JIT capture byte-identical before and after the engine changes |
| Built and staged: the XEX (record_forms.py added compares in 19 files, no carry patterns); all 5,328 textures and clips under staged names, the 5,328 old loose files removed; saves untouched | not yet run on the console |

## 2026-11-03 seventh hardware run and round 12 (frame rate on the map, the title's chips)

The seventh run (`work/console/hw07/`): the round 11b build loaded the
save's map after a ~20 s first frame (frame 828: 19.5 s, 6.8 s of it the
map's 41 textures) and ran it at **2-3 frames a second**; the user switched
off. The player stayed at 150 MB (as on PC; the sixth run's 183 MB didn't
recur), no "memory: ... let go" lines. The title had run at ~50-60 fps.
The map's slow frames (500+ ms) split: fixed 5.7, physics 2.5, update
18-20, animators 2.3, late 1.8, ui 2-5 ms, **end 220-470 ms**, draw list
**165-280 ms**. The user also saw corrupted squares on the title's poker
chips.

| Finding | How |
| --- | --- |
| "end" held the audio mix: the console mixes the frame's real time (up to 0.25 s), every long clip decoded by NVorbis as translated wasm, and the map plays 12-13 voices at once (music, ambience); a slower frame means more to decode next frame | PlayerLoop phases; the PC check's per-frame audio and voice counts on the map |
| The draw list transformed each vertex with `NCamera.WorldToViewport`, which builds and inverts the view and builds the projection per call (34,500 vertices a frame on the map); it also built debug names, copied triangles, worked out UVs and atlas bindings (from the parsed tree, by string keys) and allocated items and vertex arrays for every sprite every frame: in a JIT trace of the map, 27% of `RenderList.Build` was garbage collection set off by its own allocations | code; dotnet-trace of `play --hidden` from the hw05 save to the map, `ss.py` |
| The chips' squares are DXT5 error: the converter's colour endpoints were the block's bounding box with transparent texels' colours included, so blocks where red, white and the black outline meet got dark texels; the source is BC7 | the console texture decoded on PC (`ctx_png.py`), against the source the encoder was given |

| Change | Check |
| --- | --- |
| `console/ch360_ogg.c`: the "ch360" ogg imports, stb_vorbis (vendored, public domain/MIT) decoding clips natively from a whole-file read through the runtime's file calls; the engine's `Audio.IClipReader` (NVorbis on the JIT build; `ConsoleHost.HostClip` on the console and in host_native) | native bot 16,001 frames: BOT lines identical; its audio against the JIT run's NVorbis audio over 255 s: the same within a few 16-bit steps except ~950 runs of 1-10 samples (one decoder trims or primes clip ends differently), inaudible |
| The mixer keeps a voice's position as a whole frame and a float fraction inside a block (no double-to-integer conversion per sample: on the console's CPU each goes through memory), the exact double carried between blocks as before | native bot audio before/after: max difference 1 step over 267 s |
| Render list: the camera's view and projection taken once per camera per frame (same arithmetic, same bits); names built only when asked; sprites' int triangles, UVs (`Sprites.RenderUvs`) and atlas bindings (`Atlases.Bound`, until the atlas table changes or the atlas or texture is destroyed) kept; lists, items and vertex arrays pooled from frame to frame; the console host's blend mode per shader cached | JIT bot 15,301 frames with captures every 500 frames: the capture file byte-identical to before; every FRAME/SCENE/BOT line identical but one: at frame 1020 (a scene change) one object fewer, the loading hourglass atlas's texture, which the old per-frame binding lookup re-created after the scene change destroyed it (from frame 1080 the counts match) |
| Profile: an "audio" phase split from "end", the draw list split into "list" (build) and "submit"; a `FRAMES a-b` line every 300 frames averages them, with audio decode time and voices | PC check on the map: 4-6 ms a frame (list 1.7-2.4, update 0.9-1.3, submit 0.6-0.8, audio 0.2-0.3; machine load varies) |
| DXT colour halves fitted: principal axis of the block's texels weighted by alpha, endpoints refined by least squares on their indices (three rounds); per block the fitted or the old encoding, whichever has less alpha-weighted error; DXT1 blocks without transparent texels get it too | the title atlas (2048x2048 from 4096): colour RMSE 10.79 -> 8.57 (PSNR 27.5 -> 29.5 dB), blocks over the error threshold 19,924 -> 14,662; the chips' edge specks gone (`dxt_compare.py`) |
| All console textures re-encoded: `convert_textures.py --bundles` again (1,379 textures, 14 GB in `work/GameData/textures`), then `convert_console.py` (the fit skipped where the old encoding is exact: the same bytes, twice as fast) | 1,337 `.ctx`, 0 errors |
| Built and staged: PC check from the hw05 save to the map and the native bot 16,001 frames (BOT lines as round 11b's, 0 exceptions, 216 MB); the XEX (19 files with 64-bit compares added, no carry patterns) and the 1,337 textures to `E:\Apps\cuphead360`, saves untouched | not yet run on the console |

## 2026-11-03 sixth hardware run (round 11 build) and its fix

The sixth run (`work/console/hw06/`): **I64CHECK ok** on the console for
the first time (the record-form compares), the title up in ~20 s (hw05
~40 s), the save's map built in 4.4 s and activated in 2.5 s (hw05 9.8 +
4.4) with 151 opens for the whole load. Then a crash to the dashboard
while the map's first frame loaded its textures, at 95 of the 96 MB
budget with 39 MB free; `default.log` had vulkan4xenon's "not supported:
image views other than the whole of an image...". The player's memory
grew 118 -> 183 MB across the map load (the PC check: 150 MB, also with
the console's 448 MB limit and with frames slowed to 50 ms; scripted runs
keep a 1/60 step, so the console's real-time step is still untested on PC).

| Finding | How |
| --- | --- |
| A new texture over the budget made room from textures not drawn lately, and its own image (created, never drawn, so the oldest) was the first one freed; the view and descriptor were then made from a destroyed image | main.cpp's `make_image` order; vulkan4xenon `src/image.c` refuses a view whose image fields don't match |

| Change | Check |
| --- | --- |
| `make_image` marks the texture in use before making room; a failed view or descriptor set frees the texture and fails it instead of carrying on | host compiles, no warnings |
| `__wrap_srt_plat_commit`: before the player's linear memory grows, textures not drawn lately make room down to the memory floor; each growth is logged with its frame and stage | XEX links the wrap; staged to `E:\Apps\cuphead360`, not yet run |
| host_native: `CH360_FRAME_MS` slows each frame (console-like timing for unscripted runs) | the save-to-map run at 50 ms frames: 150 MB, as at full speed |

## 2026-11-03 fifth hardware run and round 11 (map freeze, sign bug)

The fifth run (`work/console/hw05/`): the title looked right this time (the
logo atlas loads by its IdText name); the user's save went to the map,
which froze on the hourglass. The scene built (9.8 s, activated 4.4 s),
its first frame began loading textures, and the whole console stopped
between two heartbeats (the log's texture lines had only reached their
40-line cap). The probe was **0 of 200** on every thread, the game thread
at frames 1 and 600 included; its MSR read **0x200b030** (main thread
0x9030): VMX and FP on, SF clear, so the game runs in 32-bit mode too.
I64CHECK failed as before. Start-up to the title took ~40 s: frames 3-4
read ~110 MB, the font bundles whole to index them and their Font objects
whole again, though their data stays lazy.

| Finding | How |
| --- | --- |
| The sign bug is the compiler's: `Number.Int64ToDecStr` (w2c2 `f62128`) tests `value >= 0` as `mr. r7,r5; bge`. A record form sets CR0 from its result's low word in 32-bit mode; GCC (insn `*movdi_internal2`, seen with `-dp`) takes it to judge all 64 bits. Every wrong value in hw01-hw05 is the right magnitude with the low word's sign. The earlier probes used `cmpdi` only, so all passed | disassembly of the XEX's ELF; a 15-line C reproduction gives the same `mr.`; NativeAOT's wasm object (`Cuphead360.Player.13.o`) matched to the linked function by its bytes |
| Of the record forms in one player file, the DImode ones are `*movdi_internal2` (unguarded), `*zero_extendsidi2_dot2` (unguarded), `*anddi3_imm_dot_shifted` and `*anddi3_imm_mask_dot` (GCC limits these to masks <= 0x7fffffff: safe); its carry patterns are all SImode | `-S -dp` of `m6_player.s0000000008.c` |
| The map's first frame draws 41 textures, 68 MB (5 of them, 17 MB, only by items wholly off their view), while the title's and slot select's 53 MB were still loaded; each upload held a staging buffer of its own size until the frame's end, so the frame needed ~190 MB of new GPU memory | `CH360_TRACE_TEX` in host_native: per-frame texture sets from the save in hw05 to the map |
| The map load opened `sharedassets3.assets` (6 MB, 6,173 objects) once per object: 6,000 opens, ~7 s of the console's I/O time | heartbeat file stats, CHOB object counts |

| Change | Check |
| --- | --- |
| `console/xsdk/record_forms.py`, the compiler launcher for every C/C++ file of the console build: `-S -dp`, then `cmpdi 0,rD,0` after each record form of a DImode insn whose result can reach bit 31 (not `andi.`, `andis.` below 0x8000, `rldicl.` from bit 33: a compare after all of them, ~50 a file, pushed a conditional branch in `m6_player.s0000000038.c` past +-32 KB), then assemble; it reports 64-bit carry patterns | the reproduction assembles to `mr.; cmpdi; bge`; the XEX build: 25 compares in 18 of its 87 C/C++ files (`*movdi_internal2` 17, `*zero_extendsidi2_dot2` 7, `*anddi3_mask_dot` 1), no 64-bit carry patterns; the XEX's `Int64ToDecStr` (now `f62165`) reads `mr. r7,r5; cmpdi r7,0` |
| Uploads through one 8 MB staging ring, run when it fills; textures read in 256 KB pieces into it (no whole-file buffer); a new texture first frees textures not drawn for 3 frames down to the 96 MB budget and keeps 24 MB free (else it is drawn without, tried again 2 s later); items wholly off their view are neither loaded nor drawn | host compiles; heartbeat now logs free memory, player and texture MB, and the log is synced every 0.5 s |
| CHOB: large files are indexed from the drive, seeking over byte blobs, and a font object's read leaves out its m_FontData (a ChobBlob); large files' objects are read through 32 KB blocks (8 MB on the console, 16 MB PC, least recently used reused) with four files kept open; the CONSOLE line counts file, object and block reads | JIT bot 15,301 frames: every FRAME/SCENE/BOT line identical to round 10's run; converted bytes read 150 -> 96 MB; heap after GC 136 -> 145 MB (the blocks) |
| Chose blocks over keeping large files whole: the bot's 15,872 object reads (traced with `CH360_TRACE_READS`) come in no useful order (a 64 KB read window still needs 5,000-14,000 reads); whole files under a 16 MB budget re-read 31 files (323 MB) and raised the native linear memory 215 -> 249 MB; 32 KB blocks in 8 MB: ~2,000 reads, 67 MB | `window_sim.py`, `hot_sim.py`, `block_sim.py` (scratch) over the trace |
| I64CHECK also checks signs, CompareTo, Abs, signed division and shifts of negatives, high-word AND, unsigned compares and carries | PC: ok |
| PC checks of the build (translated C, host_native) | from the hw05 save to the map: I64CHECK ok, map built 190 ms + activated 177 ms (round 10: 513 + 268), by the title GCs 44/14/8 and 218 ms paused (round 10: 60/30/26, 493 ms), linear memory 150 MB (134); native bot 16,001 frames: BOT lines as round 10's, 0 exceptions, 15,900 object reads in 2,029 block reads, GCs 3,246/281/26, 3.0 s paused (3,292/303/50, 3.6 s), linear memory 216 MB (215) |
| Built and staged | `default.xex` 102,543,360 bytes to `E:\Apps\cuphead360` (data unchanged, saves untouched). Not yet run on the console |


## 2026-11-02 fourth hardware run and round 10 (frame rate)

The fourth run (`work/console/hw04/`): the CPU probe found **0 of 200**
producer/consumer pairs wrong at any distance on the main thread, yet
I64CHECK failed exactly as before on the game thread, so neither a
forwarding hazard nor alignment explains it (hazard.py removed). The
player reached the map: built 7.6 s, activated 5.1 s; then
`vkAllocateMemory` failed (-2), `sbrk` failed and `std::bad_alloc`
aborted to the dashboard (84 MB free before the map; player 166 MB,
textures 77 MB). Also: the long pause before the logo, "press any button"
showing behind the logo (its atlas name was one of the sign-bug misses),
hitches and loading-screen stalls.

| Change | Check |
| --- | --- |
| `IdText.Of`: path ids in decimal with 32-bit arithmetic (texture, clip names) | I64CHECK compares it with long.ToString; PC equal |
| Render textures get no converted-file request (GlowBossTitle) | PC bot run |
| Probe on the main thread, two fresh threads (one made like the game's) and the game thread (frames 1, 600), each with its MSR | host compiles |
| Texture memory: a failed allocation frees textures not drawn for 3+ frames and retries; a texture read that can't get memory makes room or skips (no abort); budget 96 MB; tree budget 24 MB, decoded audio 16 MB | host compiles |
| dotnet-trace of the Root Pack fight (JIT, bot excluded): GC 60% of frame time; sampled allocations 782 MB in 8 s, 47% `MethodInfo[]` from uncached message lookups in `NAnimator.Apply` | after: 218 MB in 8 s |
| Message lookup cached; no-argument script messages through cached delegates (no `MethodInfo.Invoke`, no `GetParameters()` per call); mesh channels typed, arrays reused, no boxing; `CanvasRenderer.SetMesh` reuses its arrays; root canvases refit only when changed; key names memoized; per-frame list copies pooled; animator binding closure only on first bind; typed registries compact lazily (order kept) | JIT bot 15,301 frames: every FRAME/SCENE/BOT line identical to round 9's run, 0 exceptions; selftest 23/23 |
| Physics: shapes whose boxes are apart skip the Minkowski test (exact); Minkowski/Hull/negation use scratch buffers | bot loop identical again; 18.2 s against 22.3 s |
| GC: `DOTNET_GCgen0size=0x800000` was ignored (the 0x); native counts showed 32,149 gen0 GCs by frame 6000, 8.7 s of pause, most of the bot's path-planning geometry (7.5 of 8.6 GB sampled across the map load) | after the allocation fixes, native bot 16,001 frames: the runtime's own small gen0 budget 2,490/250/51 GCs, 3.4 s of pause in all, linear memory 215 MB; an 8 MB gen0 budget (honoured as `800000`) 326/326/175, every GC gen1+, 16.4 s, 247 MB. The default is kept (the ignored setting removed) |
| Console frame step capped at 1/30 s (at most two fixed steps a frame; the game slows below 30 fps instead of falling further behind); the mixer gets the real frame time | |

## 2026-11-02 managed player, round 9 (memory, high-word hazard)

| Change | Check |
| --- | --- |
| CH360_TRACE_MEM census of parsed trees by type (`TREES` lines) | JIT bot to Root Pack: heap 276 MB, of it parsed trees 163 MB (Sprite 93 MB: 4,889 trees ~16 KB each, mostly m_PhysicsShape point maps and vertex channel maps), file bytes held 22 MB |
| Byte lists of 16+ values as bytes (converter and parser; was 4096) | objects 811 -> 761 MB; sprite trees 93 -> 79 MB; sprites with under 4 KB of vertex data now get their mesh (ReadMesh read only byte[]) |
| A parsed-tree budget: least recently used trees dropped and parsed again from the file when asked for (`ChobObject.TreeBudget`; the console sets 32 MB, CH360_TREE_BUDGET_MB on PC) | heap at Root Pack 276 -> 126 MB, end 135 MB after GC (was 288-296); 18,223 trees dropped, 217 parsed again; JIT bot 15,301 frames 0 exceptions; selftest 23/23; saves unchanged |
| `hazard.py`: nops between a register's write and a high-word reader (compares, doubleword shifts/rotates, cntlzd, mulld/divd, std), gap 4 | self-test; one player file: 2,412 nops in 243,895 instructions (1%) |
| `probe64.c`: producers or, sldi+or, add, ld, mulld x consumers cmpdi, srdi 32, std, mr, addi at 0-12 nops, stale high word preset | assembles; `hz_so_cmp_0` is f62085's `sldi; or; cmpdi` |
| JIT bot with a 32 MB tree budget; `host_native` bot (console mode) | 15,301 frames, 0 exceptions, heap 135 MB after GC; 16,001 frames, 0 exceptions, bad lists 0, linear memory 215 MB (was 330) |
| `build_console.py --stage xsdk` (hazard.py on every object; writes before labels and conditional branches count, calls/returns/jumps reset) | `default.xex` 103,043,072 bytes; of 665 `cmpdi` in our code 653 are 4+ instructions from their register's write, the other 12 follow a jump (not reached by falling through); 389 in prebuilt xsdk/vulkan4xenon/newlib unpadded |
| `stage_console.py --to E:\Apps\cuphead360` | XEX byte-identical; 583 object files copied, identical |

## 2026-11-02 third hardware run (read; work paused)

`work/console/hw03/`: after a long black screen the game reached the title,
slot select, the intro, Elder Kettle's house and the tutorial (the user
played it), slowly; leaving the tutorial for the map it stopped on the
loading screen; tutorial button labels were missing.
- **Memory probe clean:** ld/std/ldbrx/stdbrx/lwz/stw/lwbrx/stwbrx correct
  at offsets 0-7, line and page ends; misaligned loads cost 2 ns like
  aligned ones. Not the cause of anything.
- **I64CHECK still fails** the same way. The built `Int64.ToString`
  (`f62085`) assembles the long with `lwbrx; lwbrx; sldi; or` and tests it
  with `cmpdi r29,0` on the very next instruction, then passes that same
  register to the formatter, which formats the full value. All six results
  fit "the compare saw the low word sign-extended". Round 7's probe found
  `cmpdi` right on values that arrived through a call; round 7's rewrite
  (`srdi r12,r7,32` right after `rldimi r7`) failed identically. Suspect: in
  32-bit mode a consumer of a register's high half right after its producer
  reads a stale/sign-extended high half (a forwarding hazard). Not yet
  probed.
- The sign bug breaks every converted name built from a 64-bit path id:
  the player and title atlases, `jumpdust`, the TextMeshPro SDF font atlas
  (the tutorial's missing button labels) and music clips ("AUDIO clip not
  converted").
- **The map-load stop is memory:** at frame 1200 (tutorial) 9 MB of 512
  free (player 215 MB, textures 103 MB); at the map load the heartbeat beat
  on for 150 s with the frame stuck at 1623 and the runtime-call count
  barely moving (GC with no room, most likely). PC peaks at 330 MB of linear
  memory in Root Pack.
- **Speed:** 76-129 ms a frame on average (13 fps or less); recurring
  500-1250 ms spikes in the "end" phase and the draw list (GC pauses over a
  ~170 MB heap, most likely); FixedUpdate ~23 ms a frame in the house;
  scene_start activation 4.9 s, house built in 9.9 s; files: 9,333 reads,
  28,078 seeks, 204 MB in 33.5 s (~6 MB/s); audio underruns grow with the
  slow frames.

## 2026-11-02 managed player, round 8 (second hardware run)

The second run (`work/console/hw02/`) disproved round 7's conclusion: the
CPU probe's raw `cmpd`, `cmpdi`, `cmpld` and `cmpldi` all gave the 64-bit
answer (GT on operands that differ only in the high word), and `cntlzd 1`
was 63. Yet I64CHECK failed the same way (`2147483648` -> `-18446744071562067968`,
`-4294967296` -> `18446744069414584320`) while its register compares passed:
the sign comes from the low word only on the way through memory. The
compare rewrite fixed nothing and was removed. Suspect now: doubleword
loads and stores at 4-byte-aligned addresses (in the built code w2c2's
8-byte accesses became `ld`/`std`, e.g. `ld r10,4(r9)`, and the player keeps
longs 4-byte aligned).

The run did not freeze: the heartbeat ran for 53 s with the game thread in
the player's code. Start-up (I64CHECK, AssetDb, scene_start) took 12 s
against 0.36 s for the same C on PC (`host_native`), and frame 4 (Galaxy,
scene_title) had not finished after 20 s against 1.35 s on PC, so the
console ran 30x slower or more; the first run's "freeze" was probably the
same wait.

| Change | Check |
| --- | --- |
| 64-bit memory accesses as two 32-bit ones (`console/w2c2/w2c2_base.h`) | xsdk GCC emits `lwbrx`/`stwbrx` pairs for `i64_load`/`i64_store`/`f64_load` (no merged doubleword access) |
| `probe64.c`: ld/std/ldbrx/stdbrx/lwz/stw/lwbrx/stwbrx and the player's accessors at offsets 0-7 inside a line, at a line end and a page end; ld/lwz cost per offset | compiles; runs on hardware next |
| The log thread drains the player's output during long frames; heartbeat adds runtime calls and file opens/reads/MB/ms (srt_plat_* wrapped) | `main.cpp` compiles |
| SLOW frame lines (player-loop phases), slow script calls (over 50 ms), CHOB file and SCENE load times, decoded-audio cache 32 MB on the console | on PC they named the cost: scene_start's activation 310 ms (`Cuphead.Init`: the Core prefab, Rewired), frames 3-4 ~600 ms each, of which six font loads (`FontLoader.Initialize`) ~420 ms |
| `dotnet-trace` of the JIT player's first 30 frames (`work/managed/trace/`) | font loads 809 ms: `ChobFile.Load` stepping over every element of fonts' m_FontData (written as integer lists) to index the bundle, then again to build the font |
| `convert_objects.py`: integer lists of 4096+ byte values written as bytes (tag 7); the engine reads a large m_FontData lazily (other bytes stay `byte[]`) | objects 908 -> 811 MB, 0 errors (163 + 420 files, 37.7 + 73.9 s); first 30 frames 1.2 s -> 405 ms (JIT, trace); host_native frame 3 594 -> 154 ms, linear memory at the title 166 -> 118 MB |
| A first try made every 1 MB+ byte value lazy: TextAssets and script fields then came back as blobs and the bot run hit NullReferenceExceptions in `CupheadGlyph.Init` at slot select; bisected (not the byte lists, not the converter's Shader stubs) and limited to m_FontData | JIT bot loop 15,301 frames: 14 scene loads to Root Pack, 0 exceptions; selftest 23/23; `host_native` bot 16,001 frames, 0 exceptions, bad lists 0, linear memory 330 MB; saves unchanged |

## 2026-11-02 managed player, round 7 (first hardware run, 64-bit compares)

(Round 8 disproved this round's conclusion: the compares are right.)

The user's first hardware run (`work/console/hw01/`): the XEX started,
reserved the player's memory (379 MB free at start, 358 MB after), opened
Vulkan at 1280x720, loaded scene_start and scene_title, logged "Build
version 1.3.2", then nothing more; a black screen until the console was
switched off. Two title atlases were "missing": the player had asked for
`..._-12598203202371876835.ctx` and `..._15854386641536097845.ctx`, path ids
5848540871337674781 and -2592357432173453771 formatted as if their sign came
from the low 32 bits (2^64 - id both times). The built code
(`f62085`, `Int64.ToString`) loads the id with `ldbrx` into one register,
tests it with `cmpdi r29,0` and formats that same register, so the compare
itself got the sign wrong: xsdk documents that a title runs with MSR[SF]
clear (32-bit mode), and there the console's doubleword compares saw the low
words. The xsdk GCC (-m32 -mpowerpc64) keeps 64-bit values in one register
and compares them with `cmpd`/`cmpdi`/`cmpld`/`cmpldi` (3,915 in this XEX's
translated code, 218 functions in xsdk/vulkan4xenon/newlib). Besides wrong
values, the wasm runtime's deadline compares on nanosecond clocks can then
sleep for ~2^64 ns, a candidate for the stall.

| Command | Result |
| --- | --- |
| `safecmp.py --self-test` | the rewrites as expected; every signed and unsigned outcome on 14 values differing in high/low words matches a 64-bit compare |
| xsdk GCC through `safecmp.py` on test sources, `probe64.c`, `main.cpp` | assembled; 0 doubleword compares left; rewritten sequences read correct by disassembly |
| `build_managed.py --stage jit,wasm,c` | OK (adds `ConsoleHost.SelfCheck`, "I64CHECK") |
| `build_console.py --stage native` + `host_native --frames 900` (`work/console/hw01/pc_900b.log`) | I64CHECK ok, scene_title, frame 600, 0 exceptions, bad lists 0 |
| `build_console.py --stage xsdk` | `default.xex` 99,717,120 bytes; every C/C++ object through safecmp.py (record forms kept: CR0 from the low word in 32-bit mode is what GCC expects; out-of-range conditional branches inverted over a `b`) |
| objdump of the linked image | doubleword compares 5,915 -> 1,122, all in 210 prebuilt library functions (xsdk, vulkan4xenon, newlib) except the probe's three deliberate ones |
| `stage_console.py --to E:\Apps\cuphead360` | XEX byte-identical on the USB; the data already there (5,911 files, 0 copied; the free-space check now counts only what changes) |

Not verified: that the console's compares behave as concluded (the next
run's "cpu probe" lines test the raw instructions against the rewritten
ones), what stalled the first run, anything past the title on hardware.

## 2026-11-01 managed player, round 6 (audio, memory, first console build)

Source: `acd078c` plus the round-6 commit. Windows JIT oracle, the
translated C as a headless PC program, and the xSDK build (WSL
CTR360-XSDK: xsdk `8c58c7a`, vulkan4xenon `df25dc5`).

| Command | Result |
| --- | --- |
| `tools/convert_audio.py --bundles` | 3,991 clips, 390.5 MB Ogg, 0 errors, 606 s |
| `run --frames 5600 --bot ... --audio-out work/managed/audio_run.wav` | 93 s of 48 kHz stereo: music and effects through the opening (RMS 0.01-0.08 per 5 s, peak <= 0.73, no clipping); the audio icalls are gone from the missing ledger |
| `CH360_TRACE_MEM=1 run ...` | managed heap at the world map 2,596 MB -> 255 MB (fonts' m_FontData was a boxed value per byte; lazy object trees; large files read per object); ~310 MB in a level; the full loop still 0 exceptions |
| `play --hidden 1 --bot ... --max-frames 40000` (`live9.log`) | the GL renderer offscreen (no window on the desktop): title -> house -> map -> Root Pack, 5 knockouts and retries (death card, retry), won at 26541, results, map at 27104; screenshots of results and King Dice's tooltip match the preview |
| `build_managed.py --stage wasm,c` | player.wasm 53.4 MB (every patched assembly rooted, rd.xml for XmlSerializer's lists); w2c2 62 C files, module m6_player; 75 imports: srt 35, ch360 6 (console host), 34 wasi:http/io trap stubs |
| `build_console.py --stage native` + `host_native --frames 16000 -- run --console 1 ... --bot` (`native4.log`) | the translated C on PC: 16 scene loads, title -> slot select -> intro -> house -> map -> Root Pack, knockouts at frames 9027, 12875, 15245 (the same frames as the JIT hidden run), 0 exceptions, every draw list well-formed, 126 textures, 250 s of audio; linear memory 347 MB peak (313 MB with the console's GC settings, `native5.log`) |
| `build_console.py --stage xsdk` | `default.xex` 99,536,896 bytes; shaders through xost (6), main.cpp clean under -Wall -Wextra |
| `tools/convert_console.py` | 1,379 textures: DXT5 1,137, DXT1 3, BGRA8 197, 42 run-time; 4,049 MB; 0 errors; 1,732 s; sample pages within 1.7-2.7/255 mean error of the source |
| `tools/stage_console.py --to E:\Apps\cuphead360` | 5,911 files, 5.22 GB copied to the USB: default.xex (byte-identical to the build), GameData/objects (164 top-level entries), ctex (1,337), audio (3,991), fonts/arial.ttf (the PC's own), save/, licenses/ |
| `tools/save_guard.py check` | `OK, 3 files unchanged (SHA-256)` |

Bugs the translated build found and fixed: NativeAOT has no
`Assembly.GetReferencedAssemblies` (AssetDb falls back to the loaded
assemblies); XmlSerializer needs `List<T>` indexers (rd.xml); a house
tutorial `AddComponent<BoxCollider>` needed UnityEngine.PhysicsModule
compiled in (all patched assemblies rooted).

Not verified: anything on the console (the XEX has not run yet: the first
hardware test is the user's), console frame time and memory, audio output
through xsdk's feeder, the Vulkan renderer on vulkan4xenon (no PC Vulkan
check of this renderer was run), stencil masks on the console (not
implemented there: mask graphics are skipped, children draw unclipped).

## 2026-11-01 managed player, round 5

Source: `8f3201d` (input, 2D physics, dynamic fonts, bot) and `5c2b512`
(live window, collision response, UI layout/mask/text fixes). Windows JIT
oracle; textures exported with `tools/convert_textures.py` (1,379 textures,
14.2 GB, 0 errors).

| Command | Result |
| --- | --- |
| `selftest` | `SELFTEST PASS: 23 passed, 0 failed` |
| `run --frames 15301 --save work/managed/save_bot --bot tests/input/bot_veggies.txt` (`work/managed/run63.log`, `run65.log`; save dir deleted first) | the full normal loop from the original code under scripted pad input: title -> slot select -> new game -> intro book -> Elder Kettle's house (frame 3308) -> world map (5442) -> walks the map paths (A* over the game's own colliders) to Root Pack, enters on Simple (level at 8715) -> fight won (13759) -> scene_win results (13768) -> back on the map (14322) -> King Dice's map tooltip. 16 scene loads, 0 exceptions, 4,264 fields decoded, 0 mismatched, 0 data problems; 15 missing engine methods (audio: `PlayOneShotHelper`, `Play`, `Stop`, `UnPause`, `AudioListener.pause`; `RenderTexture` size; `Texture2D.SetPixel/Apply`) and 6 Rewired user32 P/Invokes (its expected fallback to Unity input) |
| User report "player walks through unpassable scenery on the world map" | `Physics2D` now separates dynamic bodies from solid colliders (Minkowski MTV); the player stops at walls; the bot had to path around them |
| User report "text in word bubbles is improperly placed" | house bubble (TMP): resizes with OnRectTransformDimensionsChange. King Dice map tooltip: the portrait was 640 px to the right (RectTransform grandchildren lost their anchoredPosition when an ancestor resized), the text had no margin (the cloned prefab lost its RectOffset padding of 35), and it sat high (line spacing applied below lines instead of above). After the fixes (frame 15300 preview and live shot): portrait left, bubble right with its tail on the portrait, text wrapped inside 35 px margins, CONFIRM bottom-right, the map darkened behind |
| Same root causes elsewhere | level HUD moved from screen centre to bottom-left; the stale duplicate speech bubble in the house (world-space canvas seen by the UI camera on its near plane) is now clipped; the HUD weapon card's white square was a UGUI Mask (now stencil-only) |
| `play --bot ... --unlocked 1 --shot 4200,9000,15300` (`live4.log`) | live window, the same loop to the map tooltip; screenshots match the preview; holds 60 fps once warm (level: logic ~1.1 ms, build 0.5 ms, draw 0.2 ms); hitches at scene loads (0.6-2 s) |
| `play` again after the blend fix (`live5.log`) | ended at frame ~7800 with no error and no crash event (window closed); before that the bot looped on a failed plan at the Root Pack gate (live dt is not deterministic): a bot limitation, not a game fault |
| `tools/save_guard.py check` | `OK, 3 files unchanged (SHA-256)` |

Not verified: audio (not implemented), render textures and the post-effect
camera (not drawn), a comparison against the user's PC game, the whole loop
played by hand on a pad in the live window, any console build.

## 2026-11-01 managed player, round 4

Source: the round-4 commit ("Render state, Animator, UI and a Windows preview
of the title"). Windows JIT oracle; converted data unchanged from round 3.

| Command | Result |
| --- | --- |
| `python tools/build_managed.py --stage gen,engine,redirect,jit,census,selftest` | gen: 792 implemented, 1,605 property-bag accessors, 5,549 stubs; census OK; `SELFTEST PASS: 23 passed, 0 failed` |
| `run --frames 900 --capture 60,150,...,900` (`work/managed/run17.log`) | scene_start -> scene_title; 0 exceptions, 0 data problems, 556 script objects decoded, 0 mismatches; 1 missing engine method (`AudioSource::PlayOneShotHelper` x2); PROPS: 9 accessors, all AudioSource/AudioClip/AudioMixerGroup |
| `run --frames 640 --capture-every 5` (`run16.log`) + `tools/preview.py` | the game's own sequence, read from the captures: LevelCamera draws Background, Title_Animation, SplashMDHR, Fader; Fader alpha 1 -> 0 between t=1 s and 2 s (StartScreen.tweenRenderer_cr); `mdhr_logo_0001` .. `mdhr_logo_00100` (anim_splash_mdhr_logo, 24 fps), then the logo's alpha reaches 0 by t=8 s; `cuphead_title_screen_00NN` cycling (animator_title_screen); "Press Any Button" TextMesh Pro meshes (60 vertices, `CupheadVogue-ExtraBold-merged outline SDF Atlas`) toggling every 0.75 s (FlashingPrompt) |
| Title atlas sprites packed FlipHorizontal / FlipVertical / Rotate180 | all 16 frames of a dance cycle (frames 545-620) draw upright and whole after the flip handling and the preview's least-squares UV fit; no sprite packed Rotate90 was met |
| Late-bound atlases | Title_Assets, Title and mdhr_logo requested through `SpriteAtlasManager.RequestAtlas`, loaded by the game's `SpriteAtlasLoader` from their bundles, bound via `Register` |
| `tools/save_guard.py check` before/after every run | `OK, 3 files unchanged (SHA-256)` |

Preview frames: `work/preview/frame_*.png`, contact sheet
`work/preview/title_round4_sheet.png` (git-ignored, derived from the game).
Not compared yet with the user's PC game (needs a reference capture); not
drawn: the game's post-effect camera (film grain, chromatic aberration, blur).
Not verified: input, audio, anything past the title, any console build.

## 2026-11-01 managed player, round 3

Source: `bb73ff0` (engine) plus the round-3 docs/cleanup commit.

| Command | Result |
| --- | --- |
| `python -I tools/convert_objects.py --game game --out work/GameData` (Shader stubs, TextAsset bytes, built-in resources) | 161 files, 178,391 objects, 0 errors, 39.6 s; built-in resources 2 files, 110 objects |
| `... --files none --bundles` | 420 files, 89,771 objects, 0 errors, 73.1 s |
| `python tools/build_managed.py --stage gen,engine,redirect,jit,selftest` | gen: 419 implemented, 1,773 property-bag accessors, 5,754 stubs; `SELFTEST PASS: 23 passed, 0 failed` |
| `run --frames 300` (`work/managed/run13.log`) | scene_start -> scene_title (36 GameObjects, 92 components) through the original `LoadSceneAsync`; `UNITY Error: Failed to initialize GOG Galaxy: ... RuntimeError: Galaxy peer is not available on this platform` (original unavailable path); game wrote its default settings JSON to port PlayerPrefs; 0 exceptions, 0 data problems, 556 script objects decoded, 0 mismatches; 17 missing engine methods (render state, Animator, PlayOneShot, vSyncCount) |
| `tools/save_guard.py check` before/after the runs | `OK, 3 files unchanged (SHA-256)` |

Notes: run8 showed the game's own behaviour with a stale port-local
PlayerPrefs file written by a pre-fix round-2 run (BOM, empty settings value):
`SettingsData.Data` stays null, as it would in Unity. The file was the port's
own (`work/managed/save`), deleted, and regenerated correctly. Not verified:
rendering, Animator, input, audio, anything past the title, any console build.

## 2026-11-01 managed player, round 2

| Command | Result |
| --- | --- |
| `python -I tools/convert_objects.py --game game --out work/GameData --files none --bundles` | 420 files, 89,711 objects, 0 errors, 71.3 s |
| `python tools/build_managed.py` stages through selftest | gen: 387 implemented, 1,775 property-bag accessors, 5,784 stubs; `SELFTEST PASS: 23 passed, 0 failed` |
| `Cuphead360.Player run --frames 600 --seed 360` (`work/managed/run5.log`) | asset bundles load; full `Cuphead.Init` reaches `OnlineInterfaceGog.Init` and throws at the unimplemented Galaxy SDK P/Invokes; `Cuphead.Awake` throws in `AudioManager.masterVolume` (AudioMixer.GetFloat); Rewired initialization throws; `Vibrator.Update` throws each frame (600); still scene_start |
| `%APPDATA%\Cuphead` SHA-256 before/after | unchanged (3 files) |

## 2026-11-01 managed player, round 1 (single-agent)

Architecture: [MANAGED_PLAYER.md](MANAGED_PLAYER.md). Source: `5b4ef49`,
`9126b86` and the docs commit that follows them. Windows, .NET 10.0.401 JIT
unless noted; one run each.

| Command | Result |
| --- | --- |
| `python tools/build_managed.py` (patcher, publicize, gen, engine, redirect, jit, census, selftest) | `BUILD_MANAGED OK`; publicize 960 types / 5,986 fields / 7,940 methods; gen 7,946 engine methods (6,113 icall, 1,833 P/Invoke); redirect 7,946 patched, 0 left native, 2 `GetFolderPath` calls redirected |
| `Cuphead360.Player census` | 8,438 types (0 failed), 60,077 methods compiled (0 failed) |
| `Cuphead360.Player selftest` | `SELFTEST PASS: 23 passed, 0 failed` |
| `dotnet publish -r wasi-wasm -p:CupheadWasm=true` (ILCompiler.LLVM 10.0.0-rc.1.26357.1, wasi-sdk 29) | exit 0; `Cuphead360.Player.wasm` 26,400,236 bytes, core module (`0061736d01000000`) |
| w2c2 `-f 2000` + Spire360 `w2c2_fix_tables.py` / `w2c2_imports.py` | 40 C files, 186 MB; 64 imports, 30 implemented by srt, 34 missing (all `wasi:http`/`wasi:io` streams) |
| `python -I tools/convert_objects.py --game game --out work/GameData` | 161 files, 178,326 objects, 0 errors, 38.4 s |
| `Cuphead360.Player run --frames 300` (`work/managed/run3.log`) | scene_start loaded (2 GameObjects, 7 components); original `CupheadStartScene.Awake` -> `Cuphead.Init` instantiates `Core/CupheadCore`; fields decoded 496, mismatched 0; 22 exceptions and 9 missing engine methods, all at unimplemented AssetBundle/audio/font/JsonUtility calls; no scene after scene_start |
| `%APPDATA%\Cuphead` SHA-256 before/after all runs | unchanged (3 files) |

Not covered: rendering, audio, input, later scenes, the XDK compile or any run
of the translated C, Xenia, hardware, performance, memory.

Interpreter-era note: the interrupted D1 r4 source was committed unvalidated
to branch `interpreter-d1-r4-wip`; its fixture build fails the T4 mutation
assertion. The interpreter is retired; the entries below are history.

## Earlier records

The interpreter-era records (CP1, D1, M1 combat probes, boss scene boots,
the native movement slice) and their evidence were removed from the tree in
round 3 with the code they tested. They are in git history: `git show
bb73ff0:docs/VERIFICATION.md` (and `docs/evidence/` at the same commit).
They do not validate anything in the managed player.
