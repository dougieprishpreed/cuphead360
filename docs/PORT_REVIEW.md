# Cuphead360 port review

> **2026-11-01 decision:** the interpreter strategy reviewed here is retired.
> The game's IL now runs on .NET with a C# Unity engine and is compiled for
> the console through NativeAOT-LLVM -> wasm -> w2c2 -> C
> ([MANAGED_PLAYER.md](MANAGED_PLAYER.md)). Part A's content, memory,
> texture, renderer and measurement sections (A4, A6, A7) still apply; its
> execution-strategy, CLR-gap and D1/G1 sections (A3, A5 item 4, A8 steps
> 0-3, A11 F1/F2/F5/F7/F11) describe the retired runtime.

This is the canonical technical review.
- **Part A** is the shipping end-state review dated 2026-11-01. It was revised the same day
  after a source and tool coverage pass and the host's release-review feedback. It
  supersedes the plan ordering of earlier reviews.
- **Part B** is the historical fundamental review dated 2026-10-31, kept for provenance.
  Where it conflicts with Part A, Part A wins.

Evidence lives in [VERIFICATION.md](VERIFICATION.md). Recovery state is in `HANDOFF.md`
and `NEXT_SESSION_PROMPT.md`, which are host-owned.

---

# Part A — Shipping end-state review (2026-11-01, revised after coverage pass)

## A0. Provenance and coverage

- **Author:** external project lead (`claude-opus-5-5`, subscription; high effort
  requested; zero API spend).
- **Method:** files-only engineering review. No builds or runs were made, and nothing
  here is a formal proof of correctness.
- **Source state:** as recorded in `work/restart-20261031/release-review-inventory.json`:
  - HEAD `e6cd4bc`;
  - accepted CP1 `4c40d42`;
  - final D1 r2 source uncommitted (`interpreter.cpp` `47FEDA84…`, `app.cpp` `3F894907…`).

**Inventory.** The inventory lists **76 paths**, not 66 as the previous revision said:
74 tracked files plus the untracked `tests/fixtures/D1DispatchFixture.cs` and
`tests/fixtures/build_d1_fixture.bat`. Its scope
excludes the ignored `game/`, `work/` and `build/` trees. Line counts are taken from the
inventory.

| Group | Paths | Lines |
| --- | ---: | ---: |
| `src/` C++ sources and headers | 35 | 17,378 |
| `tools/` Python | 3 | 3,798 |
| `tests/` fixtures, fixture builds and reference harness | 5 | 1,066 |
| `projects/` and `scripts/` | 8 | 335 |
| `README.md`, `.gitignore`, `docs/*.md` (15), `docs/evidence/` (8, including 4 PNGs) | 25 | documentation |

**Coverage by path** (all reading rounds combined):

| Level | Paths |
| --- | --- |
| **Read in full** | **Runtime:** `metadata.cpp`, `metadata.h`, `native.cpp`, `value.h`, `object.h`. **Engine:** `animation.cpp`, `animation.h`, `scene.h`, `renderer_soft.cpp`, `renderer_soft.h`. **Game:** `player_slice.cpp`, `player_slice.h`. **Core:** `containers.h`, `endian.h`, `types.h`, `file.cpp`, `file.h`, `log.cpp`, `log.h`, `mem.cpp`, `mem.h`. **Platform:** `display.cpp`, `display.h`, `input.cpp`, `input.h`, `platform.h`, `platform_winapi.cpp`, `main_win32.cpp`, `main_xbox360.cpp`. **App:** `app.h`. **Tools:** `build_metadata.py`, `metadata_lib.py`. **Build:** `Cuphead360.props`, both `vcxproj` files, the Xbox `.sln`, `build_win32.bat`, `build_xbox360.bat`, `build_reference.bat`. **Tests:** `build_cp1_fixture.bat`, `build_d1_fixture.bat`, `CP1CoroutineFixture.cs`, `EaseReference.cs`. **Docs:** `README.md`. |
| **Read substantially** | `interpreter.cpp`: call, invoke and delegate paths; the arithmetic, shift and branch handlers sampled in A11; allocation and liveness; coroutines and the ledger; the D1 hunks. Not every opcode handler was read. |
| | `interpreter.h`: the D1 and ledger declarations. |
| | `unity_bind.cpp`: the dispatcher; the binder and object identity; tags; GetComponent and Find; transform setters; physics queries; Random, Time and StringToHash; Instantiate and Clone; lifecycle; Destroy and SetActive; UI and Camera stubs. Not every UI or TMP stub was read. |
| | `app.cpp`: every harness and test driver (`RunStackTest`, the combat helpers, `RunLevelTest`, `RunPlay`, `RunLogicTest`, `RunScene`, `AppMain`, plus the CP1 and D1 drivers from earlier rounds). The self-test and inspect helpers were only skimmed. |
| | `scene.cpp`: load, strings, textures and transforms. |
| | `extract_scene.py`: lines 1–690 (atlas cache, `sprite_info`, the binary writer, serialized-field parsing) and 1254–1841 (bounds, animation extraction, `main`, preview). |
| | `D1DispatchFixture.cs`: the case list and the mutation script. |
| **Sampled** | `extract_scene.py` lines 690–1253: streamed-frame decoding, the cross-file asset-graph closure, PPtr remapping and prefab append. |
| **Not opened in this cycle** | Win32 `.sln` (19 lines); `.gitignore`; the eight `docs/evidence/` items. |
| | `docs/*.md`: read in earlier rounds as reference, and this cycle only searched for claims the findings contradict (A11 F1, A6). They are not audited line by line. |

**Original content** was **sampled, not verified**, and no claim of content parity is
made. The sample covers:
- decompiled `Level`, `SceneLoader`, `HouseLevel`, `PlayerData`, `SlotSelectScreen`,
  `OnlineInterfaceGog`, `LevelPlayerMotor`, the Veggies classes and
  `LevelIntroAnimation`;
- host inventories of textures, the Player atlas and scene types.

## A1. Verdict

**The goal is not achieved, and its feasibility is not established.** The goal is a
single `default.xex` plus a user-run converter that plays the user's GOG Cuphead (base
game plus The Delicious Last Course) virtually identically on Xbox 360. Neither the total
work size nor the end-state performance is known. This review makes **no time
estimate**.

The repository is a semantic pilot:
- it executes the original game IL through an interpreter with a partial Unity layer;
- finite scripted harnesses can run the startup code of a few scenes;
- packed sprites are drawn incorrectly;
- there is no audio, scene flow, Animator, UI, GC, exception handling, correct physics,
  storage or shipping boot.

**Xbox evidence is weaker than it looks.** It is compilation plus old Xenia smoke runs.
The default Xbox entry point runs the native `PlayerSlice` movement scaffold rather than
original game logic (A11 F12).

The coverage pass found further structural gaps, described in A11:
- methods of game-defined generic types never execute as IL; natively written substitutes
  stand in for parts of them (F1);
- `Instantiate` can activate a template in place instead of cloning it (F3);
- per-value heap allocation grows without bound (F5);
- the scene string-table sentinel is corrupted (F8);
- several harness paths can report success where the CLR or Unity would differ.

**Strategy preference.** Keep original-IL execution and make it fast through
converter-produced bytecode plus bound native services (A3). This is a practical
preference on current evidence, not a proof that it is the only workable route. Two
measurements must come before further breadth work:
- managed and native work per frame on real hardware (A7, gate G-B);
- the per-scene texture working set in GPU formats (gate G-A).

## A2. What the shipping executable must be

