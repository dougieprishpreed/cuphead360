#!/usr/bin/env python3
"""Extract a Cuphead scene into the portable Cuphead360 scene format.

This is a local preparation tool: it reads the user's GOG installation and
writes converted data. It never writes into the game installation.

The first version targets static SpriteRenderer content (the title screen).
Animation and script serialization are layered on top as the engine grows.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import struct
import sys
import zlib

import UnityPy


def scene_index(managed_dir: str) -> list[str]:
    ggm = os.path.join(managed_dir, "globalgamemanagers")
    env = UnityPy.load(ggm)
    for o in env.objects:
        if o.type.name == "BuildSettings":
            return [str(s.path if hasattr(s, "path") else s) for s in o.read().scenes]
    return []


def load_scene_bundle(managed_dir: str, scene_file: str):
    """Loads a scene plus its serialized-file dependencies."""
    scene_path = os.path.join(managed_dir, scene_file)
    env = UnityPy.load(scene_path)
    deps = []
    for name, f in env.files.items():
        if hasattr(f, "externals"):
            for ext in f.externals:
                p = str(ext.path)
                if p and not p.startswith("library/") and p != "unity default resources":
                    candidate = os.path.join(managed_dir, p)
                    if os.path.exists(candidate):
                        deps.append(candidate)
    if deps:
        env = UnityPy.load(scene_path, *deps)
    return env


def iter_objects(env):
    for o in env.objects:
        yield o


def pptr_key(sprite):
    """Stable key for a Sprite's render data entry."""
    rk = getattr(sprite, "m_RenderDataKey", None)
    if rk is None:
        return None
    return (unicode(rk[0]) if str(type(rk[0])) else str(rk[0]), int(rk[1]))


def guid_key(guid):
    return (int(guid.data_0_), int(guid.data_1_), int(guid.data_2_), int(guid.data_3_))


def find_atlas_bundle(bundle_dir: str, tag: str):
    candidates = [
        "atlas_" + tag.lower().replace(" ", ""),
        "atlas_" + tag.lower().replace(" ", "_"),
        tag.lower(),
    ]
    for c in candidates:
        p = os.path.join(bundle_dir, c)
        if os.path.exists(p):
            return p
    return None


class AtlasCache:
    def __init__(self, bundle_dir: str, out_dir: str):
        self.bundle_dir = bundle_dir
        self.out_dir = out_dir
        self.cache = {}
        self.textures = {}
        self.images = {}
        os.makedirs(os.path.join(out_dir, "textures"), exist_ok=True)
        os.makedirs(os.path.join(out_dir, "sprites"), exist_ok=True)

    def get(self, tag: str):
        if tag in self.cache:
            return self.cache[tag]
        path = find_atlas_bundle(self.bundle_dir, tag)
        if not path:
            self.cache[tag] = None
            return None
        env = UnityPy.load(path)
        atlas = None
        for o in env.objects:
            if o.type.name == "SpriteAtlas":
                atlas = o.read()
                break
        if atlas is None:
            self.cache[tag] = None
            return None
        entries = {}
        tex_images = {}
        for key, data in atlas.m_RenderDataMap:
            tex = data.texture.read()
            tex_name = getattr(tex, "m_Name", "texture")
            if tex_name not in tex_images:
                img = tex.image.convert("RGBA")
                self.images[tex_name] = img
                tex_images[tex_name] = {
                    "name": tex_name,
                    "file": "",
                    "width": img.width,
                    "height": img.height,
                    "format": "rgba8",
                }
            entries[guid_key(key[0])] = {
                "texture": tex_name,
                "rect": [float(data.textureRect.x), float(data.textureRect.y),
                         float(data.textureRect.width), float(data.textureRect.height)],
                "offset": [float(data.textureRectOffset.x), float(data.textureRectOffset.y)],
                "pixels_per_unit": float(getattr(tex, "m_TextureSettings", None) and 1.0 or 1.0),
            }
        result = {"atlas": atlas, "entries": entries, "textures": tex_images, "path": path}
        self.cache[tag] = result
        return result


