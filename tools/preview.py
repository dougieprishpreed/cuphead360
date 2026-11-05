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

  targets      a view drawing into a render texture draws into an RGBA canvas
               of its own (premultiplied, as the console's); items sampling
               it read that canvas
  effects      the Knight's glow, the Bishop's darkness, the blur sprite and
               the blits (copy, FastBlur, BlurGamma, the film grain and colour
               fringes) as the console's shaders compute them

Not drawn: particles. It is a preview of engine state, close to the console
renderer but not it.

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
capture_path = ""


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
                data = obj.read()
                if not hasattr(data, "image"):   # a RenderTexture: drawn by the game at run time
                    print("preview: %s is a %s (not drawn)" % (tex.get("name"), type(data).__name__), file=sys.stderr)
                    return None
                return data.image.convert("RGBA")
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


def composite(canvas, layer, x0, y0, premul=False):
    """Blend a float RGBA layer over the float RGBA canvas (premultiplied colour and alpha, as the
    console blends: One, OneMinusSrcAlpha); the layer straight unless premul."""
    h, w = layer.shape[:2]
    region = canvas[y0:y0 + h, x0:x0 + w]
    a = layer[..., 3:4]
    rgb = layer[..., :3] if premul else layer[..., :3] * a
    region[..., :3] = rgb + region[..., :3] * (1 - a)
    region[..., 3:4] = a + region[..., 3:4] * (1 - a)


def blend_recipe(shader, tex, col, ui=False):
    """Cuphead's sprite shaders by what they do (the converter keeps names only).
    On UI graphics the canvas alpha (folded into the colour) also fades the blend."""
    out = _blend(shader, tex, col)
    blend = any(k in shader for k in ("SpriteBlendDynamic/", "ScreenBlend", "MultiplyBlend", "OverlayBlend")) and "ScreenIgnoreColor" not in shader
    if ui and blend:
        out[..., 3:4] *= col[3]
    return out


def _blend(shader, tex, col):
    t, c = tex, col
    if "SpriteBlendDynamic/Screen" in shader or shader.endswith("/ScreenBlend"):
        rgb = 1 - (1 - t[..., :3]) * (1 - c[:3])
        return np.concatenate([rgb, t[..., 3:4] * (c[3] if "SpriteBlendDynamic" not in shader else 1)], axis=-1)
    if "ScreenIgnoreColor" in shader:
        return t
    if "SpriteBlendDynamic/Multiply" in shader or shader.endswith("/MultiplyBlend"):
        return np.concatenate([t[..., :3] * c[:3], t[..., 3:4]], axis=-1)
    if shader == "Sprites/Color/Overlay":
        a, b = t[..., :3], c[:3]
        rgb = np.where(t[..., 0:1] < 0.5, 2 * a * b, 1 - 2 * (1 - a) * (1 - b))
        return np.concatenate([rgb, t[..., 3:4] * c[3]], axis=-1)
    if "SpriteBlendDynamic/Overlay" in shader or shader.endswith("/OverlayBlend"):
        a, b = t[..., :3], c[:3]
        rgb = np.where(a < 0.5, 2 * a * b, 1 - 2 * (1 - a) * (1 - b))
        return np.concatenate([rgb, t[..., 3:4]], axis=-1)
    return t * c


def frag_out(name, t, c, p):
    """A pass program of the game's blend and mask shaders (console/xsdk/shaders), per pixel RGBA."""
    tc = t * c
    a = t[..., 3:4] * c[3]
    if name == "inv":
        return np.concatenate([tc[..., :3], 1 - tc[..., 3:4]], axis=-1)
    if name == "straight":
        return tc
    if name == "sq":
        return np.repeat(a * a, 4, axis=-1)
    if name == "a":
        return np.repeat(a, 4, axis=-1)
    if name == "clear":
        return np.concatenate([np.zeros_like(t[..., :3]), np.full_like(a, c[3])], axis=-1)
    if name == "screen":
        rgb = t[..., :3] + np.array(c[:3]) * c[3] * p[0] * (1 - t[..., :3])
        return np.concatenate([rgb * t[..., 3:4], t[..., 3:4]], axis=-1)
    if name == "mono":
        sh = lambda dx, dy: np.roll(np.roll(t, -dy, axis=0), dx, axis=1)   # neighbours (warped space: about one texel)
        m = np.maximum.reduce([sh(-1, -1), sh(1, 1), sh(-1, 1), sh(1, -1), t])
        edge = 1 - 2 * np.abs(m[..., 3:4] - 0.5)
        ratio = t[..., :3].max(-1, keepdims=True) / np.maximum(m[..., :3].max(-1, keepdims=True), 0.5)
        base = np.array(p[0:4], np.float32)
        mono = base + t[..., :3].min(-1, keepdims=True) * (1 - base)
        r = t + (mono - t) * p[4]
        r[..., 3:4] = r[..., 3:4] + (ratio * t[..., 3:4] - r[..., 3:4]) * p[4]
        o = (r + (t - r) * edge) * np.array(c)
        return np.concatenate([o[..., :3] * o[..., 3:4], o[..., 3:4]], axis=-1)
    if name in ("blendk", "blendm", "blendn"):
        s3 = t[..., :3]
        hi = (t[..., 0:1] >= 0.5).astype(np.float32)
        if name == "blendk":
            screen = 1 - a + a * np.array(c[:3]) * (1 - s3)
            hard = 1 - a + hi * 2 * a * (1 - s3)
            return np.concatenate([screen + (hard - screen) * p[0], np.ones_like(a)], axis=-1)
        hard = a * (s3 + (2 * s3 - 1 - s3) * hi)
        if name == "blendm":
            screen = a * np.array(c[:3]) * s3
            return np.concatenate([np.maximum(screen + (hard - screen) * p[0], 0), a], axis=-1)
        return np.concatenate([np.maximum(-hard, 0), np.zeros_like(a)], axis=-1)
    return np.concatenate([tc[..., :3] * tc[..., 3:4], tc[..., 3:4]], axis=-1)   # the sprite, premultiplied


# Blend states (console/xsdk/main.cpp): (colour src, dst, op, alpha src, dst, op), None no colour.
BLENDS = {
    "premul": ("one", "isa", "add", "one", "isa", "add"), "mask_write": ("zero", "one", "add", "one", "zero", "add"),
    "masked0": ("zero", "one", "add", "ida", "one", "add"), "masked1": ("ida", "da", "add", "zero", "one", "add"),
    "rev_da": ("da", "one", "rev", "zero", "one", "add"), "add_da": ("da", "one", "add", "zero", "one", "add"),
    "rev_ida": ("ida", "one", "rev", "zero", "one", "add"), "add_ida": ("ida", "one", "add", "zero", "one", "add"),
    "mul": ("zero", "sc", "add", "zero", "one", "add"), "add_lit": ("one", "one", "add", "sa", "isa", "add"),
    "rev_sub": ("one", "one", "rev", "zero", "one", "add"), "no_color": None,
}
# Items' modes 20-34 (Effects.cs): their passes (program, blend), as the console draws them.
PASSES = {
    20: [("inv", "mask_write")], 21: [("inv", "masked0"), ("straight", "masked1")],
    22: [("sq", "rev_ida"), ("sprite", "add_ida")], 23: [("sq", "rev_da"), ("sprite", "add_da")],
    24: [("sq", "rev_ida"), ("screen", "add_ida")], 25: [("sq", "rev_da"), ("screen", "add_da")],
    26: [("a", "rev_da"), ("sprite", "add_da")], 27: [("clear", "mask_write")],
    30: [], 32: [], 33: [("blendk", "mul"), ("blendm", "add_lit")],
    34: [("blendk", "mul"), ("blendm", "add_lit"), ("blendn", "rev_sub")],
    35: [("mono", "premul")],
}


def factor(f, src, dst):
    return {"zero": 0.0, "one": 1.0, "da": dst[..., 3:4], "ida": 1 - dst[..., 3:4], "sc": src[..., :3],
            "sa": src[..., 3:4], "isa": 1 - src[..., 3:4]}[f]


def blend_into(region, src, m, spec):
    """Unity's blend equation over a canvas region, where m (coverage) says."""
    if spec is None:
        return
    src = np.clip(src, 0, 1)
    sf, df, op, saf, daf, aop = spec
    def eq(s_, d_, f1, f2, o):
        return np.clip(s_ * f1 + d_ * f2 if o == "add" else d_ * f2 - s_ * f1, 0, 1)
    rgb = eq(src[..., :3], region[..., :3], factor(sf, src, region), factor(df, src, region), op)
    al = eq(src[..., 3:4], region[..., 3:4], factor(saf, src, region), factor(daf, src, region), aop)
    out = np.concatenate([rgb, al], axis=-1)
    region[...] = region + (out - region) * m


def draw_group(canvas, tex_img, pts, uvs, tris, tint, colors, view, H, sdf=None, shader="", ui=False, premul=False,
               passes=None, params=None):
    """premul: the texture is already the effect's output (premultiplied), drawn as it is; passes: the
    item's passes (program, blend) of the game's blend and mask shaders."""
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
        if passes is not None:
            region = canvas[y0:y0 + h, x0:x0 + w]
            for prog, bl in passes:
                blend_into(region, frag_out(prog, warped, col, params), m, BLENDS[bl])
            continue
        if premul:
            layer = warped.copy()
        elif sdf is not None:
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
            layer = blend_recipe(shader, warped, col, ui)
        if premul:
            layer *= m
        else:
            layer[..., 3:4] *= m
        composite(canvas, layer, x0, y0, premul)


STENCIL_CMP = {1: lambda r, s: np.zeros_like(s, bool), 2: lambda r, s: r < s, 3: lambda r, s: r == s, 4: lambda r, s: r <= s,
               5: lambda r, s: r > s, 6: lambda r, s: r != s, 7: lambda r, s: r >= s, 8: lambda r, s: np.ones_like(s, bool)}


def coverage(pts, tris, W, H):
    """Pixels the item's triangles cover (rows top-down), for stencil writes and tests."""
    from PIL import ImageDraw
    m = Image.new("L", (W, H), 0)
    d = ImageDraw.Draw(m)
    for i in range(0, len(tris) - 2, 3):
        tri = [(pts[k][0], H - pts[k][1]) for k in tris[i:i + 3] if k < len(pts)]
        if len(tri) == 3:
            d.polygon(tri, fill=255)
    return np.asarray(m) > 0


def sample(img, u, v):
    """Bilinear taps of a float (h, w, c) image (row 0 the top) at uv (v up), clamped at the edges."""
    h, w = img.shape[:2]
    x = np.clip(u * w - 0.5, 0, w - 1)
    y = np.clip((1 - v) * h - 0.5, 0, h - 1)
    x0 = np.floor(x).astype(np.int32)
    y0 = np.floor(y).astype(np.int32)
    x1 = np.minimum(x0 + 1, w - 1)
    y1 = np.minimum(y0 + 1, h - 1)
    fx = (x - x0)[..., None]
    fy = (y - y0)[..., None]
    top = img[y0, x0] * (1 - fx) + img[y0, x1] * fx
    bot = img[y1, x0] * (1 - fx) + img[y1, x1] * fx
    return top * (1 - fy) + bot * fy


def target_uv(w, h):
    """uv at the pixel centres of a w x h target (rows top-down, v up)."""
    u = (np.arange(w, dtype=np.float32) + 0.5) / w
    v = 1 - (np.arange(h, dtype=np.float32) + 0.5) / h
    return np.broadcast_to(u[None, :], (h, w)), np.broadcast_to(v[:, None], (h, w))


W7 = ((0, 0.324), (1, 0.232), (2, 0.0855), (3, 0.0205))


def blur7(src, u, v, du, dv):
    m = sample(src, u, v)
    rgb = m[..., :3] * 0.324
    for i, wgt in W7[1:]:
        rgb = rgb + (sample(src, u - i * du, v - i * dv)[..., :3] + sample(src, u + i * du, v + i * dv)[..., :3]) * wgt
    return rgb, m[..., 3:4]


def powc(x, g):
    return np.exp2(np.log2(np.maximum(x, 1e-6)) * g)


NO_POST = False


def blit(mode, p, src, w, h, overlay):
    """A blit's output (w x h RGBA), as the console's shaders (console/xsdk/shaders) compute it
    (--no-post: every blit a copy, for quick sheets)."""
    u, v = target_uv(w, h)
    if NO_POST:
        return sample(src, u, v)
    if mode == 11:
        return sample(src, u, v)
    if mode == 12:
        tx, ty = p[8], p[9]
        return (sample(src, u + tx, v + ty) + sample(src, u - 0.5 * tx, v - 0.5 * ty)
                + sample(src, u + 0.5 * tx, v - 0.5 * ty) + sample(src, u - 0.5 * tx, v + 0.5 * ty)) * 0.25
    if mode == 18:
        out = sample(src, u, v) * 0.324
        for i, wgt in W7[1:]:
            out = out + (sample(src, u - i * p[0], v - i * p[1]) + sample(src, u + i * p[0], v + i * p[1])) * wgt
        return out
    if mode in (13, 14, 15, 16):
        rgb, a = blur7(src, u, v, p[0], p[1])
        if mode == 14:
            rgb = powc(rgb, p[2])
        elif mode == 15:
            gb = (rgb[..., 1:2] + rgb[..., 2:3]) * 0.5
            g = (1 - 0.67 * (1 - gb)) * (1 - 0.34 * (1 - rgb[..., 0:1]))
            rgb = powc(np.concatenate([rgb[..., 0:1], g, gb], axis=-1) * 1.25 - 0.25, p[2])
        elif mode == 16:
            c = powc(rgb, p[2])
            x = np.maximum((c * np.array([0.2125, 0.7154, 0.0721], np.float32)).sum(-1, keepdims=True), c.sum(-1, keepdims=True) * 0.333) * 1.15 - 0.1
            rgb = np.repeat(powc(x, p[2] * 0.9), 3, axis=-1)
        return np.concatenate([rgb, a], axis=-1)
    if mode == 17:
        r = sample(src, u - p[0], v - p[1])[..., 0:1]
        g = sample(src, u - p[2], v - p[3])[..., 1:2]
        b = sample(src, u - p[4], v - p[5])[..., 2:3]
        if overlay is None:
            o = np.ones((h, w, 4), np.float32)
        else:
            o = sample(overlay, np.mod(u * p[6] + v * p[7], 1.0), np.mod(u * p[8] + v * p[9], 1.0))
        rgb = o[..., :3] * (np.concatenate([r, g, b], axis=-1) + 1 - o[..., 3:4])
        return np.concatenate([rgb, np.ones((h, w, 1), np.float32)], axis=-1)
    return sample(src, u, v)


def knight_glow(tex, p):
    """Sprites/KnightGlow over a whole render texture (float RGBA, row 0 the top): premultiplied."""
    h, w = tex.shape[:2]
    u, v = target_uv(w, h)
    du = np.clip(u * 4 - 2, -0.5, 0.5) * p[8] * p[6]
    dv = p[9] * p[7]
    n = np.zeros((h, w, 1), np.float32)
    for i in range(1, 6):
        n += (sample(tex[..., 3:4], u + i * du, v + i * dv) < 0.1).astype(np.float32)
    k = 1 - np.clip(n * p[5], 0, p[3])
    lit = np.concatenate([np.array(p[0:3], np.float32) * (1 - k), 1 + k * (p[4] - 1)], axis=-1)
    return np.where(tex[..., 3:4] >= 0.1, lit, tex)


def darkness(tex, p):
    a = tex[..., 0:1] * p[0]
    return np.concatenate([np.zeros_like(tex[..., :3]), a], axis=-1)


def blur_sprite(tex, p, tint):
    """Cuphead/Sprites/FastBlurSprites' five passes over a texture crop (float RGBA): premultiplied."""
    h, w = tex.shape[:2]
    u, v = target_uv(w, h)
    c = np.array(tint, np.float32)
    amount, lerp, cutoff, factor, tx, ty = p[0], p[1], p[2], p[3], p[8], p[9]
    t = min(max(amount / lerp, 0.0), 1.0) if lerp > 0 else (1.0 if amount > 0 else 0.0)
    center = sample(tex, u, v)
    avg = (sample(tex, u + tx, v + ty) + sample(tex, u - 0.5 * tx, v - 0.5 * ty)
           + sample(tex, u + 0.5 * tx, v - 0.5 * ty) + sample(tex, u - 0.5 * tx, v + 0.5 * ty)) * 0.25 * c
    r = center + (avg - center) * t
    acc = np.concatenate([r[..., :3] * r[..., 3:4], r[..., 3:4]], axis=-1)

    def pass7(du, dv):
        b = sample(tex, u, v) * 0.324
        for i, wgt in W7[1:]:
            b = b + (sample(tex, u - i * du, v - i * dv) + sample(tex, u + i * du, v + i * dv)) * wgt
        b = b * c
        low = np.all(b[..., :3] <= cutoff, axis=-1, keepdims=True).astype(np.float32)
        a = b[..., 3:4] * (1 - factor * low) * t
        return np.concatenate([b[..., :3] * a, a], axis=-1)
    vv = pass7(0.0, ty * amount)
    hh = pass7(tx * amount, 0.0)
    for layer in (vv, hh, hh, vv):
        acc = layer + (1 - layer[..., 3:4]) * acc
    return acc


def as_image(arr):
    return Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")


def as_array(img):
    return np.asarray(img.convert("RGBA"), dtype=np.float32) / 255.0


def render(frame, textures):
    W, H = frame["screen"]
    canvases = {None: np.concatenate([np.zeros((H, W, 3), np.float32), np.ones((H, W, 1), np.float32)], axis=-1)}
    sizes = {None: (W, H)}
    current = "start"
    for v in frame["views"]:
        tg = v.get("target")
        key = tg["rt"] if tg else None
        if tg:
            sizes[key] = tuple(tg["size"])
        TW, TH = sizes[key]
        if key != current:
            # A render texture's pass starts cleared (transparent), but where it goes on after a grab's
            # copy (resume); the screen keeps what it has.
            if key is not None and not (v.get("resume") and key in canvases):
                canvases[key] = np.zeros((TH, TW, 4), np.float32)
            current = key
        canvas = canvases[key]
        rect = v["rect"]
        stencil = np.zeros((TH, TW), dtype=np.uint8)   # each camera starts with a clear stencil (UGUI masks)
        if v["clear"] in (1, 2):
            bg = v.get("background", [0, 0, 0, 1])
            x0, y0, w, h = [int(round(c)) for c in rect]
            canvas[TH - y0 - h:TH - y0, x0:x0 + w] = list(bg[:3]) + [bg[3] if key is not None else 1.0]
        for it in v["items"]:
            tex = it.get("texture")
            mode = it.get("effect", -1)
            p = it.get("p", [0.0] * 12)
            if v.get("blit"):
                src = canvases.get(tex.get("rt")) if tex and "rt" in tex else (as_array(textures.get(tex)) if tex and "file" in tex else None)
                if src is None:
                    continue
                ov = textures.get(it["tex2"]) if it.get("tex2") else None
                canvases[key] = canvas = blit(mode, p, src, TW, TH, as_array(ov) if ov is not None else None)
                continue
            img = None
            if tex and "rt" in tex:
                rt = canvases.get(tex["rt"])
                img = as_image(rt) if rt is not None else None
            elif tex and "file" in tex:
                img = textures.get(tex)
            elif tex and "png" in tex:
                k = "png:" + tex["png"]
                if k not in textures.mem:
                    fn = os.path.join(os.path.dirname(capture_path), tex["png"])
                    textures.mem[k] = Image.open(fn).convert("RGBA") if os.path.exists(fn) else None
                img = textures.mem[k]
            pts = list(zip(it["xy"][0::2], it["xy"][1::2]))
            uvs = list(zip(it["uv"][0::2], it["uv"][1::2]))
            colors = None
            if "colors" in it:
                c = it["colors"]
                colors = [c[i:i + 4] for i in range(0, len(c), 4)]
            sdf = None
            if "Distance Field" in (it.get("shader") or ""):
                pr = it.get("props", {})
                face = np.array(pr.get("_FaceColor", [1, 1, 1, 1]), dtype=np.float32)
                outline = np.array(pr.get("_OutlineColor", [0, 0, 0, 1]), dtype=np.float32)
                sdf = (face, outline, float(pr.get("_OutlineWidth", 0)), float(pr.get("_FaceDilate", 0)))
            if img is None and tex is not None:
                continue
            premul = False
            if mode in (8, 9) and img is not None:
                arr = as_array(img)
                img = as_image(knight_glow(arr, p) if mode == 8 else darkness(arr, p))
                premul = True
            elif mode == 10 and img is not None and uvs:
                # The blur over the sprite's part of its page, a margin for its taps.
                tw_, th_ = img.size
                us = [q[0] for q in uvs]
                vs = [q[1] for q in uvs]
                m = int(4 + 3 * abs(p[0]) + 2)
                cx0 = max(0, int(min(us) * tw_) - m)
                cx1 = min(tw_, int(max(us) * tw_) + m + 1)
                cy0 = max(0, int((1 - max(vs)) * th_) - m)
                cy1 = min(th_, int((1 - min(vs)) * th_) + m + 1)
                crop = as_array(img.crop((cx0, cy0, cx1, cy1)))
                cw, ch = cx1 - cx0, cy1 - cy0
                pc = list(p)
                pc[8], pc[9] = p[8] * tw_ / cw, p[9] * th_ / ch
                img = as_image(blur_sprite(crop, pc, it["tint"]))
                uvs = [((q[0] * tw_ - cx0) / cw, 1 - ((1 - q[1]) * th_ - cy0) / ch) for q in uvs]
                premul = True
            st = it.get("stencil")
            ui = it.get("kind") == "ui"
            ss = it.get("spriteStencil", 0)
            args = (pts, uvs, it["tris"], it["tint"], colors, rect, TH, sdf, it.get("shader") or "", ui, premul)
            if mode in PASSES:
                draw_group(canvas, img, *args, passes=PASSES[mode], params=p)
                continue
            if mode in (30, 31, 32):
                # The Rum Runners' banded stencil masks: 9-14 by the mask's alpha band, 0 by the clearer; the
                # reversed masked sprite where the stencil is under 9 (its faint passes in the bands left out here).
                if mode == 31:
                    before = canvas.copy()
                    draw_group(canvas, img, *args)
                    canvas[:] = np.where((stencil < 9)[..., None], canvas, before)
                else:
                    scratch = np.zeros_like(canvas)
                    draw_group(scratch, img, pts, uvs, it["tris"], it["tint"], colors, rect, TH)
                    a_ = scratch[..., 3]
                    if mode == 32:
                        stencil[coverage(pts, it["tris"], TW, TH)] = 0
                    else:
                        for b_ in range(6):
                            lo, hi = (0.4 + 0.1 * b_, 0.5 + 0.1 * b_) if b_ < 5 else (0.8999, 2.0)
                            stencil[(a_ > lo) & (a_ <= hi)] = 9 + b_
                continue
            if mode in (28, 29):
                # The game's stencil masks: 5 written where the mask's alpha is half or more; drawn where it is 5.
                if mode == 28:
                    scratch = np.zeros_like(canvas)
                    draw_group(scratch, img, pts, uvs, it["tris"], it["tint"], colors, rect, TH)
                    stencil[scratch[..., 3] >= 0.5] = 5
                else:
                    before = canvas.copy()
                    draw_group(canvas, img, *args)
                    canvas[:] = np.where((stencil == 5)[..., None], canvas, before)
                continue
            if ss in (1, 2):
                # A sprite mask: its sprite's pixels with alpha at or above the cutoff (the tint's alpha)
                # add one to the stencil or take one away; nothing is drawn.
                scratch = np.zeros_like(canvas)
                draw_group(scratch, img, pts, uvs, it["tris"], [1, 1, 1, 1], None, rect, TH)
                hit = scratch[..., 3] >= max(it["tint"][3], 1e-3) - 1e-3
                cur = stencil[hit].astype(np.int32)
                stencil[hit] = (np.minimum(cur + 1, 255) if ss == 1 else np.maximum(cur - 1, 0)).astype(np.uint8)
                continue
            if ss in (3, 4):
                passed = stencil >= 1 if ss == 3 else stencil == 0
                before = canvas.copy()
                draw_group(canvas, img, *args)
                canvas[:] = np.where(passed[..., None], canvas, before)
                continue
            if st is None:
                draw_group(canvas, img, *args)
                continue
            # ref, comp, op, readMask, writeMask, colorMask (Unity codes).
            ref, comp, op, rmask, wmask, cmask = st
            passed = STENCIL_CMP.get(comp, STENCIL_CMP[8])(np.uint8(ref & rmask), stencil & rmask)
            if cmask:
                before = canvas.copy()
                draw_group(canvas, img, *args)
                canvas[:] = np.where(passed[..., None], canvas, before)
            if op:
                hit = passed & coverage(pts, it["tris"], TW, TH)
                cur = stencil[hit].astype(np.int32)
                new = {1: 0 * cur, 2: np.full_like(cur, ref), 3: np.minimum(cur + 1, 255), 4: np.maximum(cur - 1, 0),
                       5: 255 - cur, 6: (cur + 1) & 255, 7: (cur - 1) & 255}.get(op, cur)
                stencil[hit] = ((cur & ~wmask) | (new & wmask)).astype(np.uint8)
    return Image.fromarray((np.clip(canvases[None][..., :3], 0, 1) * 255 + 0.5).astype(np.uint8), "RGB")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--capture", default=os.path.join(ROOT, "work", "preview", "capture.jsonl"))
    ap.add_argument("--game", default=os.path.join(ROOT, "game"))
    ap.add_argument("--out", default=os.path.join(ROOT, "work", "preview"))
    ap.add_argument("--frames", help="comma-separated frame numbers (default: all captured)")
    ap.add_argument("--sheet", action="store_true", help="also write sheet.png with every frame at quarter size")
    ap.add_argument("--no-post", action="store_true", help="blits as plain copies (no blur, film grain or fringes): quicker")
    a = ap.parse_args()
    global NO_POST
    NO_POST = a.no_post
    if os.path.abspath(a.out).startswith(os.path.abspath(a.game)):
        sys.exit("--out must be outside the game folder")
    global capture_path
    capture_path = a.capture
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
