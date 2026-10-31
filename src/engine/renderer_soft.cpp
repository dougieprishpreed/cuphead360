#include "engine/renderer_soft.h"
#include "core/endian.h"
#include "core/file.h"
#include "core/log.h"
#include "core/mem.h"

#include <math.h>
#include <string.h>

namespace ch360 {

static u32 PackRGBA(f32 r, f32 g, f32 b, f32 a) {
    u32 ir = (u32)(r < 0 ? 0 : (r > 1 ? 255 : r * 255.0f));
    u32 ig = (u32)(g < 0 ? 0 : (g > 1 ? 255 : g * 255.0f));
    u32 ib = (u32)(b < 0 ? 0 : (b > 1 ? 255 : b * 255.0f));
    u32 ia = (u32)(a < 0 ? 0 : (a > 1 ? 255 : a * 255.0f));
    return (ia << 24) | (ir << 16) | (ig << 8) | ib; // RGBA in memory; BMP converts
}

SoftRenderer::SoftRenderer(u32 width, u32 height)
    : width_(width), height_(height), pixels_(0) {
    pixels_ = (u32*)MemAlloc(width_ * height_ * 4);
    if (pixels_) memset(pixels_, 0, width_ * height_ * 4);
}

SoftRenderer::~SoftRenderer() {
    MemFree(pixels_);
}

void SoftRenderer::Clear(f32 r, f32 g, f32 b, f32 a) {
    if (!pixels_) return;
    u32 color = PackRGBA(r, g, b, a);
    for (u32 i = 0; i < width_ * height_; ++i) pixels_[i] = color;
}

void SoftRenderer::FillRect(s32 x, s32 y, s32 w, s32 h, u32 rgba) {
    if (!pixels_ || w <= 0 || h <= 0) return;
    s32 right = x + w;
    s32 bottom = y + h;
    if (x < 0) x = 0;
    if (y < 0) y = 0;
    if (right > (s32)width_) right = (s32)width_;
    if (bottom > (s32)height_) bottom = (s32)height_;
    for (s32 py = y; py < bottom; ++py) {
        u32* row = pixels_ + py * width_;
        for (s32 px = x; px < right; ++px) row[px] = rgba;
    }
}

void SoftRenderer::DrawHealthHud(s32 health, s32 health_max) {
    if (health_max <= 0 || health_max > 9) health_max = 3;
    if (health < 0) health = 0;
    const s32 x = 24, y = 24, slot_w = 44, slot_h = 24, gap = 8;
    FillRect(x - 4, y - 4, health_max * (slot_w + gap) + 4, slot_h + 8,
             PackRGBA(0.04f, 0.04f, 0.05f, 0.82f));
    for (s32 i = 0; i < health_max; ++i) {
        s32 sx = x + i * (slot_w + gap);
        FillRect(sx, y, slot_w, slot_h, PackRGBA(0.95f, 0.93f, 0.88f, 1.0f));
        FillRect(sx + 3, y + 3, slot_w - 6, slot_h - 6,
                 i < health ? PackRGBA(0.88f, 0.08f, 0.06f, 1.0f)
                            : PackRGBA(0.18f, 0.06f, 0.06f, 1.0f));
    }
}

static bool CompareDraw(const SoftRenderer::DrawItem& a, const SoftRenderer::DrawItem& b) {
    if (a.layer != b.layer) return a.layer < b.layer;
    if (a.order != b.order) return a.order < b.order;
    return a.object_index < b.object_index;
}

void SoftRenderer::DrawSprite(const SceneSprite& sprite, const SceneTexture& texture,
                              f32 x, f32 y, f32 w, f32 h, const f32 color[4],
                              bool flip_x, bool flip_y) {
    (void)sprite;
    if (!pixels_ || !texture.pixels || !texture.width || !texture.height) return;
    s32 left = (s32)floorf(x + 0.5f);
    s32 top = (s32)floorf(y + 0.5f);
    s32 dw = (s32)floorf(w + 0.5f);
    s32 dh = (s32)floorf(h + 0.5f);
    if (dw <= 0 || dh <= 0) return;
    if (left >= (s32)width_ || top >= (s32)height_ || left + dw <= 0 || top + dh <= 0) return;

    f32 cr = color[0], cg = color[1], cb = color[2], ca = color[3];
    const u8* src = texture.pixels;
    u32 sw = texture.width;
    u32 sh = texture.height;
    for (s32 dy = 0; dy < dh; ++dy) {
        s32 sy = top + dy;
        if (sy < 0 || sy >= (s32)height_) continue;
        u32 v = (u32)((dy * (s32)sh) / dh);
        if (v >= sh) v = sh - 1;
        if (flip_y) v = sh - 1 - v;
        for (s32 dx = 0; dx < dw; ++dx) {
            s32 sx = left + dx;
            if (sx < 0 || sx >= (s32)width_) continue;
            u32 u = (u32)((dx * (s32)sw) / dw);
            if (u >= sw) u = sw - 1;
            if (flip_x) u = sw - 1 - u;
            const u8* p = src + (v * sw + u) * 4;
            f32 sa = (p[3] / 255.0f) * ca;
            if (sa <= 0.0f) continue;
            u32* dst = &pixels_[sy * width_ + sx];
            u32 d = *dst;
            f32 dr = ((d >> 16) & 0xFF) / 255.0f;
            f32 dg = ((d >> 8) & 0xFF) / 255.0f;
            f32 db = (d & 0xFF) / 255.0f;
            f32 da = ((d >> 24) & 0xFF) / 255.0f;
            f32 sr = (p[0] / 255.0f) * cr;
            f32 sg = (p[1] / 255.0f) * cg;
            f32 sb = (p[2] / 255.0f) * cb;
            f32 outa = sa + da * (1.0f - sa);
            f32 outr = (sr * sa + dr * da * (1.0f - sa)) / (outa > 0 ? outa : 1.0f);
            f32 outg = (sg * sa + dg * da * (1.0f - sa)) / (outa > 0 ? outa : 1.0f);
            f32 outb = (sb * sa + db * da * (1.0f - sa)) / (outa > 0 ? outa : 1.0f);
            *dst = PackRGBA(outr, outg, outb, outa);
        }
    }
}

void SoftRenderer::DrawScene(const Scene& scene) {
    if (!pixels_) return;
    const SceneCamera* cam = scene.MainCamera();
    if (cam) {
        Clear(cam->background[0], cam->background[1], cam->background[2], cam->background[3]);
    } else {
        Clear(0.0f, 0.0f, 0.0f, 1.0f);
    }
    f32 cam_x = 0.0f, cam_y = 0.0f, ortho = 360.0f;
    if (cam) {
        cam_x = cam->position[0];
        cam_y = cam->position[1];
        ortho = cam->orthographic_size > 1.0f ? cam->orthographic_size : 360.0f;
        // The camera is attached to a GameObject; use its world transform if
        // the scene loader can resolve it.
        for (u32 i = 0; i < scene.ObjectCount(); ++i) {
            const SceneObject& o = scene.Object(i);
            if (o.id == cam->go_id) {
                cam_x = o.world_x;
                cam_y = o.world_y;
                break;
            }
        }
    }
    f32 ppw = (f32)height_ / (2.0f * ortho);

    Buffer<DrawItem> draws;
    for (u32 i = 0; i < scene.ObjectCount(); ++i) {
        const SceneObject& o = scene.Object(i);
        if (!(o.flags & 1)) continue;
        for (u32 c = 0; c < o.component_count; ++c) {
            u32 ci = o.component_start + c;
            if (ci >= scene.ComponentCount()) break;
            const SceneComponent& comp = scene.Component(ci);
            if (comp.type != 1 || !(comp.flags & 1)) continue;
            if (comp.sprite_id == 0) continue;
            DrawItem d;
            d.layer = comp.sorting_layer;
            d.order = comp.sorting_order;
            d.object_index = i;
            d.component_index = ci;
            draws.PushBack(d);
        }
    }
    // Simple insertion sort (draw counts are small and this avoids a
    // dependency on a comparator function pointer in C++03).
    for (u32 i = 1; i < draws.Size(); ++i) {
        DrawItem key = draws[i];
        u32 j = i;
        while (j > 0 && CompareDraw(key, draws[j - 1])) {
            draws[j] = draws[j - 1];
            --j;
        }
        draws[j] = key;
    }

    for (u32 i = 0; i < draws.Size(); ++i) {
        const SceneObject& o = scene.Object(draws[i].object_index);
        const SceneComponent& comp = scene.Component(draws[i].component_index);
        const SceneSprite& sp = scene.Sprite(comp.sprite_id);
        const SceneTexture& tex = scene.Texture(sp.texture_id);
        if (!tex.pixels) {
            LogWarn("render", "missing texture for sprite %s", sp.name);
            continue;
        }
        f32 ww = sp.world_width * o.world_scale_x;
        f32 wh = sp.world_height * o.world_scale_y;
        f32 wx = o.world_x - sp.pivot[0] * ww;
        f32 wy = o.world_y - sp.pivot[1] * wh;
        f32 sx = (wx - cam_x) * ppw + width_ * 0.5f;
        f32 sy_bottom = height_ * 0.5f - (wy - cam_y) * ppw;
        f32 sw = ww * ppw;
        f32 sh = wh * ppw;
        f32 sy = sy_bottom - sh;
        DrawSprite(sp, tex, sx, sy, sw, sh, comp.color,
                   (comp.flags & 2) != 0, (comp.flags & 4) != 0);
    }
}

bool SoftRenderer::SaveBmp(const char* path) const {
    if (!pixels_) return false;
    u32 row_bytes = width_ * 4;
    u32 image_size = row_bytes * height_;
    u32 file_size = 14 + 40 + image_size;
    u8* out = (u8*)MemAlloc(file_size);
    if (!out) return false;
    out[0] = 'B'; out[1] = 'M';
    WriteU32LE(out + 2, file_size);
    WriteU16LE(out + 6, 0);
    WriteU16LE(out + 8, 0);
    WriteU32LE(out + 10, 54);
    WriteU32LE(out + 14, 40);
    WriteU32LE(out + 18, width_);
    WriteU32LE(out + 22, height_);
    WriteU16LE(out + 26, 1);
    WriteU16LE(out + 28, 32);
    WriteU32LE(out + 30, 0);
    WriteU32LE(out + 34, image_size);
    WriteU32LE(out + 38, 2835);
    WriteU32LE(out + 42, 2835);
    WriteU32LE(out + 46, 0);
    WriteU32LE(out + 50, 0);
    for (u32 y = 0; y < height_; ++y) {
        u32 src_y = height_ - 1 - y;
        const u32* src = pixels_ + src_y * width_;
        u8* dst = out + 54 + y * row_bytes;
        for (u32 x = 0; x < width_; ++x) {
            u32 p = src[x];
            dst[x * 4 + 0] = (u8)(p & 0xFF);        // B
            dst[x * 4 + 1] = (u8)((p >> 8) & 0xFF); // G
            dst[x * 4 + 2] = (u8)((p >> 16) & 0xFF);// R
            dst[x * 4 + 3] = (u8)((p >> 24) & 0xFF);// A
        }
    }
    bool ok = FileWriteAll(path, out, file_size);
    MemFree(out);
    return ok;
}

} // namespace ch360
