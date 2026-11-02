# Console plan

What the shipping Xbox 360 build must be, how content gets there, and the
gates that decide it. This keeps the parts of the 2026-10-31/11-01 port
review that still apply after the move to the managed player
([MANAGED_PLAYER.md](MANAGED_PLAYER.md)); the review itself, which was mostly
about the retired IL interpreter, is in git history (`docs/PORT_REVIEW.md`
before the round-3 cleanup).

Everything here is a proposal until measured. Each item names the check that
confirms or overturns it.

Round 6 (2026-11-01) built the first console version on xSDK +
vulkan4xenon directly (`console/README.md`): no XDK build and no Xenia
step, since Xenia can't run xSDK titles and the user verifies behaviour with
the shared Windows build. The content is the converted files as they are
(objects, `ctex` DXT1/DXT5/BGRA8 pages halved to 2048, Ogg audio), not yet
the single archive of 2.1.

## 1. The shipping executable

- **Boot.** Validate `GameData` (converter identity and version), start the
  original first build scene (`scene_start`), run an unbounded vsync-paced
  frame loop until the original game quits. Missing or mismatched data shows an
  on-screen error, never a silent fallback.
- **Behaviour.** The original code drives everything through the original
  menus and flows: title, slot select, options and remapping, world maps,
  shops, levels, death/retry/results, progression, DLC, Chalice and two-player
  local co-op. No harness callbacks, teleports or scripted input.
- **Players and pads.** Cuphead has two players (`PlayerId.PlayerOne`,
  `PlayerTwo`). Either may join from any of the four pad slots. No invented
  four-player mode.
- **Platform differences**, documented and confined to the boundary:
  - GOG Galaxy: only the SDK calls are replaced, and they follow the original
    code's own "Galaxy unavailable" path (done: `GalaxyIcalls.cs`). The
    original code around them still runs; nothing becomes blanket success.
  - Storage: `OnlineInterfaceGog` writes `<SpecialFolder.ApplicationData>/Cuphead/*.sav`
    with its original load/save/callback sequencing; the port roots that
    folder in port storage (Windows: guarded save root; console: the title's
    save area, API to verify).
  - PC-only options (resolution, window, vsync) are fixed to 720p60.
  - No keyboard or mouse.

  Everything else that differs is a defect.

## 2. Content representation

### 2.1 Packaging

One indexed logical archive: `GameData/cuphead.idx` (loaded whole) plus data
volumes `cuphead.000…` below 2^31 bytes each (FAT32 USB, unverified console
file limits, per-volume integrity). Per-scene packs would duplicate shared
content.

- Index: pack/converter versions, source identity (GOG build
  `55543280706943035`, `bundleVersion` 1.3.2, SHA-256 of `Assembly-CSharp.dll`
  and key data files); entries with dense ids, type, volume, sector-aligned
  offset, sizes, codec, CRC32 and a content hash. Ids come from sorted logical
  keys (class, source file, 64-bit path ID, sub-index).
- Residency groups per scene from scene dependencies and the game's own preload
  lists (`SceneLoader.load_cr` -> `AssetLoader<SpriteAtlas>/<AudioClip>.GetPreloadAssetNames`),
  plus a resident core group; volumes ordered by group.
- Reader: 64-bit offsets, asynchronous requests on a streaming thread into
  pre-reserved slabs, completions applied at frame boundaries.
- Compression: GPU textures stored in final GPU layout; structured data with a
  fast permissive LZ codec; audio per 2.4.

Today the CHOB object trees (`tools/convert_objects.py`) are the development
format; the archive replaces them for the console.

### 2.2 Textures

The source atlases are BC7 4096² pages (1,108 Texture2D, 5.22 GB of bundles).
Xenos has no BC7.

| Content | Candidate | Condition |
| --- | --- | --- |
| Character, boss, FX atlases | DXT5 | default for partial alpha |
| Pages with only 0/1 alpha | DXT1 | alpha eligibility only; RGB still lossy |
| TMP SDF atlases, fine UI | 8-bit alpha/luminance or uncompressed | DXT damages SDF |
| Full-screen overlays | as measured | few |

- Every BC7 -> DXT conversion loses colour; the converter reports per-page
  error against the decoded BC7 and keeps a visual review set. Colour space and
  sampling are recovered from the data, not assumed.
- Blending follows each material's recovered recipe (shader, blend state,
  keywords, properties); straight vs premultiplied storage per recipe; colour
  dilation only where straight alpha is sampled. Oracle: render fixed sprite
  sets on Windows and compare with captures from the user's PC game.
- Mips off for ~1:1 content; per-page option where zoomed.
- Layout/endianness/limits stay provisional until probed on the target API
  (D3D9 under the XDK, Vulkan via vulkan4xenon under xSDK).
- Budget: a 4096² DXT5 page is 16 MiB; the eight Player pages are 128 MiB.
  Whether co-op/Chalice scenes need them all at once is the largest memory
  risk. Residency policy (preload, re-atlas per group, prefetch at phase
  boundaries, mid-fight streaming) is chosen from measured dependency sets.

