#!/usr/bin/env python3
"""Draw the engine's render captures to PNG (Windows preview of what the port draws).

    preview.py [--capture work/preview/capture.jsonl] [--game game] [--out work/preview]
               [--frames 60,120] [--sheet]

The managed player writes one JSON line per captured frame
(`run --capture 60,120` or `--capture-every N`): cameras in depth order and,
for each, its sprites and UI meshes in Unity's draw order, with screen
positions, texture coordinates, tint and texture identity (converted file +
path id). This tool decodes those textures from the player's own game files
with UnityPy (BC7 etc. through texture2ddecoder), caches them as PNG under
<out>/tex, and composites each frame:

  clear flags  SolidColor/Skybox fill the camera rect, Depth/Nothing keep it
  sprites      one affine map per sprite (its mesh is planar), alpha blended
  UI           per triangle, vertex colors x tint x CanvasGroup alpha;
               TextMesh Pro SDF text approximated (face + outline thresholds)

Not drawn: post effects of the game's own render pipeline (film grain,
vignette, chromatic shift), render textures, masks, particles. It is a
preview of engine state, not the console renderer.

Reads the game folder only. Output frames: <out>/frame_NNNNN.png.
"""
import argparse
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

import UnityPy

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


class Textures:
    def __init__(self, game, cache):
        self.data = os.path.join(game, "Cuphead_Data")
        self.cache = cache
        self.mem = {}
        self.envs = {}
        os.makedirs(cache, exist_ok=True)

    def source(self, chob_path):
        """Converted file path -> (game file to load, serialized file name inside)."""
        parts = chob_path.replace("\\", "/").split("/")
        name = parts[-1][:-5] if parts[-1].endswith(".chob") else parts[-1]
        if parts[0] == "bundles" and len(parts) == 3:
            return os.path.join(self.data, "StreamingAssets", "AssetBundles", parts[1]), name
        for cand in (os.path.join(self.data, name), os.path.join(self.data, "Resources", name)):
            if os.path.exists(cand):
                return cand, name
        return None, name

    def get(self, tex):
        key = "%s:%d" % (tex["file"], tex["path"])
        if key in self.mem:
            return self.mem[key]
        fn = os.path.join(self.cache, key.replace("/", "_").replace(":", "_") + ".png")
        if os.path.exists(fn):
            img = Image.open(fn).convert("RGBA")
        else:
            img = self.decode(tex)
            if img is not None:
                img.save(fn)
        self.mem[key] = img
        return img

    def decode(self, tex):
        path, inner = self.source(tex["file"])
        if path is None:
            print("preview: no game file for", tex["file"], file=sys.stderr)
            return None
        env = self.envs.get(path)
        if env is None:
            env = self.envs[path] = UnityPy.load(path)
        for sf_name, sf in self.walk(env.files):
            if os.path.basename(sf_name) != inner and len(env.files) > 1 and inner not in sf_name:
                continue
            obj = sf.objects.get(tex["path"])
            if obj is not None:
                img = obj.read().image
                return img.convert("RGBA")
        print("preview: texture %s not found in %s" % (tex, path), file=sys.stderr)
        return None

    def walk(self, files):
        for name, f in files.items():
            if hasattr(f, "objects"):
                yield name, f
            elif hasattr(f, "files"):
                yield from self.walk(f.files)


def affine(dst, src):
    """Coefficients with src = (a x + b y + c, d x + e y + f), least squares over all point pairs.

    Tight sprite meshes have sliver triangles; one triangle alone gives a poor map."""
    m = np.array([[x, y, 1.0] for x, y in dst])
    if len(dst) < 3 or np.linalg.matrix_rank(m, tol=1e-6) < 3:
        return None
    sx = np.linalg.lstsq(m, np.array([p[0] for p in src]), rcond=None)[0]
    sy = np.linalg.lstsq(m, np.array([p[1] for p in src]), rcond=None)[0]
    return sx, sy


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / np.maximum(e1 - e0, 1e-6), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def composite(canvas, layer, x0, y0):
    """Alpha blend a float RGBA layer (premultiplied=False) into the float RGB canvas."""
    h, w = layer.shape[:2]
    region = canvas[y0:y0 + h, x0:x0 + w]
    a = layer[..., 3:4]
    region[...] = layer[..., :3] * a + region * (1 - a)


