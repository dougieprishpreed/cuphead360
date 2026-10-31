# Architecture

## Selected strategy

**Metadata-driven IL execution runtime + native Unity-compatible layer +
incremental native code generation for hot paths.**

The original `Assembly-CSharp.dll` is the authoritative game logic. Instead of
hand-recreating behaviour, the port loads the original metadata and IL and
executes it with a custom CLR-compatible runtime written in portable C++03.
Unity-facing APIs are implemented by a focused native engine. This is a
hybrid of the two viable extremes:

- a full CIL-to-C++ translator would produce the best peak performance but
  requires solving control-flow reconstruction, exception regions, delegates,
  generics, and coroutine state machines before anything runs;
- a pure clean-room reimplementation would be faster to start but would lose
  the exact original behaviour the project requires.

Executing the original IL preserves semantics immediately and gives us a
behavioural oracle for later optimisation. The interpreter is predecoded at
load time (tokens resolved, branches resolved), and hot methods can later be
translated to C++ without changing the object model or Unity layer.

### Why not port Mono or use IL2CPP?

- Cuphead's PC build is Mono-JIT, not IL2CPP. There is no generated C++.
- IL2CPP output depends on Unity's proprietary `libil2cpp` and the native
  Unity engine, neither of which is available for Xenon.
- Mono's runtime is a large POSIX-oriented codebase; porting it to the XDK
  toolchain, Xenon threads, and the 512 MB console memory model would be a
  larger and less controllable project than a game-specific runtime.
- XNA's managed runtime was evaluated and rejected: it cannot run the
  original Unity-facing assembly without a complete UnityEngine shim, its
  CLR/BCL surface is older than the game's, and the project requirement is a
  native C/C++ XDK build.

### Why the interpreter is not the final performance story

The runtime is designed so that execution can be accelerated without changing
game code:

1. Predecoded instructions and resolved metadata remove parsing from the hot
   loop.
2. The object model uses fixed field offsets and direct vtable slots.
3. Methods identified by profiling as hot can be translated to C++ functions
   that operate on the same `Value`/object representation.
4. The native Unity layer (transform hierarchy, sprite batching, collision,
   audio mixing) is already native; it dominates frame cost in a 2D game.

The interpreter is therefore a correctness-first execution engine with a
defined AOT escape hatch, not a permanent design ceiling.

## Layers

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

Assets flow through `tools/prepare_game.py`:

```
GOG installation
   |  version detection + manifest
   |  UnityFS bundle extraction (sprites, textures, audio, fonts)
   |  serialized scene/animation extraction
   v
GameData/ (original or losslessly repacked data; never committed)
```

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
- Tests include a big-endian data-path test that runs on both targets.

## Memory model

- 32-bit pointers, 4-byte object references.
- Object heap: 4-byte type id + fields, allocated from a tracked heap. Initial
  implementation uses a simple non-moving allocator plus free lists; a
  mark/sweep collector is planned once object lifetimes are exercised.
- Asset data is streamed/cached separately from the game heap and evicted by
  bundle, to respect the 512 MB shared memory budget.
- No assumption that all 5.22 GB of bundles is resident.

## Coroutines and lifecycle

C# iterator state machines are ordinary classes with a `MoveNext` method. The
runtime's coroutine scheduler owns the `IEnumerator` object and calls
`MoveNext` once per frame, matching Unity's behaviour:

- `StartCoroutine` registers the enumerator;
- yielded `WaitForSeconds`/`WaitForEndOfFrame`/null values are interpreted by
  the scheduler;
- `StopCoroutine` removes it;
- `OnDisable`/`OnDestroy` stop owned coroutines.

Unity message dispatch (`Awake`, `OnEnable`, `Start`, `FixedUpdate`, `Update`,
`LateUpdate`, `OnDisable`, `OnDestroy`) is driven by the engine's component
scheduler using method lookup on the managed type.

## Deliberate non-goals

- Porting the entire Unity engine. Only the APIs and behaviours reachable
  from Cuphead's IL need implementation. The coverage list is generated from
  metadata and tracked in `docs/COMPATIBILITY.md`.
- Reimplementing gameplay from observation. Original IL is the source of
  truth.
- Shipping game data. The repository ships tools; the user's installation
  supplies content.