def sprite_info(sprite, atlas_cache: AtlasCache, out_sprites: dict, out_textures: dict):
    name = getattr(sprite, "m_Name", "")
    tag = None
    tags = getattr(sprite, "m_AtlasTags", None)
    if tags:
        tag = str(tags[0])
    rk = getattr(sprite, "m_RenderDataKey", None)
    entry = None
    tex = None
    if tag:
        atlas = atlas_cache.get(tag)
        if atlas and rk is not None:
            entry = atlas["entries"].get(guid_key(rk[0]))
            if entry:
                tex = atlas["textures"].get(entry["texture"])
    if entry is None:
        # Unpacked sprite: its render data points directly at a texture.
        rd = getattr(sprite, "m_RD", None)
        if rd is not None and getattr(rd, "texture", None) is not None:
            try:
                t = rd.texture.read()
                img = t.image.convert("RGBA")
                tex = {"name": getattr(t, "m_Name", "texture"), "file": "",
                       "width": img.width, "height": img.height, "format": "rgba8"}
                atlas_cache.images[tex["name"]] = img
                entry = {"texture": tex["name"],
                         "rect": [float(rd.textureRect.x), float(rd.textureRect.y),
                                  float(rd.textureRect.width), float(rd.textureRect.height)],
                         "offset": [float(rd.textureRectOffset.x), float(rd.textureRectOffset.y)]}
            except Exception:
                pass
    if entry is None or tex is None:
        return None
    img = atlas_cache.images.get(tex["name"])
    if img is None:
        return None
    x, y, w, h = [int(round(v)) for v in entry["rect"]]
    if w <= 0 or h <= 0:
        return None
    top = img.height - (y + h)
    crop = img.crop((x, top, x + w, top + h)).convert("RGBA")
    safe = "".join(ch if ch.isalnum() or ch in "-_." else "_" for ch in name)
    if not safe:
        safe = "sprite_%d" % int(sprite.object_reader.path_id if hasattr(sprite, "object_reader") else 0)
    crop_base = os.path.join("sprites", safe)
    raw_path = os.path.join(atlas_cache.out_dir, crop_base + ".rgba")
    with open(raw_path, "wb") as f:
        f.write(b"CHTX")
        f.write(struct.pack("<III", crop.width, crop.height, 1))
        f.write(crop.tobytes())
    crop.save(os.path.join(atlas_cache.out_dir, crop_base + ".png"))
    rec = {
        "name": name,
        "texture": safe,
        "texture_file": crop_base + ".rgba",
        "texture_width": crop.width,
        "texture_height": crop.height,
        "rect": [0.0, 0.0, float(crop.width), float(crop.height)],
        "offset": [0.0, 0.0],
        "pivot": [float(sprite.m_Pivot.x), float(sprite.m_Pivot.y)],
        "pixels_per_unit": float(sprite.m_PixelsToUnits),
        "width": float(sprite.m_Rect.width) / max(0.0001, float(sprite.m_PixelsToUnits)),
        "height": float(sprite.m_Rect.height) / max(0.0001, float(sprite.m_PixelsToUnits)),
        "atlas": tag or "",
    }
    out_sprites[name] = rec
    out_textures[safe] = {
        "name": safe, "file": crop_base + ".rgba",
        "width": crop.width, "height": crop.height, "format": "rgba8",
    }
    return rec


def vec3(v):
    return [float(v.x), float(v.y), float(v.z)]


def vec4(v):
    return [float(v.x), float(v.y), float(v.z), float(v.w)]


def read_components(go):
    comps = []
    for c in go.m_Component:
        try:
            comps.append(c.component.read())
        except Exception:
            pass
    return comps


MAGIC_SCENE = 0x43534843  # "CHSC"
SCENE_HEADER_WORDS = 32