### 2.3 Sprites

Atlas pages plus each sprite's original mesh: sprite-space vertices, atlas UVs
(packing rotation/flip included), indices, rect, pivot, pixels-per-unit, page
identity. Keys are (serialized file, path ID), never names.

### 2.4 Audio

132 AudioClips. Runtime: XAudio2 under the XDK (the xSDK audio path to
verify) behind the engine's AudioSource/AudioMixer state (mixer state exists:
`AudioMixerIcalls.cs`). XMA needs XDK tools the user won't have, so the
converter stores 16-bit PCM or a CPU-decodable ADPCM, chosen by measured disk
and CPU cost. Music streams; SFX stay resident within budget.

### 2.5 Other content

Scene graphs, prefabs, ScriptableObjects (CHOB today), Animator controllers,
clips and events, colliders, Rewired user data, localization, TMP font assets,
materials, preload lists. The converter reports every class or field it cannot
represent (CHOB keeps every object; the engine reports decode mismatches).

## 3. Converter (user-facing)

- A frozen Windows executable (pinned UnityPy, Pillow, texture2ddecoder, a
  small native helper for DXT and hashing); no Python or SDK for users.
- Flow: select the GOG folder; validate identity against the supported list;
  check free space; convert into staging with a content cache; deterministic
  output; verify; rename atomically; write a report.
- Builds the console code too: patcher, NativeAOT-LLVM and w2c2 run on the
  user's PC from their own DLLs (nothing derived from the game is distributed).
  Whether that toolchain can ship inside a converter, or the user installs it,
  is open.
- Never writes into the game folder or reads saves. Errors say what to do.
- Deployment: `default.xex` plus `GameData/` on USB FAT32 or FTP, every file
  below 4 GiB.

## 4. Budgets (to validate on hardware)

Available title memory is unmeasured (an early Xenia probe reported "~491 MB
free physical"; an emulator figure, ambiguous unit).

| Pool | Proposed cap |
| --- | ---: |
| XEX image, CRT, statics (AOT code is large: 26 MB wasm today) | 48 MiB |
| Managed heap (NativeAOT GC) | 96 MiB peak |
| Engine state (scene graph, animation, physics) | 32 MiB |
| GPU textures incl. transition overlap | 192 MiB |
| Audio | 36 MiB |
| I/O staging, decompression | 16 MiB |
| Render targets, buffers | 16 MiB |
| Reserve | 48 MiB |
| **Total** | **484 MiB** |

A planning envelope, not a fit; pools are validated separately and transitions
measured per case.

CPU at 60 Hz: game logic single-threaded as in Unity (~10 ms target, to test);
render submission from a per-frame snapshot on another hardware thread; audio
and streaming on further threads. Input read at frame start; no more latency
than the GOG build.

## 5. Verification ladder and gates

| Level | Gate |
| --- | --- |
| V0 | Converter determinism, format tests, supported-version validation |
| V1 | Windows oracles: engine self-tests, render fixtures per recipe, and a test-only trace mod for the user's PC game recording per-frame state under recorded input, to diff against the port and settle Unity behaviours (Random, rounding, StringToHash, blending). Development only. |
| V2 | Windows full routes: the opening, Root Pack with death/retry/victory/progression, then every scene |
| V3 | XDK XEX on Xenia: functional parity only, never timing (once, hidden, muted, self-driven; the trigger for the xSDK switch) |
| V4 | Hardware (xSDK): boot, memory, frame-time distributions in the worst scenes, transitions, audio continuity |
| V5 | Content matrix: 79 scenes, base and DLC, difficulties, co-op, Chalice, shops, King Dice, chess, endings |
| V6 | Soak/stress: multi-hour flat heap, power loss during save, pad hot-plug |
| V7 | Release candidate from the user instructions on a clean PC and console |

- **G-A memory:** worst-case texture working set plus transition overlap fits
  measured memory under the chosen residency policy.
- **G-B CPU:** a recorded-input replay of a heavy scene (Saltbaker: 2,000+
  objects) on hardware, rendering separate, reporting median/p95/p99 frame
  logic time; go if p95 is within budget or has a measured path there.
- **G-C GC:** flat heap in a soak.

The opening flow ([OPENING_FLOW.md](OPENING_FLOW.md)) is one gate inside V2/V4,
not release.

## 6. Facts to verify

Target API, docs or probe: texture limits/pitch/alignment, eDRAM formats,
audio source formats, save-device APIs, file seek and overlapped I/O, file-size
limits, available title memory, hardware-thread layout.

Unity 2017.4 behaviours, with the V1 oracle rather than memory: `Random`
(xorshift128 seeding and the float mapping in `MiscIcalls.cs`), `Mathf.Round`
midpoints, `Animator.StringToHash`, `deltaTime` under `timeScale`,
`FindObjectsOfType` and inactive objects, `TextAsset.text` BOM handling,
bundle `LoadAllAssets` contents, per-recipe blending.