- **Boot.**
  - *Today:* `src/platform/xbox360/main_xbox360.cpp:11-18` hard-codes `command="play"`,
    240 frames, scripted input, mute and screenshots.
  - *A shipping entry point:*
    1. validates `GameData` (A5.5);
    2. loads the metadata and bytecode package;
    3. starts the original first build scene;
    4. runs an unbounded, vsync-paced frame loop until the original game quits.
  - Missing or mismatched data produces an on-screen error, never a silent fallback.
- **Behaviour.** The original IL drives the game through the original menus and flows:
  - title, slot select, options and remapping;
  - world maps, shops and levels;
  - death, retry, results and progression;
  - DLC entry, Chalice and **two-player local co-op**.

  No harness callbacks, teleports or scripted input may be involved.
- **Players and pads.** Cuphead has **two** game players (`PlayerId.PlayerOne` and
  `PlayerTwo`). Scanning all four physical controller slots so that either player can
  join from any pad is sensible. There is **no four-player co-op**, and none may be
  invented.
- **Platform differences.** These are documented, not hidden, and are confined to the
  external boundary:
  - **GOG Galaxy achievements and online stats:** only the calls into the external
    Galaxy SDK are replaced, with logged results that follow the original code's own
    offline or unavailable path.
    - The original IL around those calls still runs: local stat bookkeeping, callbacks,
      events and return values that gameplay or progression consumes.
    - Gameplay-affecting methods and storage callbacks are **never** turned into blanket
      success or no-op paths.
    - The current shortcuts violate this (A11 F11).
  - **Storage:** cloud storage becomes port-local storage, with the original
    load/save/callback sequencing preserved (A5 item 11).
  - **PC-only options:** display, resolution and window options are fixed to 720p.
  - **Input devices:** there is no keyboard or mouse.

  Everything else that differs is a defect.

## A3. Execution strategy (preference on current evidence)

**Preference:** keep original-IL execution, and distribute it as data produced by the
user's converter. Make it fast with converter-produced predecoded bytecode plus bound
native services, not with translated game code shipped in the XEX.

**Why, on engineering evidence and current scope:**
1. **Repository boundary.** The repository ships tools and engine code, never game code
   or data (`README.md`). A XEX with translated game code compiled in would cross that
   boundary. This is the project's own distribution rule; **this review draws no legal
   conclusion**.
2. **Converter goal.** Any path that needs every user to install the Xbox 360 XDK
   contradicts the converter goal. A path that runs on converter output does not.
3. **Existing work.** The interpreter already runs original startup, movement, damage
   and coroutine paths, with a strict fault ledger and fixture oracles (CP1, D1). Its
   remaining gaps are enumerated rather than unknown.

**Alternatives that were not evaluated (unverified, deferred on scope, not ruled out):**
- **A managed runtime or interpreter from another project.** Its licence, PPC
  big-endian support, Xbox 360 toolchain fit and Unity 2017-era profile compatibility
  are all unverified, and the Unity service layer would still be needed.
- **Optional user-generated native code** for hot paths, built by a user who has the XDK
  from their own files. This may be useful for developers or power users later, but it
  must not be required.
- **Mono variants.** Some Mono versions include an interpreter mode, so a Mono-based path
  need not imply JIT or full AOT. Whether any suitable version builds for Xbox 360 PPC
  with this toolchain, and its cost, are unverified. It would still need the whole Unity
  service layer and content pipeline.
- **A clean-room reimplementation of game logic.** Parity would be much harder to
  establish, because every gameplay rule would need its own oracle.

The preference should be revisited if gate G-B fails after P1–P2, or if the generic-types
gap (A11 F1) proves too large for the interpreter model.

