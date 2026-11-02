# Status

Playable on Windows with sound; a first console build staged, untested on
hardware. What is true on 2026-11-01 (round 6), and what isn't. Evidence: [VERIFICATION.md](VERIFICATION.md). Architecture:
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
- The full normal loop: new game, intro, Elder Kettle's house, world map,
  Root Pack, results, back to the map, from the original code (bot-driven
  and in the live window): pads/keyboard through Rewired's Unity fallback,
  2D physics with collision response, dynamic TTF/CFF fonts, UGUI layout
  and masks.
- Live Windows window (`play`): OpenGL, compressed textures, XInput, sound
  (waveOut), 60 fps once warm; `--hidden` draws offscreen for checks.
- Audio: AudioSources through the game's mixer groups, Ogg clips decoded by
  NVorbis (`tools/convert_audio.py`).
- Console build: the player compiled to wasm, translated to C, built with
  xSDK into `default.xex` with a Vulkan host (`console/`); the translated C
  plays the opening loop headless on PC with 0 exceptions.
- Windows preview: `run --capture` + `tools/preview.py` draw what the engine
  would render to PNG (BC7 atlases decoded from the player's game files).
- Converter: all serialized files, built-in resources and asset bundles to
  CHOB object trees, 0 errors.
- The installed saves are never touched (SHA-256 checked around every run).

## Not done

- Anything verified on the console itself (first hardware test pending);
  stencil masks on the console.
- Render textures and the game's post-effect camera (film grain, chromatic
  aberration, blur); `Texture2D.SetPixel`.
- Scenes beyond the opening loop are untested; scene loads hitch 0.6-2 s.
- The single-archive console content format (CONSOLE_PLAN 2.1): the console
  reads the converted files directly.
- Console memory and frame time (no hardware numbers yet). The XDK/Xenia
  step was skipped: the user's xSDK path goes straight to hardware.