def draw_group(canvas, tex_img, pts, uvs, tris, tint, colors, view, H, sdf=None):
    tw, th = tex_img.size if tex_img is not None else (1, 1)
    dpts = [(x, H - y) for x, y in pts]
    spts = [(u * tw, (1 - v) * th) for u, v in uvs] if uvs else [(0, 0)] * len(pts)
    # Which triangles share one affine map: sprites (planar meshes) all do.
    groups = [tris] if colors is None and sdf is None else [tris[i:i + 3] for i in range(0, len(tris), 3)]
    vx0, vy0, vw, vh = view
    clip = (max(0, int(vx0)), max(0, int(H - vy0 - vh)), min(canvas.shape[1], int(vx0 + vw)), min(canvas.shape[0], int(H - vy0)))
    for g in groups:
        idx = sorted(set(g))
        tri = affine([dpts[i] for i in idx], [spts[i] for i in idx])
        if tri is None:
            continue
        sx, sy = tri
        xs = [dpts[i][0] for i in idx]
        ys = [dpts[i][1] for i in idx]
        x0, y0 = max(clip[0], int(np.floor(min(xs)))), max(clip[1], int(np.floor(min(ys))))
        x1, y1 = min(clip[2], int(np.ceil(max(xs)))), min(clip[3], int(np.ceil(max(ys))))
        if x1 <= x0 or y1 <= y0:
            continue
        w, h = x1 - x0, y1 - y0
        # Sample at pixel centres of the output region.
        coeffs = (sx[0], sx[1], sx[0] * (x0 + 0.5) + sx[1] * (y0 + 0.5) + sx[2] - 0.5,
                  sy[0], sy[1], sy[0] * (x0 + 0.5) + sy[1] * (y0 + 0.5) + sy[2] - 0.5)
        if tex_img is not None:
            warped = np.asarray(tex_img.transform((w, h), Image.AFFINE, coeffs, resample=Image.BILINEAR), dtype=np.float32) / 255.0
        else:
            warped = np.ones((h, w, 4), dtype=np.float32)
        mask = Image.new("L", (w, h), 0)
        md = ImageDraw.Draw(mask)
        for i in range(0, len(g) - 2, 3):
            md.polygon([(dpts[g[i + k]][0] - x0, dpts[g[i + k]][1] - y0) for k in range(3)], fill=255)
        m = np.asarray(mask, dtype=np.float32)[..., None] / 255.0
        col = np.array(tint, dtype=np.float32)
        if colors is not None:
            vc = np.mean([colors[i] for i in idx], axis=0) / 255.0
            col = col * vc
        if sdf is not None:
            d = warped[..., 3:4]
            face, outline, ow, dilate = sdf
            soft = 0.04
            edge = 0.5 - dilate * 0.5
            fa = smoothstep(edge - soft, edge + soft, d)
            oa = smoothstep(edge - ow * 0.5 - soft, edge - ow * 0.5 + soft, d) if ow > 0 else fa
            rgb = face[:3] * col[:3] * fa + outline[:3] * (oa - fa) * (1 - 0)
            alpha = (fa * face[3] + (oa - fa) * outline[3]) * col[3]
            rgb = np.where(alpha > 1e-6, rgb / np.maximum(fa + (oa - fa), 1e-6), 0)
            layer = np.concatenate([rgb.astype(np.float32), alpha.astype(np.float32)], axis=-1)
        else:
            layer = warped * col
        layer[..., 3:4] *= m
        composite(canvas, layer, x0, y0)


def render(frame, textures):
    W, H = frame["screen"]
    canvas = np.zeros((H, W, 3), dtype=np.float32)
    for v in frame["views"]:
        rect = v["rect"]
        if v["clear"] in (1, 2):
            bg = v.get("background", [0, 0, 0, 1])
            x0, y0, w, h = [int(round(c)) for c in rect]
            canvas[H - y0 - h:H - y0, x0:x0 + w] = bg[:3]
        for it in v["items"]:
            tex = it.get("texture")
            img = textures.get(tex) if tex and "file" in tex else None
            pts = list(zip(it["xy"][0::2], it["xy"][1::2]))
            uvs = list(zip(it["uv"][0::2], it["uv"][1::2]))
            colors = None
            if "colors" in it:
                c = it["colors"]
                colors = [c[i:i + 4] for i in range(0, len(c), 4)]
            sdf = None
            if "Distance Field" in (it.get("shader") or ""):
                p = it.get("props", {})
                face = np.array(p.get("_FaceColor", [1, 1, 1, 1]), dtype=np.float32)
                outline = np.array(p.get("_OutlineColor", [0, 0, 0, 1]), dtype=np.float32)
                sdf = (face, outline, float(p.get("_OutlineWidth", 0)), float(p.get("_FaceDilate", 0)))
            if img is None and tex is not None:
                continue
            draw_group(canvas, img, pts, uvs, it["tris"], it["tint"], colors, rect, H, sdf)
    return Image.fromarray((np.clip(canvas, 0, 1) * 255 + 0.5).astype(np.uint8), "RGB")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--capture", default=os.path.join(ROOT, "work", "preview", "capture.jsonl"))
    ap.add_argument("--game", default=os.path.join(ROOT, "game"))
    ap.add_argument("--out", default=os.path.join(ROOT, "work", "preview"))
    ap.add_argument("--frames", help="comma-separated frame numbers (default: all captured)")
    ap.add_argument("--sheet", action="store_true", help="also write sheet.png with every frame at quarter size")
    a = ap.parse_args()
    if os.path.abspath(a.out).startswith(os.path.abspath(a.game)):
        sys.exit("--out must be outside the game folder")
    want = set(int(x) for x in a.frames.split(",")) if a.frames else None
    textures = Textures(a.game, os.path.join(a.out, "tex"))
    shots = []
    with open(a.capture, encoding="utf-8") as f:
        for line in f:
            frame = json.loads(line)
            if want and frame["frame"] not in want:
                continue
            img = render(frame, textures)
            fn = os.path.join(a.out, "frame_%05d.png" % frame["frame"])
            img.save(fn)
            shots.append((frame["frame"], img))
            print("preview: frame %d (t=%.2f, %s) -> %s" % (frame["frame"], frame["time"], frame["scene"], fn))
    if a.sheet and shots:
        cols = min(4, len(shots))
        tw, th = shots[0][1].width // 4, shots[0][1].height // 4
        rows = (len(shots) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * tw, rows * th), (40, 40, 40))
        for i, (_, img) in enumerate(shots):
            sheet.paste(img.resize((tw, th), Image.BILINEAR), ((i % cols) * tw, (i // cols) * th))
        sheet.save(os.path.join(a.out, "sheet.png"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