**Correction.** The previous revision cited "no user-defined generics"
(`docs/REVERSE_ENGINEERING.md:33-34, 143-144`) as a reason for this strategy. **That
claim is false.** The game defines generic types such as `AbstractLevelProperties`3`,
`AbstractLevelState`2` and `PlayerEvent`1`, and the runtime intercepts them by name
(A11 F1).

**Current cost structure that must change** (source evidence):
- `Interpreter::Execute` decodes raw IL every time. Every call `MemAlloc`s its argument
  and local arrays, and copies the 32-entry generic argument array twice.
- Native and Unity calls are matched through `strcmp` chains on every call. There are
  595 `strcmp` sites in `src`; `native.cpp` scans its `kEntries` table, and
  `unity_bind.cpp` dispatches through `InvokeUnityNative`. Game-method overrides are
  also matched by name on every invoke (A11 F11).
- `Value` is a 16-byte tagged cell, with a 16-byte object header and no field packing.
  Every Vector2, Vector3 or struct copy is a tracked heap allocation that is never
  freed (A11 F5).
- `IsLiveObject` is linear over all allocations (`interpreter.cpp:700`).
- `Scene::ComputeWorldTransforms` is O(N²×8) (`scene.cpp:325`) and runs on **every**
  transform setter (A11 F6).
- The log flushes to disk on every line (`log.cpp:30-39`).
- D1 r2 classifies and caches interface calls with linear scans; this is being corrected
  (A8 step 0).

**Measured warning signs** (host measurements on Windows, not a console profile): Root
Pack runs at about 0.8 FPS with a working set of about 1.5 GiB, and the software
renderer draws from 1.42 GiB of eager RGBA. These numbers mix:
- software rasterisation;
- eager texture I/O;
- interpreter, transform and logging costs.

**Order of performance work, decided by measurement:**
- **P0 (E0 instrumentation)**, specified in A7.
- **P1:** bind each MemberRef to its native function **once** at load (a function
  pointer table keyed by MemberRef id), removing per-call `strcmp`. This is low semantic
  risk.
- **P2:** converter-generated predecoded bytecode (`CHBC`), with:
  - resolved tokens and branch and call targets;
  - typed opcodes for common stack shapes;
  - inline caches;
  - frame arenas instead of per-call `MemAlloc`.

  IL semantics remain the definition; P2 is differentially tested against the current
  interpreter on the fixtures and on original methods.
- **P3:** native implementations of engine-adjacent hot paths that are Unity APIs
  anyway: Vector and Mathf, a transform hierarchy with parent indices and dirty flags,
  animation sampling and collision queries. Game logic stays IL.
- **P4 (only if G-B fails):** revisit the alternatives above and typed object layouts.
  There is no reflex rewrite, and no claim that P1–P3 will close the gap.

## A4. Content representation and packaging

The decisions below are **design proposals conditional on E0 and the probe XEX**. Each
names the measurement or check that confirms or overturns it.

### A4.1 Single logical archive, multiple bounded volumes

**Proposal:** one indexed logical archive:
- `GameData/cuphead.idx`: a small index, loaded whole;
- `GameData/cuphead.000 … .NNN`: data volumes, each below 2^31 bytes, 1 GiB by default.

**Reasons:**
- **File API.** The current file layer has `u32` sizes and no seek
  (`src/core/file.h:14-21`, `file.cpp:79-85`), and reads whole files
  (`file.cpp:101-125`).
- **Transfer media.** FAT32 USB media cannot hold files of 4 GiB or more.
- **Device limits.** The console storage devices' file-size limits are unverified (A9).
- **Robustness.** Per-volume integrity and re-conversion are easier with bounded volumes.

**Per-scene packs** would duplicate shared content (Player pages, HUD, fonts, common SFX)
or need cross-pack references anyway.

**Index contents** (little-endian, explicit readers):
- **Header:**
  - pack and converter versions;
  - target;
  - source identity: GOG build id `55543280706943035`, `bundleVersion` 1.3.2, and the
    SHA-256 of `Assembly-CSharp.dll` and of key data files;
  - runtime compatibility range.
- **Entries:**
  - dense `u32` id, type, flags and volume;
  - sector-aligned offset;
  - stored and logical sizes;
  - codec, CRC32 and a 128-bit content hash.
- **Deterministic ids:** logical keys (asset class, source serialized file, 64-bit path
  ID, sub-index) are sorted and then numbered densely. Identical payloads share storage
  but keep distinct logical entries.

  The current extractor truncates path IDs to 32 bits (`extract_scene.py:583`). The pack
  must keep all 64 bits.

**Residency groups:**
- per scene, derived from original scene dependencies **and** the game's own preload
  lists (`SceneLoader.load_cr` calls
  `AssetLoader<SpriteAtlas>/<AudioClip>.GetPreloadAssetNames`);
- a resident `core` group;
- volumes ordered by group for sequential reads.

**Integrity:**
- per-volume SHA-256 in the index;
- boot-time header, version and size checks;
- a per-entry CRC check on load;
- an optional full-verify mode.

**Runtime reader (new):**
- 64-bit offsets and `ReadAt`;
- asynchronous requests served by a streaming thread into pre-reserved slabs;
- completions applied on the main thread at frame boundaries.

The Xbox seek and overlapped-I/O APIs are to be verified (A9).

**Compression:**
- GPU textures are stored in their final GPU layout with no CPU decompression, once the
  layout is validated (A4.2);
- structured data uses a fast, permissively licensed LZ-class codec;
- audio follows A4.4.

### A4.2 Textures

The source atlases are BC7 4096² pages: 1,108 Texture2D objects in 5.22 GB of bundles,
with names such as `sactx-4096x4096-BC7-Player-…`. Xbox 360 has no BC7. The host
confirmed that:
- `d3d9types.h` declares `D3DFMT_DXT1/3/5` and linear variants;
- `xgraphics.h` declares `XGTileTextureLevel` and `XGSetTextureHeader`.

**Format choice per content class (proposal):**

| Content class | Candidate format | Condition |
| --- | --- | --- |
| Character, boss and FX atlases (anti-aliased alpha) | DXT5 | Default for pages with partial alpha. |
| Pages whose alpha is only fully opaque or fully transparent | DXT1 | **Alpha analysis establishes DXT1 *alpha eligibility* only.** It does not make RGB lossless. |
| Font/TMP SDF atlases and fine UI | 8-bit alpha or luminance, or uncompressed | DXT damages SDF and thin glyphs. Format availability is to be verified. |
| Full-screen overlays | As measured | Small count. |

**Quality is validated independently of memory arithmetic:**
- **Every BC7 → DXT1 or BC7 → DXT5 conversion loses colour detail.**
- The converter reports per-page error metrics against the decoded BC7 reference, plus
  a visual review set.
- Alpha edges and colour space are checked separately. The project's colour space and
  the original sampling (sRGB or linear) are recovered from the user's data, not
  assumed.
- Thresholds are set from E0 review, not from budget pressure.

**Blending and alpha representation follow the recovered recipes, not a global
assumption.**
- The current extractor records **no material or shader** for any SpriteRenderer
  (`extract_scene.py:1564-1589`). The previous revision's statement that Unity's default
  sprite shader blends `SrcAlpha`/`OneMinusSrcAlpha` was an unverified assumption and is
  withdrawn.
- Unity 2017.4's SL-Blend manual, cited by the host
  (<https://docs.unity3d.com/2017.4/Documentation/Manual/SL-Blend.html>), distinguishes
  traditional and premultiplied transparency. They are not interchangeable. It was not
  re-fetched for this review.
- **The converter records each renderer's material and shader**, including blend state,
  shader keywords and properties. For each recipe it chooses straight or premultiplied
  texel storage. Colour dilation into transparent texels applies only where the recipe
  samples straight alpha.
- The renderer implements exactly the recovered recipes.
- **Oracle fixture:** for each recovered recipe, render a fixed sprite set in the Windows
  oracle and compare against captures of the same set from the user's PC game, via the
  V1 trace mod or a capture scene.

**Mip-maps:** off by default for content drawn at about 1:1. Zoomed scenes are measured,
and mips become a per-page option.

**Layout, endianness and dimensions stay provisional until XDK and probe validation:**
- **Layout:** linear DXT (upload is a copy) is the first candidate. Tiled formats via
  `XGTileTextureLevel` are the alternative if probe or GPU profiling shows that linear
  is unsupported or too slow for the use. The converter must not need the XDK, so a
  tiled layout would be produced on the console at load or by a converter-side tiler
  validated against the probe.
- **Endianness:** either the GPU fetch endian setting or a converter pre-swap of DXT
  16-bit words, as determined with `XGSetTextureHeader` plus a probe.
- **Probe XEX:** records maximum dimensions, pitch and base alignment, and
  physical-allocation granularity for one 4096² DXT5 page that it creates, fills and
  samples.

**Texture budget arithmetic** (memory only; quality is separate):
- a 4096² DXT5 page is 16 MiB, so the eight Player pages are 128 MiB;
- whether a scene needs them all at once (Cuphead and Mugman in co-op, Chalice) is the
  largest unmeasured memory risk;
- E0 computes each scene's referenced page set from sprite render data, and its
  dependency sets per phase.

**Residency policy is chosen from measured dependency sets and latency.** There is no
blanket rule:
- prefer **preloading** where the scene's set fits;
- otherwise choose among:
  - **re-atlasing** each residency group's sprites into new pages, with remapped UVs
    and meshes and preserved sprite identity;
  - **prefetch** at original phase or transition boundaries;
  - **mid-fight streaming**, where measured latency and dependency timing make it safe.

The previous revision's ban on mid-fight streaming had no census behind it and is
withdrawn.

### A4.3 Sprites: atlas pages plus original meshes

**Proposal (supersedes the A1 crop plan as the shipping representation):** atlas pages
plus each sprite's original mesh.

Each sprite keeps:
- sprite-space vertex positions;
- atlas UVs, which already encode packing rotation and flip;
- indices;
- the full rect, pivot and pixels-per-unit;
- page identity.

This preserves original placement, trim and packing, keeps DXT blocks page-aligned and
allows batching by page.

**The current extractor gets this wrong in three ways:**
- it crops axis-aligned rects and ignores packing transforms and tight meshes
  (`extract_scene.py:192-196`);
- it keys sprites and textures by name (`extract_scene.py:197-225`);
- it writes raw RGBA plus PNG for every crop (`extract_scene.py:201-206`).

**Kept from A1 and folded into converter step R1:**
- the identity fixes: page key by (serialized file, path ID) and the full render-data
  key;
- the UV-origin and quarter-rotation fixtures;
- the hard-budget acquire/release contract, applied to GPU page residency with
  GPU-fence lifetimes.

The software renderer remains a Windows **oracle** and draws the same meshes.

### A4.4 Audio

There are 132 AudioClips and no playback today.
- **Runtime:** XAudio2 (`xaudio2.lib` is linked, `Cuphead360.props:40`). A native
  `AudioManager` implements the `AudioSource`/`AudioMixer` semantics that the census
  finds.
- **Codec:** XMA needs XDK tools, so the user converter cannot produce it. Instead it
  stores 16-bit PCM or a CPU-decodable ADPCM, chosen by measured disk and CPU cost.
  Which formats XAudio2 on 360 accepts is to be verified.
- **Music** streams from the pack; SFX stay resident within budget.

### A4.5 Other converted content

All with stable ids:
- the metadata/bytecode package;
- scene graphs with S1 object-presence records and validated byte consumption;
- Animator controllers, clips and events;
- colliders, including Edge and Polygon;
- Rewired user data;
- localization tables;
- TMP font assets;
- materials and shaders (A4.2);
- the preload lists.

The converter must **report** every component type, field kind and parse failure that it
cannot represent. Today these are dropped or truncated silently (A11 F9).

## A5. Engine and semantic gaps to full parity

Each item gives the current state (with references) and the proposed direction. A11 adds
the findings from the coverage pass.

1. **Lifecycle and identity.**
   - *Current:*
     - the harness hand-picks callbacks (`app.cpp` `RunLevelTest`);
     - update scripts that fail once are disabled permanently;
     - `Destroy` only deactivates;
     - `op_Equality` compares raw pointers, so there is no Unity fake-null
       (`unity_bind.cpp:3122`);
     - `GetInstanceID` returns the object index, so a component shares its GameObject's
       id (`unity_bind.cpp:2996`).
   - *Direction:*
     - a common player loop: FixedUpdate at the project's fixed timestep, then physics
       and triggers, Update, coroutines, animation, LateUpdate and the render snapshot;
     - deferred destroy and fake-null;
     - persistent roots;
     - stable object identity.
2. **Scene flow.** `SceneManager` and `DontDestroyOnLoad` are stubs.
   - *Direction:* an asynchronous pump driven by `SceneLoader.load_cr`. The harness's
     forced reset of `SceneLoader.currentlyLoading` (`app.cpp:2340-2346`) must not
     survive into product code.
3. **Serialization (S1).**
   - empty inline objects are dropped;
   - unsupported field kinds stop parsing silently (A11 F9);
   - decided in `opus-opening-prerequisite-decision.md`.
4. **CLR semantics.**
   - *Missing:* exceptions and `finally`; unsigned and unordered comparisons;
     null-target delegates treated as static; array runtime types; generic statics;
     **generic types** (A11 F1); integer divide-by-zero and `shr.un` (A11 F7); GC.
   - *GC:* there is no collector, and the per-value allocation leak (F5) makes this
     urgent. The direction is a precise, non-moving mark-sweep collector with registered
     roots, run at safe points. Gate G-C is a soak test with a flat heap.
5. **Native API coverage.** Many shims return plausible constants or silently drop
   behaviour; A11 F10 and F11 add examples. E0 builds a MemberRef coverage census that
   classes each call as implemented, UNSUPPORTED-recorded or silent. Silent stubs
   become ledger-visible.
6. **Animator.**
   - *Current:* default-state playback only.
     - Extraction keeps only the first blend-tree node's sprite (PPtr) curves and drops
       transitions, parameters, events and every float or transform curve
       (`extract_scene.py:1320-1421`).
     - The runtime caps a silent `Best best[128]` (`animation.cpp`).
     - `Animator.StringToHash` is FNV-1a (`unity_bind.cpp:1727`).
   - *Direction:*
     - converted controllers, curves and events resolved to MethodDefs;
     - `StringToHash` must reproduce the ids stored in the converted controller data. A
       self-checking oracle is available from the user's own files: the converter
       checks that hashing each `m_TOS` name reproduces its key.
7. **Physics.**
   - *Current:* AABB only, with circles treated as squares and rotation applied to
     offsets only (`app.cpp:2088-2112`). Layer masks are ignored. Only
     `OnTriggerEnter2D` is dispatched, from O(N²) pair tests with name heuristics
     (`app.cpp:1983-2146`). Edge and Polygon colliders are missing.
   - *Direction:* native 2D queries with filters, the layer collision matrix and
     trigger enter/stay/exit. Dynamics only if the census needs them.
8. **Rendering.**
   - *Current:* a software renderer without rotation or materials.
   - *Direction:* a GPU sprite renderer that:
     - draws original meshes with full 2D affine transforms;
     - sorts like Unity 2D;
     - batches by page and material;
     - implements the recovered shaders and camera effects (A4.2);
     - compiles HLSL into the XEX at developer build time.

     Whether the 720p target fits eDRAM is to be verified.
9. **UI, fonts and localization.** Canvas and RectTransform layout, sliced and filled
   images, masks, TMP SDF text, mesh text, language tables and per-language fonts.
10. **Input and co-op (two players).**
    - *Current:*
      - only pad 0 is read (`input.cpp:21`);
      - dash edge detection re-triggers while held (`input.cpp:27, 40`);
      - Rewired `GetAxis` ignores the player and returns the x value for axis 0 and the
        y value for every other axis (`native.cpp:1404`);
      - buttons are one shared bitmask.
    - *Direction:*
      - a Rewired shim driven by the original Rewired user data;
      - two game players joined or dropped through the original `PlayerManager` flow,
        from any of the four pads;
      - remapping persisted through storage;
      - vibration.
11. **Storage (S2).**
    - *Current:* PlayerPrefs is a 32-entry in-memory table (`native.cpp:1635-1712`).
      `JsonUtility` is UNSUPPORTED-recorded, `XmlSerializer.Deserialize` returns null and
      `StringReader.ReadToEnd` returns `""` (`native.cpp:1241, 1822-1824`).
    - *Direction:*
      - a port filesystem boundary for
        `Environment.GetFolderPath`/`persistentDataPath`, which FAULTs outside the port
        root;
      - XContent on Xbox (API to verify);
      - atomic writes with a backup copy;
      - a real `JsonUtility`;
      - the original load, save and callback sequencing, including failure callbacks.
        No blanket success.
12. **Platform services.** GOG isolation at the SDK boundary only (A2, A11 F11). There
    is no network.

## A5.5 Converter (user-facing)

- **Form:**
  - a frozen Windows executable: the Python pipeline with pinned UnityPy, Pillow and
    texture2ddecoder, plus a small native helper from this repository for DXT encoding
    and hashing;
  - no Python or SDK for users.
- **Flow:**
  1. select the GOG folder;
  2. validate identity (build id, `bundleVersion`, SHA-256 of `Assembly-CSharp.dll`,
     `globalgamemanagers` and the bundle manifest) against the supported list;
  3. check free space;
  4. convert into a staging directory with a content-addressed cache;
  5. write deterministic output;
  6. verify the output;
  7. rename atomically;
  8. write a report.
- **Version coupling must be removed or made explicit.** Today the extractor hard-codes
  build-specific values:
  - field ids 7163 and 7215–7222 (`extract_scene.py:1284`);
  - asset roots (`"sharedassets8.assets", 6208` at 1695, and prefab 5401 at 1129);
  - sprite names (1266, 1801).

  It also synthesises objects: level-bound colliders (1271-1317), a player (1703-1747)
  and ghost deactivation (1254-1268). Each must either derive from metadata or be keyed
  to the validated source identity, and the report must list synthesised content.
- **Errors** say what to do.
- **Bounds:** one 4096² page at a time (64 MiB decoded). The converter never writes into
  the game folder or reads saves.
- **Output size** is to be measured.
- **Deployment:** `default.xex` plus `GameData/` on USB FAT32 or by FTP. Every file stays
  below 4 GiB.

## A6. Budgets (proposals to validate, not proven allowances)

**Available memory is unmeasured.** COMPATIBILITY.md records that Xenia reported "~491
MB free physical" in an early probe.
- That figure comes from an emulator, and its unit (MB or MiB) is ambiguous.
- It is not a hardware title-memory figure, and it does not account for OS, GPU,
  transition or scratch peaks.

Available title memory and those peaks must be measured on hardware before any claim of
fit.

| Pool | Proposed cap |
| --- | ---: |
| XEX image, CRT, statics | 16 MiB |
| Metadata and bytecode resident | 32 MiB |
| Managed heap (GC, 16-byte Values) | 96 MiB peak |
| Native scene graph, animation, physics | 32 MiB |
| GPU textures, including transition overlap | 192 MiB |
| Audio (SFX plus stream buffers) | 36 MiB |
| I/O staging and decompression scratch | 16 MiB |
| Render targets, vertex and index buffers | 16 MiB |
| Reserve | 48 MiB |
| **Total** | **484 MiB** |

- The caps sum to 436 MiB; with the reserve, **484 MiB**. This is a planning envelope to
  compare against measured hardware availability, not a fit.
- The pools are not proven to be simultaneously available, and each is validated
  separately.
- Transitions load the incoming group while the outgoing group is still resident, unless
  an original blackout allows release first. The worst case is measured per transition.
- `mem.cpp` counts payload bytes of `MemAlloc` only (`mem.cpp:37-53`). It excludes
  headers, the CRT, D3D and XDK allocations, so `MemPeakBytes` is not a process peak.
  Hardware measurement uses system-level memory statistics as well.

**CPU at 60 Hz** (16.67 ms per frame):
- **Proposed split:**
  - the main thread runs IL and engine logic;
  - render submission runs from a per-frame snapshot on another hardware thread;
  - audio and streaming run on further threads.
- **Proposed budget:** about 10 ms of main-thread logic. This is a proposal to test
  against hardware (G-B), not a derived limit.
- Game logic stays single-threaded, as in Unity. The Xenon thread layout is to be
  verified.

**Input latency:** read input at the start of the frame. The target is no more frames of
latency than the GOG build, measured with on-screen frame counters.

## A7. Measurement, verification ladder and gates

**E0 instrumentation (P0).** It must separate cost classes and account for its own
overhead.
- **Cost classes reported per frame:**
  - managed interpretation (IL instructions by opcode class, calls, allocations);
  - native and Unity calls by member, with time;
  - transform recomputes, with count and time;
  - collision pairs;
  - software rasterisation;
  - texture and file I/O.

  Software rasterisation and texture I/O are reported separately from managed and native
  work and are excluded from the G-B figure.
- **Timers:** a high-resolution counter. `GetTickCount` (`platform_winapi.cpp`) has
  millisecond resolution and is not adequate.
- **Overhead:**
  - measured as counters-off versus counters-on runs of the same replay;
  - logging is disabled or buffered during timed runs, because per-line flushing
    (`log.cpp:30-39`) dominates otherwise.
- **Scenes:** representative ones, including the heaviest DLC scenes (Saltbaker has over
  2,000 objects).

**Gate G-B (CPU feasibility).** A dispatch microbenchmark multiplied by an instruction
count is **not** a frame-time prediction. Operand mix, call depth, native cost, cache
behaviour and allocation all matter. G-B is decided by:
1. a **representative weighted trace or replay, or a bounded real workload, on hardware**:
   - the actual interpreter and native layer running recorded inputs through a heavy
     scene's managed and native work for a fixed number of frames;
   - rendering is either disabled or measured separately;
   - it reports the per-frame distribution (median, p95, p99), not only the mean;
2. the Windows E0 breakdown, used to explain the hardware figure and target P1–P3;
3. a microbenchmark, as one input only.

**Go** if the hardware workload's main-thread logic stays within the proposed budget at
p95 with the intended optimisations applied, or with a credible measured path to it.
**Otherwise** stop breadth work and revisit A3.

**Ladder:**

| Level | Gate |
| --- | --- |
| V0 | Converter determinism, synthetic format tests and supported-version validation. |
| V1 | **Windows oracles:** isolated .NET semantic fixtures; recovered-recipe render fixtures (A4.2); a **test-only trace mod** for the user's PC game that records per-frame state under recorded input, used to diff against port traces and to check Unity behaviours (Random, rounding, StringToHash, blending). Development use only, never shipped. |
| V2 | Windows full routes: the opening, then Root Pack with death, retry, victory and progression, then every scene. |
| V3 | XEX on Xenia: functional parity only, never timing. |
| V4 | **Hardware:** boot, memory map, frame-time distributions in the worst scenes, transition latency, audio continuity. |
| V5 | Content matrix: 79 scenes, base and DLC, difficulties, two-player co-op, Chalice, shops, King Dice, the chess gauntlet, endings. |
| V6 | Soak and stress: multi-hour sessions with a flat heap; power loss during save; controller hot-plug. |
| V7 | Release candidate from the user instructions on a clean PC and console. |

**Early gates:**
- **G-A (memory):** the measured worst-case texture working set, plus transition
  overlap, fits the measured available memory under the chosen residency policy.
- **G-B (CPU):** above.
- **G-C (GC):** a flat heap in a soak test.

M1 (the opening flow) is one gate inside V2/V4, not release.

## A8. Ordered path

0. **D1 correction r4** (`work/restart-20261031/opus-d1-review-request.json`, amended
   from r3 because of A11 F1):
   - the interface set and hashed caches;
   - R6 for MemberRefs;
   - the modifier bound;
   - the **generic-base fail-closed bound**;
   - docs.
1. **E0 measurement** (the next feature checkpoint; read-only on the game):
   - **(a) content census:**
     - per-scene and per-phase sprite and page sets, with DXT size projections;
     - audio sizes;
     - materials and shaders per renderer;
     - fonts, collider and body types;
     - Animator features and mixer effects;
     - preload lists;
     - component types and field kinds that the extractor drops (F9);
   - **(b) MemberRef coverage census**, including TypeSpec-parent MemberRefs into
     game-defined generic types and the members currently intercepted natively (F1);
   - **(c) runtime instrumentation** as specified in A7, plus counts of Instantiate
     reuse versus clone decisions (F3) and allocation growth per frame (F5);
   - **(d) a bounded hardware workload mode** for G-B, run when hardware access exists.
2. **P1:** native binding table.
3. **G1: game-defined generic types**, sized by the census (b). A converter and runtime
   checkpoint, possibly split:
   - resolve TypeSpec-parent MemberRefs to the definition's MethodDef with instantiation
     context;
   - give instantiations base and interface lists;
   - vtables through generic bases;
   - per-instantiation statics;
   - remove the name-based intercepts and field lookups (F1).
4. **R1:** converter pack v1. Index, volumes and reader; page plus mesh sprites with
   identity fixes; material and shader capture; the string-table fix (F8); a reporting
   ledger; the Windows mesh oracle.
5. **R2:** Xbox GPU renderer and the probe XEX for formats, layout and alignment.
6. **Semantic and runtime checkpoints:**
   - **S1** serialization;
   - **CP2a** lifecycle loop, including the Instantiate and clone fixes (F3, F4);
   - **CP2b** exceptions;
   - **CP2c** semantics, including null-target delegates and the F7 arithmetic;
   - **GC**.
7. **Systems:** **CP3** scene pump; **CP4a** Animator; physics as the census dictates;
   UI, TMP and localization; audio; input and two-player co-op; **S2** storage.
8. **P2** bytecode, if G-B needs it.
9. **M1** opening flow (Windows, then hardware); Root Pack complete.
10. **Content breadth**, V5 to V7, release converter.

The total work size is **unknown**. Each step is one bounded worker assignment,
Opus-reviewed and Sol-checked, with both builds and the relevant regressions. Step sizes
are estimated only after E0.

## A9. Facts requiring verification

**SDK, documentation or probe XEX** (none are asserted in this review):
- Xenos maximum texture dimension, DXT linear and tiled pitch and base alignment,
  physical-allocation API and granularity.
- `XGSetTextureHeader` endian handling.
- eDRAM size and its 720p formats.
- XAudio2 (360) source formats.
- XContent save-device APIs.
- File seek and overlapped I/O APIs.
- Storage-device maximum file sizes.
- Available title memory and the OS/GPU reservation on hardware.
- Hardware-thread layout.
- Whether any Mono or third-party runtime candidate builds for this target (A3).

**Unity 2017.4 behaviours to verify with the V1 oracle**, not from memory:
- the `Random` algorithm and integer `Range` distribution;
- `Mathf.Round` and `Math.Round` midpoint behaviour;
- `Mathf.Approximately` tolerance;
- the `Animator.StringToHash` function (also self-checkable from `m_TOS`, A5 item 6);
- `deltaTime` under `timeScale`;
- `FindObjectsOfType` treatment of inactive objects;
- per-recipe blend and alpha representation.

## A10. Alternatives not taken now (with reasons)

- **A shipped XEX containing translated game code:** crosses the repository's
  distribution rule (A3). No legal conclusion is drawn.
- **Required user-side XDK steps:** contradict the converter goal. An optional developer
  path remains open.
- **Mono, a third-party runtime or a clean-room reimplementation:** unverified for this
  target and scope (A3). Deferred, not ruled out.
- **One giant pack file:** `u32` file API, FAT32 transfer and unverified device limits.
- **Per-scene packs:** duplication.
- **Crop reconstruction as the shipping sprite representation:** breaks DXT blocks,
  duplicates pixels, prevents batching, and today ignores packing transforms.
- **XMA in the converter:** needs the XDK.
- **Tuning the software renderer for Xbox:** wrong architecture; it stays an oracle.

The previous revision listed "streaming atlas pages mid-fight" here. That entry is
withdrawn; it is now a measured choice (A4.2).

## A11. Findings from the coverage pass

These are new, or newly evidenced, consequential findings. They come from code reading
(not execution), and each cites its source. "Consequence" states the effect when the path
is reached; how often each path is reached is for E0 to measure.

Independent host checks confirmed the generic-instantiation/export and native
substitution paths, the Instantiate table-scan rule, the void logic-driver return,
and both four-byte string-pool sentinels. A read-only census of the original
Assembly-CSharp.dll GenericParam owner table found **27 generic type definitions
and 74 generic method definitions**. This corrects REVERSE_ENGINEERING.md; it
does not imply that every generic method takes the same broken path. MethodSpec
and instantiation-context coverage must be measured alongside TypeSpec calls.
Private census: work/restart-20261031/sol-original-generics-census.json.

| # | Finding | Source | Consequence and direction |
| --- | --- | --- | --- |
| F1 | **Game-defined generic types do not execute as IL.** The converter emits each closed instantiation as a composite type with `external=True`, `base_id=0` and no interfaces. A MemberRef whose parent is such a TypeSpec is never resolved to a MethodDef, so calls go native. Vtables restart at slot 0 under such a base. Inherited generic fields are found by **name** on every access. `native.cpp` hand-writes substitutes: the `AbstractLevelProperties`3` constructor (setting TotalHealth, CurrentHealth, goal times and states natively), `get_CurrentState`, and `add_`/`remove_` events, which are void, so subscriptions are dropped. The same applies to `AbstractLevelState`2`. `REVERSE_ENGINEERING.md:33-34, 143-144` says there are no game generic types; that is false. | `metadata_lib.py:576-602, 1122-1166, 1203-1251`; `interpreter.cpp:958-1006, 2094-2100`; `native.cpp:1899-1940` | **Boss health, state and phase setup in every level runs partly through native substitutes**, not original code. Interface dispatch through such bases is affected too: `FindInterfaceOrigin` stops at the composite base (`interpreter.cpp:2165-2174`), and `HasExternalNonObjectBase` calls it external (`2422-2435`). That is why D1 r4 adds C5. The structural fix is G1 (A8). |
| F2 | The converter rewrites tokens, including MemberRef → MethodDef/FieldDef, TypeRef/TypeSpec → synthetic TypeDef ids, and VAR/MVAR `ldtoken` markers. Its docstrings nevertheless say the IL is preserved "exactly" or "byte-for-byte". | `build_metadata.py:4-5, 188-234`; `metadata_lib.py:8-9` | Opcode streams and branch offsets are preserved; token bytes are not. Documentation must say "token-rewritten IL". |
| F3 | **`Instantiate` reuse rule.** If any object from the template's table index to the **end of the scene** has a `LevelPlayerController`, the template itself is activated and re-Awoken in place instead of being cloned. | `unity_bind.cpp:2285-2314` | The rule is unrelated to the template being instantiated. Spawned projectiles, effects and enemies can become the template itself, depending on table order. Fix in CP2a. E0 counts reuse versus clone decisions. |
| F4 | `CloneGameObject` shallow-copies reference fields (Unity deep-copies serialized objects and arrays). It hard-codes repairs of `LevelPlayerController` `_motor` and related fields, sets `BasicProjectile.move=true`, runs Awake only and ignores the rotation argument. | `unity_bind.cpp:2168, 2240` and the surrounding clone code | Clones share mutable state, and game-specific patches stand in for semantics. Fix in CP2a. |
| F5 | Every Vector2/Vector3 creation and struct `CopyValue` is a `MemAlloc` tracked in `objects_` with no collector. `IsLiveObject` is linear over `objects_`. | `interpreter.cpp:542-560, 700, 841-849` | Memory grows every frame, and liveness checks get slower over a session. GC (and value-type storage without heap cells) is a prerequisite for G-C. |
| F6 | Every transform setter calls `Scene::ComputeWorldTransforms` (O(N²×8)), sometimes twice per call. | `scene.cpp:325`; `unity_bind.cpp:530, 578, 945-1072, 1241, 2266, 2336, 2396` | Probably a dominant CPU cost in object-heavy scenes; E0 to confirm. P3 replaces it with parent indices and dirty flags. |
| F7 | **Integer divide-by-zero** sets the divisor to 1, so it returns the dividend; the warning says it is "yielding 0". The CLR throws. **`shr.un` on a negative I4** shifts the sign-extended 64-bit value, so `-1 >>> 1` yields `-1` instead of `0x7FFFFFFF`. | `interpreter.cpp:3042-3047, 3066-3071` | Silent numeric divergence. CP2c fix with fixtures. The divide case must FAULT or throw once exceptions exist. |
| F8 | **Scene and animation string pools start with the 4-byte literal `\x00`** (`b"\\x00"`), not a NUL byte. Offset 0, which every empty string maps to, therefore reads as `\x00` followed by the first interned string. | `extract_scene.py:253, 1425`; `scene.cpp:48-49` | Every empty controller, name or file field reads as a non-empty garbage string. Name heuristics (`strstr(ctrl, "Projectile")`, `"Player"`) and tag fallbacks can match falsely. Fix in R1 with a converter test. |
| F9 | **Serialized-field parsing stops silently** at the first unsupported field: I1/U1/I2/U2/CHAR/I8/U8/R8, or unknown value types. Later fields keep their defaults. Color, Quaternion, Vector4 and Rect are skipped without values. Elements of arrays and lists of `[Serializable]` classes are parsed as 12-byte PPtrs. Unsupported component types are dropped, and parse exceptions are printed and skipped. | `extract_scene.py:545-645, 677-688, 374-375, 1626-1627, 1657-1658` | Wrong initial state with no record. S1 must validate byte consumption per field and report every unsupported kind. |
| F10 | **Unity and BCL parity:** | | All are V1-oracle items (A9) and need fixtures before they are relied on. |
| | – `Mathf.Round`/`Math.Round` use `floor(x+0.5)`, while .NET and Unity document midpoint-to-even. | `native.cpp:122` | |
| | – `Approximately` uses an absolute 1e-6 tolerance. | `native.cpp:90` | |
| | – `List.Sort` is a stable insertion sort, while .NET's is unstable, so equal-key orders differ. | `native.cpp:662` | |
| | – `ToUpper`/`IsLetter` handle ASCII only. | `native.cpp:256, 344` | |
| | – `Random` is an LCG, with modulo for integer `Range`. | `unity_bind.cpp:1666` | |
| | – `deltaTime` ignores `timeScale` and equals the unscaled value. | `unity_bind.cpp:3268` | |
| | – `Vector3.Lerp` is unclamped. | `unity_bind.cpp:2720` | |
| | – `get_rotation` is identity and `set_rotation` a no-op. | `unity_bind.cpp:3197-3198` | |
| | – `CompareTag` also matches substrings of object or controller names. | `unity_bind.cpp:597-660` | |
| | – `String.Concat` with three arguments dereferences a null `MemberRefInfo`, which is undefined behaviour. | `native.cpp:221` | |
| F11 | **Game-method overrides by name on every invoke:** `OnlineInterfaceGog.SyncAchievementsAndStats` is skipped entirely, and on an argument-count mismatch it is run with a synthesised null `this`. `Trilean` operators and `AbstractMonoBehaviour.get_transform` are native. `OnDamageTaken` is matched by `strcmp` on every delegate invoke. The native table also maps game classes such as `OnlineInterfaceGog.UnlockAchievement` to `Native_Void`. `GCFreePredicateList` calls return void, 0 or false. | `interpreter.cpp:780, 2011-2075`; `native.cpp:1852-1853, 2056` | Skipping whole IL methods can skip local bookkeeping, contrary to A2. The null-`this` path should become unnecessary once D1's mapping is complete; remove it in a later cleanup with a regression check, not in D1. Replace with SDK-boundary stubs. |
| F12 | **Xbox default path is scaffold:** `main_xbox360.cpp` runs `play` → `RunPlay` → `PlayerSlice`, whose run, jump, dash, gravity and ground are native constants. `--command logic` exits 0 even on FAIL, because `RunLogicTest` returns void. The stack harness hard-codes object ids, synthesises Rewired players and selects animation frames by hand. | `main_xbox360.cpp:11-18`; `player_slice.cpp`; `app.cpp:3454, 3796, 1371-1745` | Xbox runs are not evidence of original gameplay, and "logic PASS" exit codes are not evidence at all. Make the exit code reflect the result before any further use. |
| F13 | **Timing and measurement hazards:** a per-line log flush; `GetTickCount` resolution; `MemPeakBytes` counting payload only; the Win32 project setting no `/arch`, so VS2010 x86 uses x87 floating point. | `log.cpp:30-39`; `platform_winapi.cpp`; `mem.cpp:37-53`; `projects/win32/Cuphead360.vcxproj` | E0 timing must control for all of these (A7). The Windows oracle's float behaviour differs from both Unity x86-64 and Xenon. Numeric trace diffs need tolerances, or the arch should be pinned and documented. |

## A12. Corrections in this revision

| Previous claim | Correction |
| --- | --- |
| Inventory "66 tracked files" | 76 paths; coverage is stated per path (A0). |
| Interpreter is the "only distributable route"; Mono "needs JIT or full AOT"; "multi-year scope" | A practical preference with stated reasons. Alternatives are unverified and deferred on scope. No legal conclusion; no time estimate (A1, A3, A8, A10). |
| "No user-defined generics" | False (A3, A11 F1). |
| Straight alpha `SrcAlpha`/`OneMinusSrcAlpha` "as Unity's default sprite shader does" | Withdrawn. Recipes are recovered per material and shader and reproduced against an oracle fixture (A4.2). |
| DXT1 "only when alpha analysis proves it lossless" | Alpha analysis proves DXT1 alpha eligibility only. BC7 → DXT conversion can lose colour; quality is validated separately from memory (A4.2). |
| Linear DXT and endian handling stated as decisions; no streaming mid-fight | Provisional until XDK and probe validation. Residency is chosen from measured dependency sets and latency (A4.2). |
| G-B as microbenchmark × instruction count | A hardware workload or replay decides; the microbenchmark is one input; E0 separates cost classes and measures its own overhead (A7). |
| Budget table without a total, beside "about 491 MB" | Caps plus reserve total 484 MiB, as a proposal. 491 MB is an ambiguous Xenia free-memory report, not hardware availability (A6). |
| "Four pads with join and drop" | Two game players; four physical slots scanned (A2, A5 item 10). |
| "Achievements and stats become logged no-ops" | Stubs only at the external SDK boundary; original local and callback semantics preserved (A2, A11 F11). |

---

# Part B — Historical: fundamental review (2026-10-31)

*Historical record. Superseded by Part A where they conflict, notably the A1 crop plan
and the plan ordering.*

This review audits source checkpoint `beb3a87`. It supersedes architectural and
roadmap claims that describe planned facilities as implemented. It is not
acceptance of the playable opening flow. The implementation checkpoints below
must supply their own evidence in [VERIFICATION.md](VERIFICATION.md).

The requested roles are retained: Sol is the host and independent second lead;
Opus 5.5 is the external project lead; `crack_builder` is the DeepSeek V4.1 Flash
implementation/test worker at max effort. The adapter completed the initial
Opus audit on the exact `claude-opus-5-5` subscription route with no fallback,
no API-key source, and `not-billed-via-api` usage provenance. High effort was
requested through the supported process-local setting; the stream does not
expose an observed effort value. The user authorized Max quota only, with zero
API spend. The native worker's turn_context records the exact DeepSeek model
and max effort. Its exact-thread router_upstream activity reports successful
opencode-go requests on that model, corroborating the configured router route.
The session header separately says openai; that metadata discrepancy is retained
in the local readiness evidence rather than treated as a provider switch.
Host, Opus runs and the native worker's exact log are registered in the existing
activity panel. No role, model or provider was substituted. Readiness is not
source acceptance.

## Strategy decision

Retain original IL execution and the native Unity service layer for a bounded
pilot. Original startup, inventory, movement and damage paths provide useful
evidence that this approach can execute recovered game logic. They do not
establish CLR correctness, original-game fidelity, console performance or the
viability of the complete port.

Replacing the interpreter with Mono/AOT would still leave the Unity service,
content and presentation work. An immediate whole-engine rewrite has no
measured advantage. Reconsider the managed execution strategy if a small
correctness pilot cannot support the opening's actual call paths. Hot-path
translation is a future option, not an implemented escape hatch or a substitute
for correct semantics.

The current interpreter decodes IL during execution. Predecoding, collection,
asset streaming, a GPU renderer and production texture packing remain planned.
The existing scene/play harness is useful for regressions; a common lifecycle
and scene loop must replace its hand-selected gameplay callbacks.

## Findings and priorities

| Priority | Finding | Consequence and decision |
| --- | --- | --- |
| Critical | StartCoroutine returns null; no coroutine owner or join handle; Stop APIs are no-ops. | Repair handles, waits, owner lifetime and observable failures first. Preserve the distinction between an independently started coroutine and a raw nested iterator. |
| Critical | The scheduler silently disables all VeggiesLevel coroutines. | Root Pack spawn/phase/win code cannot execute. Remove the filter with diagnostics; diagnose remaining failures rather than replacing the filter. |
| Critical | Scene loads are no-ops or unsupported; active scene is manufactured; persistence is a no-op. | Build an original-request-driven transition pump, stable object identity, persistent roots, teardown and fresh lifecycle. A matching scene name does not prove retry. |
| Critical | Animator parameters, transitions and events are absent. | Potato Shoot and death events cannot fire. Test event-driven spawning and phase changes after adding the consuming runtime and extracted data. Do not inject the missing event as gameplay evidence. |
| High | Original callback failures can coexist with exit 0/PASS. | Add strict fault accounting, caller context and negative tests. Keep legacy smoke-test scope explicit. |
| High | throw/rethrow fail calls, leave skips finally, and endfinally faults; array runtime types, generic statics/events and iterator interface dispatch also have gaps. | Repair through separate, independently testable semantic checkpoints. Executing original bytes alone does not preserve their meaning. |
| High | Destroy mainly deactivates objects; component destruction, lifecycle and coroutine cancellation are incomplete. | Establish a common lifecycle before relying on scene reload or persistent state. |
| High | UI/TMP rendering and the real Dialoguer graph are absent. | Recover required data and the actual title/slot/house/map/game-over/results presentation. Native HUD rectangles are limited engine evidence. |
| High | JsonUtility produces an empty object; persistentDataPath and saves are stubbed. | Use a port-local store and original progression logic. Distinguish in-memory return to map from persistence after process restart. |
| High | Eager raw textures exceed console memory; CPU/frame cost is unmeasured. | Bounded residency and measured peaks are prerequisites for console gameplay. Profile before selecting acceleration work. |

The paused-handoff defects remain explicit audit inputs: unordered floating
branches and integer unsigned width/sign handling, array GetType,
PlayerEvent.Shared caching/arguments, generic
enumerator dispatch, retry propagation, synthetic input and broken rendering.
The original weapon inventory already contains peashot. Elder Kettle's message
drives the authored tutorial/power-up sequence; tests must not invent a new
inventory grant.

## Original route and content dependencies

Source tracing establishes the intended calls, not an executed route:

1. StartScreen confirmation requests slot selection. The new-game branch of
   SlotSelectScreen configures Cutscene.Load with the intro and house target.
   Cutscene.Skip uses the original configured destination.
2. HouseLevel/HouseElderKettle consume the recovered dialogue graph and its
   variables/messages. HouseLevelExit waits, then requests Isle I.
3. MapPlayerMotor uses two-axis Rigidbody2D velocity and LateUpdate ladder
   clamps. MapLevelLoader records return positions, opens the difficulty UI,
   and calls SceneLoader.LoadLevel after confirmation.
4. VeggiesLevel runs its original potato/onion/carrot/win coroutine chain.
   Potato attacks and death require Animator events. Difficulty matters.
5. LevelGameOverGUI.Retry calls SceneLoader.ReloadLevel. WinScreen returns
   through LoadLastMap. These must reach a real scene pump with progression
   retained; direct boots and harness reruns cannot demonstrate them.

Read-only inspection of original scene files found 173 SpriteRenderers and
40 EdgeCollider2D components in Isle I. The current extractor omits edge
colliders. Title/slot/house/results also depend on Canvas/text presentation;
results includes mesh-based text. These counts exclude external prefab graphs.

## Measured limits and baseline

The current prepared tutorial has 3,874 RGBA texture files totaling
989,522,484 bytes (943.68 MiB). Root Pack has 5,955 totaling 1,490,179,264 bytes
(1,421.15 MiB). Scene header texture counts match these file counts, and
Scene.LoadTextures retains a FileReadAll allocation for each. These file totals
are not a measured process peak, but the eager path already exceeds the entire
Xbox 360 512 MiB shared budget before other allocations. Disk deduplication or
optional PNG previews alone will not solve residency.

The Windows logic command was rerun against `beb3a87`: exit 0, logic PASS,
allocator peak 17,117,059 bytes. Its existing cases cover ease/inventory/velocity,
not the disputed semantics or gameplay. Tutorial and Root Pack boot commands
both exited 0/PASS despite 21 ERROR lines each, including failed constructors
and cloned Options UI Awake. Their approximately 21 MiB allocator peaks exclude
texture loading. Old health captures visibly expose packed character poses;
they establish changing HUD rectangles, not correct scene rendering.

Local audit artifacts are under `work/restart-20261031/`: the full Opus review,
Sol's independent findings, original-scene type inventory, texture inventory,
baseline logs, and exact adapter records. They are deliberately uncommitted.

## Runnable sequence (historical; superseded by Part A §A8)

Continuation decision: read-only asset preflight found eight distinct Player
atlas pages sharing one texture name, which the current cache treats as one
identity. All 2,392 entries are tightly packed; 356 require packing transforms.
Final CP1 review also identified inherited interface-slot misdispatch: the
original OnGrounded `SetStat` call can select `UnlockAchievement` because
interface and class slots share a numeric value but different meanings.
Matching arity can conceal other wrong-method calls. Opus therefore places a
small interface-dispatch checkpoint (D1) after accepted CP1, followed by the
bounded sprite-correctness/residency checkpoint (A1), before the broader
lifecycle work below. A1's contract
must enforce allocation limits and preserve sprite placement; it is planned,
not implemented or accepted. See ASSET_PIPELINE.md for measured defects.

After accepted CP1, a read-only package probe attributed Root Pack's frame-118
fault to `Level.intro.readyComplete`: the serialized `intro` object is null.
Its class has no serialized leaf fields, the extractor emits no object-presence
record, and the binder creates nested class objects only while applying child
values. The prepared scene has no `intro` record or children. A later generic
serialization repair must preserve empty inline objects; creating one will
still leave the real intro animation/callback gate to implement. Do not force
`readyComplete` or treat this as an Animator-only fault.

The lead confirmed this finding and places a bounded S1 serialized-object
graph checkpoint after A1 and before the common lifecycle work. S1 must validate
type eligibility, byte consumption and object presence, including collections;
it must not manufacture intro completion. A separate S2 port-local filesystem
boundary follows the required semantic repairs and precedes slot-screen runs.

The project lead issues one bounded implementation brief at a time, followed
by Opus review and an independent Sol check:

1. Coroutine handles/waits/cancellation and strict fault diagnostics; remove
   the name filter without disguising missing Animator behavior.
2. Common lifecycle/player loop, followed by separate runtime-semantics fixes
   where required by actual paths. Avoid combining every CLR defect in one task.
3. Scene registry and asynchronous transition pump with persistent roots,
   original requests, teardown, new-instance and stale-coroutine assertions.
4. Required animation state/events, sprite correctness, bounded texture
   residency, and measured frame/heap/texture counters.
5. Opening dialogue, minimum UI/text, input and authored map geometry; run
   normal new game through the visible controllable map. Port-local storage
   initialization/load/save callbacks are already needed here: SlotSelectScreen
   calls PlayerData.Init, which initializes storage and loads/creates slots.
   Persistence cannot be postponed entirely until the victory checkpoint.
   The original GOG SavePath uses the installed game's AppData Cuphead directory
   on WindowsPlayer, which is also the platform currently reported by the shim.
   Install and test a port-local path/interface override **before enabling any
   original storage calls**; prove that the installed save directory is neither
   read nor written.
6. Enter Root Pack from the map; authored damage, invulnerability, death and
   original retry; original victory/results/map return and port save persistence.

Every significant shared-code checkpoint builds Windows and Xbox Release and
retains appropriate tutorial/platforming/prepared-boss regressions. Windows
execution, XEX compilation, Xenia execution and hardware results remain separate
evidence classes. Explicitly muted runs are permitted; absent audio remains a
gap. The opening milestone stays incomplete until the entire actual route and
its visible, interactive outcomes are recorded.
