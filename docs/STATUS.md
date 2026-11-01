# Status

Not a playable port yet. What is true on 2026-11-01 (round 4), and what
isn't. Evidence: [VERIFICATION.md](VERIFICATION.md). Architecture:
[MANAGED_PLAYER.md](MANAGED_PLAYER.md). Plan: [CONSOLE_PLAN.md](CONSOLE_PLAN.md).

## Works (Windows, JIT oracle)

- All 8,438 types of the game, Unity, Rewired and Galaxy assemblies load and
  all 60,077 methods compile on .NET 10.
- Unity lifecycle semantics: 23/23 self-test checks.
- The original boot: `scene_start` -> `Cuphead.Init` (resources, 420 asset
  bundles' worth of data, fonts, localization, settings, GOG Galaxy's
  original "unavailable" path) -> `scene_title` through the original
  `LoadSceneAsync`, 300 frames with no exceptions, no data problems, and every
  script object's serialized fields decoded byte-exact.
- Engine state for scenes, objects, transforms, coroutines, asset bundles,
  Resources, JsonUtility, PlayerPrefs (port-local), the audio mixer
  (snapshots, exposed parameters), TextAsset.
- The title sequence plays from the original code: fader, MDHR logo
  animation, fade, dancing title with the flashing "Press Any Button" text,
  through the engine's cameras, sprite renderers, late-bound sprite atlases,
  Animator, Canvas/CanvasRenderer and TextMesh Pro meshes, 0 exceptions.
- Windows preview: `run --capture` + `tools/preview.py` draw what the engine
  would render to PNG (BC7 atlases decoded from the player's game files).
- Converter: all serialized files, built-in resources and asset bundles to
  CHOB object trees, 0 errors.
- The installed saves are never touched (SHA-256 checked around every run).

## Not done

- A real renderer: only the Windows preview draws, without the game's
  post-effect camera (film grain, chromatic aberration, blur).
- Input (Rewired's native layer), audio playback, physics.
- Any scene past the title; the opening flow (OPENING_FLOW.md).
- Texture/audio conversion and the console content archive.
- The console host: the translated C has not been compiled by the XDK or
  xSDK, nothing has run in Xenia or on hardware; no memory or frame-time
  measurements.
