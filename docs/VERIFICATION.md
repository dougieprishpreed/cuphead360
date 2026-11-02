# Verification

Newest first. Each entry says what was run, on which build, and what it
showed. Windows entries use the JIT oracle (`work/managed/jit`).

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