def write_scene_binary(scene: dict, out_dir: str, path: str) -> int:
    """Writes the compact CHSC scene consumed by the C++ runtime."""
    strings = bytearray(b"\\x00")
    soff = {"": 0}

    def s(text):
        text = text or ""
        if text in soff:
            return soff[text]
        off = len(strings)
        strings.extend(text.encode("utf-8"))
        strings.append(0)
        soff[text] = off
        return off

    texture_index = {}
    for i, t in enumerate(sorted(scene.get("textures", {}).keys())):
        texture_index[t] = i + 1
    sprite_index = {}
    for i, name in enumerate(sorted(scene.get("sprites", {}).keys())):
        sprite_index[name] = i + 1

    components = bytearray()
    go_records = bytearray()
    for g in scene["gameobjects"]:
        comp_start = len(components) // 48
        count = 0
        tr = g.get("transform", {})
        for c in g.get("components", []):
            ctype = 0
            flags = 0
            sprite_id = 0
            controller_off = 0
            order = 0
            layer = 0
            color = [1.0, 1.0, 1.0, 1.0]
            if c["type"] == "SpriteRenderer":
                ctype = 1
                flags |= 1 if c.get("enabled", True) else 0
                flags |= 2 if c.get("flipX") else 0
                flags |= 4 if c.get("flipY") else 0
                sprite_id = sprite_index.get(c.get("sprite", ""), 0)
                order = int(c.get("sortingOrder", 0))
                layer = int(c.get("sortingLayer", 0))
                color = list(c.get("color", color))
            elif c["type"] == "Animator":
                ctype = 2
                flags |= 1 if c.get("enabled", True) else 0
                controller_off = s(c.get("controller", ""))
            elif c["type"] == "Camera":
                ctype = 3
                flags |= 1 if c.get("enabled", True) else 0
            else:
                continue
            components.extend(struct.pack(
                "<4I2i4f2I",
                ctype, flags, sprite_id, controller_off,
                order, layer,
                float(color[0]), float(color[1]), float(color[2]), float(color[3]),
                0, 0,
            ))
            count += 1
        pos = tr.get("position", [0.0, 0.0, 0.0])
        rot = tr.get("rotation", [0.0, 0.0, 0.0, 1.0])
        scale = tr.get("scale", [1.0, 1.0, 1.0])
        go_records.extend(struct.pack(
            "<4I3f4f3f2I",
            int(g["id"]), int(tr.get("parent", 0)), s(g.get("name", "")),
            1 if g.get("active", True) else 0,
            float(pos[0]), float(pos[1]), float(pos[2]),
            float(rot[0]), float(rot[1]), float(rot[2]), float(rot[3]),
            float(scale[0]), float(scale[1]), float(scale[2]),
            comp_start, count,
        ))

    sprites = bytearray()
    for name in sorted(scene.get("sprites", {}).keys()):
        sp = scene["sprites"][name]
        tex = sp.get("texture", "")
        sprites.extend(struct.pack(
            "<4I4f2f3f2fI",
            s(sp.get("name", name)), texture_index.get(tex, 0),
            int(sp.get("texture_width", 0)), int(sp.get("texture_height", 0)),
            float(sp.get("rect", [0, 0, 0, 0])[0]), float(sp.get("rect", [0, 0, 0, 0])[1]),
            float(sp.get("rect", [0, 0, 0, 0])[2]), float(sp.get("rect", [0, 0, 0, 0])[3]),
            float(sp.get("pivot", [0.5, 0.5])[0]), float(sp.get("pivot", [0.5, 0.5])[1]),
            float(sp.get("pixels_per_unit", 1.0)), float(sp.get("width", 0.0)),
            float(sp.get("height", 0.0)),
            float(sp.get("offset", [0.0, 0.0])[0]), float(sp.get("offset", [0.0, 0.0])[1]),
            0,
        ))

    textures = bytearray()
    for name in sorted(scene.get("textures", {}).keys()):
        t = scene["textures"][name]
        textures.extend(struct.pack(
            "<6I",
            s(t.get("name", name)), s(t.get("file", "").replace("\\\\", "/")),
            int(t.get("width", 0)), int(t.get("height", 0)),
            1 if t.get("format", "rgba8") == "rgba8" else 0, 0,
        ))

    cameras = bytearray()
    for cam in scene.get("cameras", []):
        bg = cam.get("backgroundColor", [0, 0, 0, 1])
        pos = cam.get("position", [0, 0, 0])
        cameras.extend(struct.pack(
            "<2I9f3f2I",
            int(cam.get("go_id", 0)),
            1 if cam.get("orthographic") else 0,
            float(cam.get("orthographicSize", 360.0)),
            float(cam.get("fieldOfView", 60.0)),
            float(cam.get("near", -1000.0)),
            float(cam.get("far", 1000.0)),
            float(bg[0]), float(bg[1]), float(bg[2]), float(bg[3]),
            float(cam.get("depth", 0.0)),
            float(pos[0]), float(pos[1]), float(pos[2]),
            0, 0,
        ))

    body = bytearray()
    hdr = [MAGIC_SCENE, 1, 0, len(go_records)//64, len(sprites)//64,
           len(textures)//24, len(cameras)//64, len(components)//48]
    sections = [strings, go_records, sprites, textures, cameras, components]
    offsets = []
    sizes = []
    base = SCENE_HEADER_WORDS * 4
    for sec in sections:
        offsets.append(base + len(body))
        sizes.append(len(sec))
        body.extend(sec)
    while len(body) & 15:
        body.append(0)
    hdr.extend(offsets)
    hdr.extend(sizes)
    hdr.extend([0] * (SCENE_HEADER_WORDS - len(hdr)))
    hdr[2] = SCENE_HEADER_WORDS * 4 + len(body)
    blob = struct.pack("<%dI" % SCENE_HEADER_WORDS, *hdr) + bytes(body)
    with open(path, "wb") as f:
        f.write(blob)
    return len(blob)



def decode_streamed_frames(clip):
    """Decodes Unity's StreamedClip frame/key records.

    Frame: float time, int keyCount, then keyCount * (int curveIndex,
    float coeff[4]). For sprite/path curves value = coeff[3] is the index
    into pptrCurveMapping.
    """
    sc = clip.m_MuscleClip.m_Clip.data.m_StreamedClip
    data = sc.data
    out = []
    i = 0
    while i + 2 <= len(data):
        t = struct.unpack("<f", struct.pack("<I", data[i]))[0]
        i += 1
        n = int(data[i])
        i += 1
        keys = []
        for _ in range(n):
            if i + 5 > len(data):
                return out
            index = int(data[i])
            i += 1
            coeff = [struct.unpack("<f", struct.pack("<I", data[i + j]))[0] for j in range(4)]
            i += 4
            keys.append((index, coeff[3]))
        if t >= 0.0 and t != float("inf") and keys:
            out.append((t, keys))
    return out


def relative_path(gos_by_id, root_id, go_id):
    names = []
    cur = go_id
    guard = 0
    while cur and cur != 0 and guard < 64:
        guard += 1
        g = gos_by_id.get(cur)
        if not g:
            return None
        if cur == root_id:
            return "/".join(reversed(names))
        names.append(g.get("name", ""))
        tr = g.get("transform", {})
        cur = tr.get("parent", 0)
    return None


def extract_animations(env, out, atlas_cache, out_dir):
    gos_by_id = {g["id"]: g for g in out["gameobjects"]}
    # Controller name -> controller asset.
    controllers = {}
    for o in env.objects:
        if o.type.name == "AnimatorController":
            d = o.read()
            controllers[d.m_Name] = d

    anim = {"controllers": [], "clips": [], "frames": [], "states": []}
    processed = set()
    for root in out["gameobjects"]:
        animators = [c for c in root["components"] if c["type"] == "Animator"]
        for ac in animators:
            cname = ac.get("controller", "")
            ctrl = controllers.get(cname)
            if not ctrl or cname in processed:
                continue
            processed.add(cname)
            root_id = root["id"]
            # Map CRC32(path) -> GameObject id for descendants of this root.
            path_map = {}
            for g in out["gameobjects"]:
                p = relative_path(gos_by_id, root_id, g["id"])
                if p is not None:
                    path_map[zlib.crc32(p.encode("utf-8")) & 0xFFFFFFFF] = g["id"]
                    if p:
                        path_map.setdefault(zlib.crc32(g.get("name", "").encode("utf-8")) & 0xFFFFFFFF, g["id"])
            tos = {h: n for h, n in (ctrl.m_TOS or [])}
            sm = ctrl.m_Controller.m_StateMachineArray[0].data if ctrl.m_Controller.m_StateMachineArray else None
            if sm is None:
                continue
            ctrl_rec = {
                "name": cname,
                "default": int(sm.m_DefaultState),
                "state_start": len(anim["states"]),
                "state_count": 0,
            }
            state_indices = []
            for si, s in enumerate(sm.m_StateConstantArray):
                st = s.data
                state_name = tos.get(st.m_NameID, "state_%d" % si)
                clip_id = 0
                if st.m_BlendTreeConstantArray:
                    bt = st.m_BlendTreeConstantArray[0].data
                    if bt.m_NodeArray:
                        clip_id = int(bt.m_NodeArray[0].data.m_ClipID)
                clip = None
                if 0 <= clip_id < len(ctrl.m_AnimationClips):
                    try:
                        clip = ctrl.m_AnimationClips[clip_id].read()
                    except Exception:
                        clip = None
                frames = decode_streamed_frames(clip) if clip else []
                bindings = clip.m_ClipBindingConstant.genericBindings if clip and clip.m_ClipBindingConstant else []
                mapping = clip.m_ClipBindingConstant.pptrCurveMapping if clip and clip.m_ClipBindingConstant else []
                flat = []
                for t, keys in frames:
                    for curve_index, value_index in keys:
                        if curve_index >= len(bindings):
                            continue
                        b = bindings[curve_index]
                        target = path_map.get(int(b.path) & 0xFFFFFFFF)
                        vi = int(value_index)
                        if target is None or vi < 0 or vi >= len(mapping):
                            continue
                        try:
                            sp = mapping[vi].read()
                        except Exception:
                            continue
                        si_rec = sprite_info(sp, atlas_cache, out["sprites"], out["textures"])
                        if not si_rec:
                            continue
                        flat.append((float(t), si_rec["name"], int(target), int(curve_index)))
                if not flat:
                    continue
                clip_key = cname + "/" + (clip.m_Name if clip else state_name)
                clip_index = len(anim["clips"])
                frame_start = len(anim["frames"])
                for f in flat:
                    anim["frames"].append({
                        "time": f[0], "sprite": f[1], "target": f[2], "curve": f[3],
                    })
                anim["clips"].append({
                    "name": clip_key,
                    "sample_rate": float(clip.m_SampleRate) if clip else 24.0,
                    "loop": bool(st.m_Loop),
                    "frame_start": frame_start,
                    "frame_count": len(flat),
                    "curve_count": len(bindings),
                })
                state_indices.append(len(anim["states"]))
                anim["states"].append({
                    "name": state_name,
                    "clip": clip_index,
                    "loop": bool(st.m_Loop),
                    "speed": float(st.m_Speed),
                })
                ctrl_rec["state_count"] += 1
            if ctrl_rec["state_count"]:
                anim["controllers"].append(ctrl_rec)
    return anim


def write_anim_binary(anim, out_dir, path, sprite_ids):
    strings = bytearray(b"\\x00")
    soff = {"": 0}
    def s(text):
        text = text or ""
        if text in soff:
            return soff[text]
        off = len(strings)
        strings.extend(text.encode("utf-8"))
        strings.append(0)
        soff[text] = off
        return off

    sprite_index = sprite_ids
    clips = bytearray()
    for c in anim["clips"]:
        clips.extend(struct.pack("<If6I", s(c["name"]), float(c["sample_rate"]),
                                 1 if c["loop"] else 0, c["frame_start"],
                                 c["frame_count"], c["curve_count"], 0, 0))
    frames = bytearray()
    for f in anim["frames"]:
        frames.extend(struct.pack("<f3I", float(f["time"]), sprite_index.get(f["sprite"], 0),
                                  int(f["target"]), int(f["curve"])))
    states = bytearray()
    for st in anim["states"]:
        states.extend(struct.pack("<I2If2I", s(st["name"]), int(st["clip"]),
                                  1 if st["loop"] else 0, float(st["speed"]), 0, 0))
    ctrls = bytearray()
    for c in anim["controllers"]:
        ctrls.extend(struct.pack("<I7I", s(c["name"]), int(c["default"]),
                                 int(c["state_start"]), int(c["state_count"]), 0, 0, 0, 0))

    hdr = [0x4E414843, 1, 0, len(anim["clips"]), len(anim["frames"]),
           len(anim["states"]), len(anim["controllers"])]
    sections = [strings, clips, frames, states, ctrls]
    body = bytearray()
    offsets = []
    sizes = []
    base = 32 * 4
    for sec in sections:
        offsets.append(base + len(body))
        sizes.append(len(sec))
        body.extend(sec)
    while len(body) & 15:
        body.append(0)
    hdr.extend(offsets)
    hdr.extend(sizes)
    hdr.extend([0] * (32 - len(hdr)))
    hdr[2] = base + len(body)
    blob = struct.pack("<%dI" % 32, *hdr) + bytes(body)
    with open(path, "wb") as f:
        f.write(blob)
    return len(blob)



def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", required=True, help="Cuphead_Data directory")
    ap.add_argument("--scene", default="scene_title")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    scenes = scene_index(args.game)
    idx = None
    for i, s in enumerate(scenes):
        if s.endswith("/" + args.scene + ".unity"):
            idx = i
            break
    if idx is None:
        print("scene not found:", args.scene)
        return 1
    scene_file = "level%d" % idx
    print("scene", args.scene, "file", scene_file)
    env = load_scene_bundle(args.game, scene_file)

    bundle_dir = os.path.join(args.game, "StreamingAssets", "AssetBundles")
    atlas_cache = AtlasCache(bundle_dir, args.out)
    os.makedirs(args.out, exist_ok=True)

    out = {
        "format": 1,
        "scene": args.scene,
        "scene_file": scene_file,
        "gameobjects": [],
        "sprites": {},
        "textures": {},
        "cameras": [],
    }

    # Map GameObject path IDs to records. Only objects serialized in the
    # scene file itself belong to the scene; dependencies also contain
    # prefabs and assets that must not be instantiated.
    def in_scene(o):
        try:
            return os.path.basename(str(o.assets_file.name)) == scene_file
        except Exception:
            return False

    gos = {}
    transform_go = {}
    for o in env.objects:
        if o.type.name == "GameObject" and in_scene(o):
            go = o.read()
            gos[o.path_id] = {
                "id": o.path_id,
                "name": go.m_Name,
                "active": bool(go.m_IsActive),
                "components": [],
            }
    for o in env.objects:
        if not in_scene(o):
            continue
        if o.type.name == "Transform" or o.type.name == "RectTransform":
            tr = o.read()
            go_pptr = tr.m_GameObject
            go_id = go_pptr.m_PathID if go_pptr else 0
            if go_id not in gos:
                continue
            rec = gos[go_id]
            transform_go[o.path_id] = go_id
            rec["transform"] = {
                "position": vec3(tr.m_LocalPosition),
                "rotation": vec4(tr.m_LocalRotation),
                "scale": vec3(tr.m_LocalScale),
                "parent_tr": tr.m_Father.m_PathID if tr.m_Father else 0,
                "children_tr": [c.m_PathID for c in (tr.m_Children or [])],
                "parent": 0,
                "children": [],
            }
        elif o.type.name == "SpriteRenderer":
            sr = o.read()
            go_pptr = sr.m_GameObject
            go_id = go_pptr.m_PathID if go_pptr else 0
            if go_id not in gos:
                continue
            sprite = None
            if sr.m_Sprite:
                try:
                    sprite = sr.m_Sprite.read()
                except Exception:
                    sprite = None
            spr = sprite_info(sprite, atlas_cache, out["sprites"], out["textures"]) if sprite else None
            c = {
                "type": "SpriteRenderer",
                "enabled": bool(sr.m_Enabled),
                "sprite": spr["name"] if spr else "",
                "color": [float(sr.m_Color.r), float(sr.m_Color.g), float(sr.m_Color.b), float(sr.m_Color.a)],
                "flipX": bool(sr.m_FlipX),
                "flipY": bool(sr.m_FlipY),
                "sortingOrder": int(sr.m_SortingOrder),
                "sortingLayer": int(sr.m_SortingLayer),
                "sortingLayerName": str(getattr(sr, "m_SortingLayerName", "") or ""),
            }
            gos[go_id]["components"].append(c)
        elif o.type.name == "Animator":
            an = o.read()
            go_pptr = an.m_GameObject
            go_id = go_pptr.m_PathID if go_pptr else 0
            if go_id not in gos:
                continue
            ctrl_name = ""
            try:
                if an.m_Controller:
                    ctrl_name = an.m_Controller.read().m_Name
            except Exception:
                pass
            gos[go_id]["components"].append({
                "type": "Animator",
                "enabled": bool(an.m_Enabled),
                "controller": ctrl_name,
            })
        elif o.type.name == "Camera":
            cam = o.read()
            go_id = cam.m_GameObject.m_PathID if cam.m_GameObject else 0
            rec = {
                "go_id": go_id,
                "orthographic": bool(cam.orthographic),
                "orthographicSize": float(cam.orthographic_size),
                "fieldOfView": float(cam.field_of_view),
                "near": float(cam.near_clip_plane),
                "far": float(cam.far_clip_plane),
                "backgroundColor": [float(cam.m_BackGroundColor.r), float(cam.m_BackGroundColor.g),
                                    float(cam.m_BackGroundColor.b), float(cam.m_BackGroundColor.a)],
                "depth": float(cam.m_Depth),
            }
            out["cameras"].append(rec)

    # Resolve Transform parent/child links to GameObject ids.
    for g in gos.values():
        tr = g.get("transform")
        if not tr:
            continue
        ptr = tr.pop("parent_tr", 0)
        ctr = tr.pop("children_tr", [])
        tr["parent"] = transform_go.get(ptr, 0)
        tr["children"] = [transform_go[c] for c in ctr if c in transform_go]
    out["gameobjects"] = [gos[k] for k in sorted(gos.keys())]
    gos_by_id = {g["id"]: g for g in out["gameobjects"]}
    for cam in out["cameras"]:
        g = gos_by_id.get(cam.get("go_id", 0))
        if g and "transform" in g:
            cam["position"] = g["transform"]["position"]
        else:
            cam["position"] = [0.0, 0.0, 0.0]

    anim = extract_animations(env, out, atlas_cache, args.out)
    with open(os.path.join(args.out, "scene.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2)
    size = write_scene_binary(out, args.out, os.path.join(args.out, "scene.chs"))
    print("scene.chs", size, "bytes")
    with open(os.path.join(args.out, "anim.json"), "w", encoding="utf-8") as f:
        json.dump(anim, f, indent=2)
    sprite_ids = {name: i + 1 for i, name in enumerate(sorted(out["sprites"].keys()))}
    asize = write_anim_binary(anim, args.out, os.path.join(args.out, "anim.chs"), sprite_ids)
    print("anim.chs", asize, "bytes clips=%d frames=%d controllers=%d" % (
        len(anim["clips"]), len(anim["frames"]), len(anim["controllers"])))

    # Preview: composite SpriteRenderers into a PNG for visual validation.
    try:
        from PIL import Image, ImageDraw
        width, height = 1280, 720
        bg = (24, 24, 32, 255)
        canvas = Image.new("RGBA", (width, height), bg)
        # Build world transforms (2D).
        parent = {g["id"]: g.get("transform", {}).get("parent", 0) for g in out["gameobjects"]}
        local = {g["id"]: g.get("transform", {}) for g in out["gameobjects"]}
        cache_world = {}
        def world(gid):
            if gid in cache_world:
                return cache_world[gid]
            t = local.get(gid)
            if not t:
                return (0.0, 0.0, 0.0, 1.0, 1.0, 1.0)
            x, y, z = t["position"]
            rx, ry, rz, rw = t["rotation"]
            sx, sy, sz = t["scale"]
            # Extract Z rotation from quaternion.
            import math as m
            ang = m.atan2(2.0 * (rw * rz + rx * ry), 1.0 - 2.0 * (ry * ry + rz * rz))
            p = parent.get(gid, 0)
            if p and p in local:
                px, py, pz, pa, psx, psy = world(p)
                ca, sa = m.cos(pa), m.sin(pa)
                x, y = px + (x * ca - y * sa) * psx, py + (x * sa + y * ca) * psy
                ang += pa
                sx *= psx
                sy *= psy
            cache_world[gid] = (x, y, ang, sx, sy, sx)
            return cache_world[gid]
        draws = []
        for g in out["gameobjects"]:
            for c in g["components"]:
                if c["type"] != "SpriteRenderer" or not c["sprite"]:
                    continue
                s = out["sprites"].get(c["sprite"])
                if not s:
                    continue
                if s["name"] == "irisA_0000":
                    continue
                draws.append((c["sortingOrder"], g["id"], g, c, s))
        draws.sort(key=lambda t: (t[0], t[1]))
        for _, gid, g, c, s in draws:
            tex = out["textures"].get(s["texture"])
            if not tex:
                continue
            crop = Image.open(os.path.join(args.out, s["texture_file"].replace(".rgba", ".png"))).convert("RGBA")
            if c["flipX"]:
                crop = crop.transpose(Image.FLIP_LEFT_RIGHT)
            if c["flipY"]:
                crop = crop.transpose(Image.FLIP_TOP_BOTTOM)
            x, y, ang, sx, sy, _ = world(gid)
            pw = s["width"] * sx
            ph = s["height"] * sy
            draw_w = max(1, int(round(pw)))
            draw_h = max(1, int(round(ph)))
            crop = crop.resize((draw_w, draw_h))
            # Pivot is in sprite rect units from bottom-left.
            px = -s["pivot"][0] * draw_w
            py = -s["pivot"][1] * draw_h
            # 2D camera: orthographic, centered at origin.
            cx = width / 2 + x + px
            cy = height / 2 - (y + py) - draw_h
            tmp = Image.new("RGBA", (width, height), (0, 0, 0, 0))
            tmp.paste(crop, (int(cx), int(cy)), crop)
            if abs(ang) > 0.001:
                tmp = tmp.rotate(math.degrees(ang), resample=Image.BICUBIC, center=(int(cx + draw_w/2), int(cy + draw_h/2)))
            canvas.alpha_composite(tmp)
        canvas.convert("RGB").save(os.path.join(args.out, "preview.png"))
        print("preview:", os.path.join(args.out, "preview.png"))
    except Exception as e:
        print("preview failed:", e)

    print("gameobjects", len(out["gameobjects"]), "sprites", len(out["sprites"]), "textures", len(out["textures"]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
