# Asset and metadata pipeline

This document describes the formats produced by the local preparation tools
and consumed by the shared C++ runtime.

All tools read the user's GOG installation and write only to their `--out`
directory. They never modify `game/`.

## Metadata: `metadata.chm`

Produced by:

```
python tools/build_metadata.py game\Cuphead_Data\Managed\Assembly-CSharp.dll work\metadata\metadata.chm
```

The package is derived from the original `Assembly-CSharp.dll`. It preserves
the original IL byte-for-byte (after canonicalizing metadata token operands
to TypeDef/FieldDef/MethodDef where possible). It is little-endian and is
converted to host values by `src/runtime/metadata.cpp`.

Header: 64 u32 words (`CHM1`, version, file size, counts, section offsets,
section sizes). Sections: strings, types, fields, methods, signatures,
method signatures, local signatures, IL, exception clauses, user strings,
member refs, method specs, params, and auxiliary arrays.

The loader was validated on Windows and on big-endian Xbox 360 under Xenia.

## Scene: `scene.chs`

Produced by `tools/extract_scene.py`. The tool:
- reads the scene list from `globalgamemanagers`;
- loads the selected `levelN` plus its serialized-file dependencies;
- resolves GameObject/Transform hierarchies (Transform path IDs are mapped
  to GameObject IDs before writing);
- resolves SpriteRenderer sprites through their SpriteAtlas render-data key;
- crops each used sprite from its atlas into `sprites/<name>.rgba`;
- writes a compact scene binary.

Header: 32 u32 words (`CHSC`, version, file size, counts, section offsets and
sizes). Sections: strings, GameObjects (64 bytes), sprites (64), textures
(24), cameras (64), components (48), script values (24 bytes each).
For type-4 (MonoBehaviour) components, the component's `sprite_id` field is
the first script-value index and `sorting_layer` is the value count.
Script values store field id, parent field, array index, kind, and three
payload words. Kinds: int, float, bool, Vector2, Vector3, string (offset into
the script-string section), PPtr, null PPtr, and array. Nested serializable
fields carry the parent field id; arrays carry the first element record
index. The runtime binds script values in a second pass after all components
exist.

Raw texture files begin with `CHTX`, then width, height, format (`1` =
RGBA8), then tightly packed top-down RGBA bytes.

## Animation: `anim.chs`

Produced by the same tool after parsing AnimatorControllers and
AnimationClips.

- Controller states are resolved through the controller's `m_TOS` name table.
- Default state and state clip IDs come from the controller's state machine.
- Sprite tracks come from Unity `StreamedClip` frames. Each frame is
  `float time, int keyCount, keyCount * (int curveIndex, float coeff[4])`;
  `coeff[3]` is the mapping index for sprite/PPtr curves.
- `genericBindings[i].path` is the CRC32 of the target's relative transform
  path (verified against `zlib.crc32("Cuphead")`, etc.).
- Frames are flattened to `(time, scene sprite id, target GameObject id,
  curve)`.

Header: 32 u32 words (`CHAN`). Sections: strings, clips (32 bytes), frames
(16), states (24), controllers (32).

The runtime currently plays each controller's default state. Transitions,
parameters, float/scale curves, and game-script `Animator.Play` calls are
future work.

## Current limitations

- Only SpriteRenderer/Animator/Camera components are extracted. UI/Canvas/
  TextMeshPro, colliders, audio sources, and MonoBehaviour serialized fields
  are not yet represented.
- Animation extraction covers sprite PPtr tracks. Scale/alpha/position
  curves (including the title-screen fader) are not applied yet.
- Scene dependencies are loaded by path from `Cuphead_Data`; asset bundles are
  loaded only for SpriteAtlas lookup. A production packer will consolidate
  these into streamable `GameData` packs.
- Sprite crops are per-sprite for development. Final packing should atlas
  them for batching and console memory.
