# Architecture

The 2026-10-31 [fundamental review](PORT_REVIEW.md) distinguishes the current
implementation from the target design below. The original-IL approach is
retained for a bounded pilot; the playable opening flow and Xbox gameplay
remain unproven. Consult STATUS.md and VERIFICATION.md for checkpoint evidence.

## Selected strategy

**Metadata-driven IL interpreter + a focused native Unity service layer.**
Native code generation for measured hot paths is a future option.

The original `Assembly-CSharp.dll` is the authoritative game logic. Instead of
hand-recreating behaviour, the port loads the original metadata and IL and
executes it with a custom managed runtime written in portable C++03. CLR and
Unity compatibility are incomplete and require independent verification.
The alternatives have concrete tradeoffs:

- a full CIL-to-C++ translator could reduce interpreter overhead but
  requires solving control-flow reconstruction, exception regions, delegates,
  generics, and coroutine state machines before anything runs;
- a pure clean-room reimplementation would be faster to start but would lose
  the exact original behaviour the project requires.

Original IL is the authority for game rules; its execution preserves behavior
only where the runtime and engine services implement the required semantics.
The interpreter currently decodes IL bytes during execution. Predecoding and
hot-method translation are planned, not implemented. They would still require
the same correct object model and Unity service contracts.

### Why not port Mono or use IL2CPP?

- Cuphead's PC build is Mono-JIT, not IL2CPP. There is no generated C++.
- IL2CPP output depends on Unity's proprietary `libil2cpp` and the native
  Unity engine, neither of which is available for Xenon.
- A Mono port would require XDK/PPC, threading and memory integration, while
  leaving the Unity service layer to implement. Its cost has not been measured
  against completing this runtime; reconsider if the bounded pilot fails.
- XNA's managed runtime was evaluated and rejected: it cannot run the
  original Unity-facing assembly without a complete UnityEngine shim, its
  CLR/BCL surface is older than the game's, and the project requirement is a
  native C/C++ XDK build.

### Why the interpreter is not the final performance story

Potential acceleration steps require profiling and correctness evidence:

1. Predecode instructions and cache resolved native bindings to reduce repeated
   decoding and string dispatch. Neither optimization is currently complete.
2. The object model uses fixed field offsets and direct vtable slots.
3. Consider translation of measured hot methods to C++ operating on the same
   `Value`/object representation; no AOT pipeline currently exists.
4. Profile transforms, collision and software rasterization independently of
   IL execution. There is no measured frame-cost distribution on Xbox, no GPU
   sprite batching, and no validated audio mixer.

These options do not establish acceptable Xenon performance. Windows timing,
Xenia timing and hardware timing must be measured and identified separately.

## Layers

This is the target module layout. Its list includes facilities such as managed
exceptions, audio and scene loading that are absent or incomplete today.

```
src/core       fixed-width types, endian loads/stores, files, logging,
               memory, containers, math
src/runtime    metadata package reader, type system, object heap, IL decoder
               and interpreter, BCL subset, delegates, coroutines, exceptions
src/engine     Unity-compatible object model: GameObject, Transform,
               Component, MonoBehaviour lifecycle, sprites, animation,
               collision, audio, scene loading
src/game       game-specific glue that is not recovered from IL (boot flow,
               platform services, save/online shims)
src/platform   win32 and xbox360 backends: display, input, audio, storage,
               timing, threads, diagnostics
```

Game logic stays in `src/runtime` + the original IL. `src/game` must not
reimplement gameplay mechanics.

## Data flow

```
Assembly-CSharp.dll (user's copy)
        |
        | tools/build_metadata.py  (host, Python)
        v
  GameData/metadata.chm  ----> runtime metadata loader
        |                              |
  original IL bytes ------------------+
        |
        v
  interpreter frames  <---->  Unity-compatible native engine
        |                              |
        +-------- game state ----------+
                                       |
                          win32 / xbox360 platform backends
```

The current development pipeline uses `tools/extract_scene.py`:

