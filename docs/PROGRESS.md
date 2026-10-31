# Progress

Status legend: **Not investigated / Recovered / Implemented / Windows tested /
XDK built / Xenia tested / Hardware tested**

## Completed in this session

### Investigation

- [x] Located and verified the GOG installation at `game/`.
- [x] Identified Cuphead `bundleVersion` 1.3.2, Unity 2017.4.9f1, Mono.
- [x] Confirmed DLC (`The Delicious Last Course`) is present in the same
  build: DLC scenes in `BuildSettings`, DLC types in `Assembly-CSharp.dll`,
  DLC asset bundles.
- [x] Inventoried managed assemblies, scene list, bundle contents, and object
  counts. See `REVERSE_ENGINEERING.md`.
- [x] Decompiled `Assembly-CSharp.dll` locally with ILSpy 11.1 for analysis
  (not committed).
- [x] Verified the XDK toolchain by rebuilding Spire360's `hello` XEX: VS2010
  PowerPC compiler `_MSC_VER=1600`, big-endian, D3D9, XInput, file writes.
- [x] Verified VS2010 x86 compiler exists for the Windows target.
- [x] Confirmed private GitHub repository `dougieprishpreed/cuphead360` exists
  (empty) and the configured Git credential manager can access it.

### Architecture

- [x] Selected metadata-driven IL execution + native Unity layer + future hot
  path AOT. Rationale in `ARCHITECTURE.md`.
- [x] Confirmed game code has no generic type/method definitions, reducing
  runtime complexity.
- [x] Established endianness and memory constraints as first-class design
  requirements.

### Code

- [ ] Portable core skeleton.
- [ ] Windows executable.
- [ ] Xbox 360 XEX from shared core.
- [ ] Metadata package generator.
- [ ] C++ metadata loader.
- [ ] IL interpreter.
- [ ] First original-logic execution milestone.
- [ ] Asset extraction pipeline.
- [ ] Renderer/engine vertical slice.

## Known gaps

- No full game runtime yet.
- No rendering/audio/collision engine yet.
- The decoder/interpreter does not yet cover the full IL opcode set.
- No Xenia/hardware validation of Cuphead360 itself.

## Next priorities

1. Shared core + dual-target hello build.
2. Python metadata package generator validated against ILSpy.
3. C++ metadata loader tested on Windows and 360.
4. Interpreter slice executing real `Assembly-CSharp` methods with a .NET
   reference comparison.
5. Asset pipeline for player sprites/animation and a first render.
