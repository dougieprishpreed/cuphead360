# Asset and metadata pipeline

This document describes the formats produced by the local preparation tools
and consumed by the shared C++ runtime.

The [2026-10-31 port review](PORT_REVIEW.md) records current feasibility limits.
These are development formats. A production packer, atlasing, bounded texture
residency and Xbox texture layout support are not implemented.

All tools read the user's GOG installation and write only to their `--out`
directory. They never modify `game/`.

## Metadata: `metadata.chm`

Produced by:

```
python tools/build_metadata.py game\Cuphead_Data\Managed\Assembly-CSharp.dll work\metadata\metadata.chm
```

The package is derived from the original `Assembly-CSharp.dll`. It retains IL
instructions while canonicalizing metadata token operands to
TypeDef/FieldDef/MethodDef where possible; it is not an unmodified byte copy.
It is little-endian and is
converted to host values by `src/runtime/metadata.cpp`.

Header: 64 u32 words (`CHM1`, version, file size, counts, section offsets,
section sizes). Sections: strings, types, fields, methods, signatures,
method signatures, local signatures, IL, exception clauses, user strings,
member refs, method specs, params, and auxiliary arrays.

The loader has historical Windows and emulated big-endian Xbox 360/Xenia
validation. This is separate from hardware validation and does not establish
execution of the latest checkpoint on either console target.

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
parameters, triggers, animation events, float/scale curves, and game-script
Animator.Play calls remain work to do. anim.chs has no sections for transitions,
parameters, triggers or animation events; the extractor and consuming runtime
must both change. Root Pack attacks, death and phase changes depend on them.

## Current limitations

- GameObjects/transforms, SpriteRenderer/Animator/Camera, BoxCollider2D,
  CircleCollider2D, Rigidbody2D and partial MonoBehaviour serialized fields
  are represented. Complete Canvas/TMP presentation, audio and arbitrary
  component graphs are not. EdgeCollider2D is omitted: the original Isle I
  scene file contains 40 such boundary components.
- Animation extraction covers sprite PPtr tracks. Scale/alpha/position
  curves (including the title-screen fader) are not applied yet.
- Nested serialized class collections need a structured decoder. The current
  List/array path calls parse_sig_value directly for each element, where a
  class is treated as a Unity object reference; it bypasses the serializable
  class recursion in parse_field_value. parse_script_values also stops at the
  first unsupported field and returns partial values. Complete graph recovery
  must validate byte consumption and report unsupported layouts explicitly.
- Scene dependencies are loaded by path from `Cuphead_Data`; asset bundles are
  loaded only for SpriteAtlas lookup. A production packer will consolidate
  these into streamable `GameData` packs.
- Sprite crops are per-sprite for development. Final packing should atlas
  them for batching and console memory.

## Residency and next packaging steps

Scene.LoadTextures currently retains all raw RGBA files at once. The current
prepared tutorial has 3,874 files totaling 989,522,484 bytes (943.68 MiB);
Root Pack has 5,955 totaling 1,490,179,264 bytes (1,421.15 MiB). These are file
totals, not a measured process peak, but the consuming eager path exceeds the
Xbox 360 shared-memory budget before other allocations.

Before expanding extraction, make optional PNG previews explicit and preserve
one current package per scene. Introduce immutable content identities and a
shared manifest for disk deduplication, then bounded texture residency with
resident/peak accounting and scene-lifetime release. Do not use writable
hardlinks that let regeneration mutate another scene's content. Validate sprite
rects, pivots, transparency, packing and animation references visually; the
retained checkpoint images contain packed-pose rendering artifacts.

Atlas organization and target-specific formats must have a consuming renderer.
The current software renderer reads linear RGBA; converting files to a GPU
layout alone does not supply a GPU renderer or establish console viability.

The original dialogue resource is present at resources.assets path ID 1175
(87,612 bytes). UnityPy's available type tree reads only its 56-byte header and
rejects the remaining payload. Recovery therefore needs the serialized schema
from metadata (or an independently validated reconstructed type tree), including
the nested dialogue/global/theme lists and the collection decoder correction
described under Current limitations. The existing empty runtime object is not
a recovered dialogue database.