```
GOG installation
   |  serialized scene/dependency extraction
   |  sprite crops and partial animation/script extraction
   v
scene.chs + anim.chs + per-sprite RGBA (development data; never committed)
```

The production GameData packer, bounded residency, complete UI/audio/font
conversion and Xbox texture formats remain work to do. The documented
`tools/prepare_game.py` command does not currently exist.

## Executable contract

Both targets run the same `src/app` and the same runtime/engine code.

- Xbox 360: `projects/xbox360/Cuphead360.vcxproj`, built with the XDK's
  PowerPC compiler and Visual Studio 2010. Entry point `main()`. Output
  `default.xex`.
- Windows: `projects/win32/Cuphead360.vcxproj`, built with Visual Studio
  2010's x86 compiler. Same code, different platform backend. Supports a
  hidden/headless mode for automated tests.

No gameplay logic may live in a platform backend.

## Endianness and data representation

The Xbox 360 is big-endian PowerPC. The original game data, .NET metadata,
and IL are little-endian.

- All metadata and asset loaders use explicit little-endian readers
  (`read_u16le`, `read_u32le`, ...). They never cast serialized bytes to a
  host integer.
- In-memory `Value`/object fields use host-native representation.
- Generated assets are stored little-endian with a documented format and
  converted at load time. GPU textures will be converted to the Xenon's
  expected tiled/swizzled layout by the platform renderer, not by the shared
  game logic.
- Hash/checksum algorithms operate on bytes, not host words.
- Shared SelfTestEndian in src/app/app.cpp tests explicit byte readers/writers
  and is compiled into both targets. A Windows pass does not validate a
  big-endian CPU execution path; Xenia and hardware execution need separate
  recorded evidence.

## Memory model

- 32-bit pointers, 4-byte object references.
- Managed values/objects use the runtime's explicit tagged representation;
  they are not CLR-compatible memory layouts merely because pointers are 32-bit.
- Allocations are tracked and primarily retained until interpreter shutdown.
  There is no managed collector. Repeated scenes and retries need bounded
  lifetime/reclamation before long-session memory claims are justified.
- Scene.LoadTextures currently reads and retains every raw RGBA texture. The
  prepared tutorial payload is about 944 MiB; Root Pack about 1,421 MiB. This
  eager path exceeds the entire 512 MiB Xbox shared-memory budget.
- Streaming/cache eviction and bounded peak accounting are target requirements,
  not current facilities. File-size deduplication alone does not provide them.

## Coroutines and lifecycle

C# iterator state machines are ordinary classes with MoveNext. At the audited
`beb3a87` baseline, StartCoroutine registers an iterator but returns null,
Stop APIs are no-ops, ownership/join waits are absent, and a name filter disables
boss families. The first repair checkpoint addresses these defects; its exact
validated behavior and approximations belong in VERIFICATION.md.

The target distinguishes coroutine ownership from waiting on an independently
started handle. GameObject deactivation/destruction stops owned coroutines;
disabling a Behaviour alone must not stop them. The audited baseline has no
common lifecycle loop implementing all Awake/OnEnable/Start/FixedUpdate/Update/
LateUpdate/OnDisable/OnDestroy callbacks. Scene teardown, persistent roots,
asynchronous loads and fresh encounter initialization are later prerequisites.

## Storage boundary

Original PlayerData.Init requires storage callbacks during slot selection.
OnlineInterfaceGog.SavePath selects the installed game's AppData Cuphead directory
when Application.platform is WindowsPlayer; the current shim reports that
platform. A port-local storage path or interface override must be implemented
and tested before original storage calls are enabled. Tests must demonstrate
that the installed save directory is neither read nor written. In-memory
progression and durable save/reload remain separate acceptance checks.

## Deliberate non-goals

- Porting the entire Unity engine. Only the APIs and behaviours reachable
  from Cuphead's IL need implementation. The coverage list is generated from
  metadata and tracked in `docs/COMPATIBILITY.md`.
- Reimplementing gameplay from observation. Original IL is the source of
  truth.
- Shipping game data. The repository ships tools; the user's installation
  supplies content.
